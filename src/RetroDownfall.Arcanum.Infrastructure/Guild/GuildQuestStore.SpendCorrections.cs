using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal async Task<Result<GuildSpendCorrectionResultDto>> CorrectOperatorSpendAsync(GuildCallerContext caller, Guid questId,
        Guid effectId, GuildSpendCorrectionRequest request, CancellationToken ct)
    {
        Result valid = GuildSpendCorrectionPolicy.ValidateOperatorRequest(request);

        if (valid.IsFailure) return valid.Error;

        if (caller is null || caller.Kind != GuildCallerKind.Operator || questId == Guid.Empty || effectId == Guid.Empty
            || allocations is not GuildBudgetLedger ledger) return OwnerUnavailable;

        Result<GuildQuestStored> located = await GetAsync(caller, questId, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        Guid root = located.Value.Quest.RootQuestId;

        return await authority.InQuestTransactionAsync<GuildSpendCorrectionResultDto>(root, async (connection, transaction, _, token) =>
        {
            Result<GuildQuestStored> found = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

            if (found.IsFailure) return found.Error;

            if (found.Value.Quest.RootQuestId != root) return OwnerUnavailable;

            Result visible = await RequireVisibleAsync(connection, transaction, caller, found.Value, token).ConfigureAwait(false);

            if (visible.IsFailure) return visible.Error;

            await using (SqliteCommand epoch = Command(connection, transaction, """
                SELECT COUNT(*) FROM guild_signets s JOIN guild_signet_authority_state a ON a.StateKey=1 AND a.Epoch=s.AuthorityEpoch
                WHERE s.SignetId=$signet AND s.QuestId=$quest AND s.RootQuestId=$root AND s.InstallationGeneration=$generation
                """))
            {
                Add(epoch, "$signet", Id(found.Value.Quest.SignetId));

                Add(epoch, "$quest", Id(questId));

                Add(epoch, "$root", Id(root));

                Add(epoch, "$generation", Id(caller.InstallationGeneration));

                if (!Equals(1L, await epoch.ExecuteScalarAsync(token).ConfigureAwait(false))) return OwnerUnavailable;
            }

            Result<GuildBudgetEffect> effect = await ledger.ReadEffectWithinAsync(connection, transaction, effectId, token).ConfigureAwait(false);

            if (effect.IsFailure) return effect.Error;

            if (effect.Value.Request.QuestId != questId || effect.Value.Request.Kind != GuildBudgetEffectKind.LocalProvider) return OwnerUnavailable;

            return await ApplySpendCorrectionWithinAsync(connection, transaction, ledger, found.Value, effect.Value, caller, request, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private async Task<Result<GuildSpendCorrectionResultDto>> ApplySpendCorrectionWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildBudgetLedger ledger, GuildQuestStored quest, GuildBudgetEffect effect,
        GuildCallerContext caller, GuildSpendCorrectionRequest request, CancellationToken ct)
    {
        GuildBudgetEffectRequest binding = effect.Request;

        string requestIdentity = Id(request.IdempotencyKey);

        GuildSpendAmountsRequest amounts = request.Amounts!;

        byte[] digest = GuildSpendReceiptIdentity.OperatorDigest(caller, binding, request);

        return await ApplySpendWithinAsync(connection, transaction, ledger, quest, effect, amounts,
            new(GuildSpendOrigin.OperatorAttested, requestIdentity, digest, request.EvidenceDigest!.ToUpperInvariant(), request.ExpectedRevision), ct).ConfigureAwait(false);
    }

    // Only authenticated ingress may reach this common transaction-owned accounting path.
    private sealed record SpendCorrectionIdentity(GuildSpendOrigin Origin, string RequestIdentity, byte[] RequestDigest,
        string? EvidenceDigest, long? ExpectedRevision);

    private async Task<Result<GuildSpendCorrectionResultDto>> ApplySpendWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildBudgetLedger ledger, GuildQuestStored quest, GuildBudgetEffect effect,
        GuildSpendAmountsRequest amounts, SpendCorrectionIdentity identity, CancellationToken ct)
    {
        GuildBudgetEffectRequest binding = effect.Request;

        await using (SqliteCommand prior = Command(connection, transaction, """
            SELECT r.EffectId,r.RequestDigest,CASE WHEN typeof(r.OriginalResult)='blob' AND length(r.OriginalResult)<=4096 THEN r.OriginalResult END,
                r.OriginalEventSequence,r.ReceiptId,r.EvidenceRevision,r.AcceptedAtUtc,e.KnownUsd,e.KnownTokens,e.UnknownUsd,e.UnknownTokens,e.Complete,e.NoDispatchProven,e.Digest
            FROM guild_spend_receipts r JOIN guild_budget_evidence e ON e.ReceiptId=r.EvidenceReceiptId
                AND e.EffectId=r.EffectId AND e.AppliedRevision=r.EvidenceRevision
            WHERE r.OriginCode=$origin AND r.RequestIdentity=$identity
            """))
        {
            Add(prior, "$origin", (int)identity.Origin);

            Add(prior, "$identity", identity.RequestIdentity);

            await using SqliteDataReader reader = await prior.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.GetString(0) != Id(binding.EffectId) || reader.GetValue(1) is not byte[] savedDigest
                    || !CryptographicOperations.FixedTimeEquals(identity.RequestDigest, savedDigest)
                    || !SpendDigestsMatch(reader, 1, 13)) return Conflict;

                if (reader.IsDBNull(2) || !UtcInstantText.TryParse(reader.GetString(6), out DateTimeOffset acceptedAt)) return Conflict;

                GuildSpendReceiptDto? saved;

                try { saved = JsonSerializer.Deserialize((byte[])reader.GetValue(2), GuildSpendStorageJsonContext.Default.GuildSpendReceiptDto); }
                catch (JsonException) { return Conflict; }

                if (!ValidSpendReceipt(saved) || saved!.QuestId != quest.Quest.Id || saved.EffectId != binding.EffectId
                    || saved.Origin != identity.Origin || Id(saved.ReceiptId) != reader.GetString(4) || saved.EvidenceRevision != reader.GetInt64(5)
                    || saved.EventSequence != reader.GetInt64(3) || saved.AcceptedAt != acceptedAt || !ReceiptMatchesEvidence(reader, 7, saved)
                    || saved.EvidenceDigest != identity.EvidenceDigest || saved.Facts.KnownUsd != amounts.Usd
                    || saved.Facts.LocalTokens is not { } savedLocal || savedLocal.InputTokens != amounts.InputTokens
                    || savedLocal.OutputTokens != amounts.OutputTokens || savedLocal.CachedTokens != amounts.CachedTokens
                    || savedLocal.ReasoningTokens != amounts.ReasoningTokens
                    || !((byte[])reader.GetValue(2)).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(saved, GuildSpendStorageJsonContext.Default.GuildSpendReceiptDto))) return Conflict;

                return new GuildSpendCorrectionResultDto(saved, true);
            }
        }

        if (identity.ExpectedRevision is { } expected && effect.Revision != expected) return RevisionConflict;

        Result<GuildStoredInitialSpend?> initial = await GuildSpendSourceStore.LoadInitialWithinAsync(connection, transaction, binding.EffectId, ct).ConfigureAwait(false);

        if (initial.IsFailure) return initial.Error;

        // Find the original bill before deciding whether a legacy snapshot is unavailable.
        Result<GuildSpendBill?> readBill = await ReadCorrectionBillWithinAsync(connection, transaction, binding, ct).ConfigureAwait(false);

        if (readBill.IsFailure) return readBill.Error;

        GuildSpendBill? bill = readBill.Value;

        if (bill is null) return new Error(ErrorCodes.Guild.SourceExpired, "The physical billing source is unavailable.");

        if (initial.Value is null) return new Error(ErrorCodes.Guild.InitialEvidenceUnavailable, "The original billing snapshot is unavailable.");

        GuildInitialBillSnapshot snapshot = initial.Value.Snapshot;

        if (initial.Value.Outcome.Request != binding || snapshot.EffectId != binding.EffectId || snapshot.QuestId != binding.QuestId || snapshot.DispatchId != binding.DispatchId
            || snapshot.SourceId != binding.SourceId || snapshot.RunId != binding.AccountingAssociationId || !bill.Matches(snapshot)
            || bill.Usd != effect.KnownUsd || checked(bill.Input + bill.Output) != effect.KnownTokens) return Conflict;

        GuildSpendLocalTokensDto proved;

        if (effect.Revision == initial.Value.Outcome.Revision)
        {
            if (effect.KnownUsd != initial.Value.Outcome.KnownUsd || effect.KnownTokens != initial.Value.Outcome.KnownTokens
                || effect.UnknownUsd != initial.Value.Outcome.UnknownUsd || effect.UnknownTokens != initial.Value.Outcome.UnknownTokens
                || effect.State != initial.Value.Outcome.State) return Conflict;

            proved = new(snapshot.InputTokens, snapshot.OutputTokens, snapshot.CachedTokens, snapshot.ReasoningTokens);
        }
        else
        {
            await using SqliteCommand latest = Command(connection, transaction,
                "SELECT ReceiptId FROM guild_spend_receipts WHERE EffectId=$effect AND EvidenceRevision=$revision");
            Add(latest, "$effect", Id(binding.EffectId));

            Add(latest, "$revision", effect.Revision);
            if (await latest.ExecuteScalarAsync(ct).ConfigureAwait(false) is not string raw
                || !Guid.TryParseExact(raw, "D", out Guid priorId) || priorId == Guid.Empty) return Conflict;

            Result<GuildSpendReceiptDto> prior = await ReadSpendReceiptWithinAsync(connection, transaction,
                quest.Quest.Id, binding.EffectId, priorId, ct).ConfigureAwait(false);
            if (prior.IsFailure) return prior.Error;
            if (prior.Value.Facts is not { LocalTokens: { } local } priorFacts || priorFacts.KnownUsd != effect.KnownUsd
                || priorFacts.KnownTokens != effect.KnownTokens || priorFacts.UnknownUsd != effect.UnknownUsd
                || priorFacts.UnknownTokens != effect.UnknownTokens || priorFacts.Complete != (effect.State == GuildBudgetEffectState.Complete)) return Conflict;
            proved = local;
        }

        if (bill.Input != proved.InputTokens || bill.Output != proved.OutputTokens || bill.Cached != proved.CachedTokens
            || bill.Reasoning != proved.ReasoningTokens) return Conflict;

        await using (SqliteCommand dispatch = Command(connection, transaction, "SELECT COUNT(*) FROM guild_dispatches WHERE DispatchId=$dispatch AND QuestId=$quest AND AccountingAssociationId=$run"))
        {
            Add(dispatch, "$dispatch", Id(binding.DispatchId));

            Add(dispatch, "$quest", Id(binding.QuestId));

            Add(dispatch, "$run", Id(binding.AccountingAssociationId));

            if (!Equals(1L, await dispatch.ExecuteScalarAsync(ct).ConfigureAwait(false))) return Conflict;
        }

        GuildSpendFacts current = new(bill.Input, bill.Output, bill.Cached, bill.Reasoning, bill.Usd,
            effect.UnknownUsd, effect.UnknownTokens, effect.State == GuildBudgetEffectState.Complete);

        Result<GuildSpendFacts> accepted = GuildSpendCorrectionPolicy.Validate(current, amounts);

        if (accepted.IsFailure) return accepted.Error;

        GuildSpendFacts facts = accepted.Value;

        Guid receiptId = Guid.NewGuid();

        await using (SqliteCommand update = Command(connection, transaction, """
            UPDATE BillableOperations SET InputTokens=$input,OutputTokens=$output,CachedTokens=$cached,ReasoningTokens=$reasoning,ActualCostUsd=$usd
            WHERE Id=$id
            """))
        {
            Add(update, "$input", facts.InputTokens);

            Add(update, "$output", facts.OutputTokens);

            Add(update, "$cached", facts.CachedTokens);

            Add(update, "$reasoning", facts.ReasoningTokens);

            Add(update, "$usd", ExactUsdText.Format(facts.Usd));

            Add(update, "$id", bill.Id);

            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Conflict;
        }

        GuildBudgetEvidence evidence = new(receiptId, Convert.ToHexString(identity.RequestDigest), facts.Usd, checked(facts.InputTokens + facts.OutputTokens),
            facts.UnknownUsd, facts.UnknownTokens, facts.Complete);

        Result<GuildBudgetEffect> applied = await ledger.ApplyEvidenceWithinAsync(connection, transaction, binding.EffectId, evidence, ct).ConfigureAwait(false);

        if (applied.IsFailure) return applied.Error;

        Result daily = await ledger.CorrectDailyWithinAsync(connection, transaction, effect, facts.Usd, ct).ConfigureAwait(false);

        if (daily.IsFailure) return daily.Error;

        if (quest.Quest.Revision == long.MaxValue) return ExhaustedReceipt;

        DateTimeOffset now = time.GetUtcNow();

        await using (SqliteCommand update = Command(connection, transaction, "UPDATE guild_quests SET Revision=Revision+1,UpdatedAtUtc=$now WHERE Id=$quest AND Revision=$revision"))
        {
            Add(update, "$now", UtcInstantText.Format(now));

            Add(update, "$quest", Id(quest.Quest.Id));

            Add(update, "$revision", quest.Quest.Revision);

            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return RevisionConflict;
        }

        Result<long> projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, quest.Quest.RootQuestId,
            quest.Quest.Id, GuildEventKind.SpendChanged, quest.Quest.Id, quest.Quest.Revision + 1, null, now, ct,
            spend: new(binding.EffectId, receiptId, applied.Value.Revision, identity.Origin)).ConfigureAwait(false);

        if (projected.IsFailure) return projected.Error;

        GuildSpendReceiptDto receipt = new(receiptId, quest.Quest.Id, binding.EffectId, identity.Origin,
            identity.EvidenceDigest, new(facts.Usd, evidence.KnownTokens, facts.UnknownUsd, facts.UnknownTokens, facts.Complete,
                new(facts.InputTokens, facts.OutputTokens, facts.CachedTokens, facts.ReasoningTokens)), applied.Value.Revision, projected.Value, now);

        await using SqliteCommand save = Command(connection, transaction, """
            INSERT INTO guild_spend_receipts(ReceiptId,EffectId,EvidenceReceiptId,EvidenceRevision,OriginCode,RequestIdentity,
                RequestDigest,OriginalResult,OriginalEventSequence,AcceptedAtUtc)
            VALUES($receipt,$effect,$receipt,$revision,$origin,$identity,$digest,$result,$sequence,$now)
            """);

        Add(save, "$receipt", Id(receiptId));

        Add(save, "$effect", Id(binding.EffectId));

        Add(save, "$revision", applied.Value.Revision);

        Add(save, "$origin", (int)identity.Origin);

        Add(save, "$identity", identity.RequestIdentity);

        Add(save, "$digest", identity.RequestDigest);

        Add(save, "$result", JsonSerializer.SerializeToUtf8Bytes(receipt, GuildSpendStorageJsonContext.Default.GuildSpendReceiptDto));

        Add(save, "$sequence", projected.Value);

        Add(save, "$now", UtcInstantText.Format(now));

        if (await save.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return Conflict;

        return new GuildSpendCorrectionResultDto(receipt, false);
    }

    private static async Task<Result<GuildSpendBill?>> ReadCorrectionBillWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildBudgetEffectRequest binding, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT Id,RunId,OperationType,Provider,Model,Purpose,StartedAt,CompletedAt,InputTokens,OutputTokens,
                ReasoningTokens,CachedTokens,PricingSnapshotJson,Status,ProviderRequestId,ActualCostUsd FROM BillableOperations
            WHERE Id=$compact COLLATE NOCASE OR Id=$dashed COLLATE NOCASE
            """);

        Add(command, "$compact", binding.SourceId.ToString("N"));

        Add(command, "$dashed", binding.SourceId.ToString("D"));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Result<GuildSpendBill?>.Success(null);

        if (reader.GetValue(2) is not long || reader.GetValue(8) is not long || reader.GetValue(9) is not long
            || reader.GetValue(10) is not long || reader.GetValue(11) is not long || reader.GetValue(13) is not long) return Conflict;

        GuildSpendBill bill = new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), UtcInstantText.Parse(reader.GetString(6)), UtcInstantText.Parse(reader.GetString(7)),
            reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), reader.GetString(12),
            reader.GetInt32(13), reader.IsDBNull(14) ? null : reader.GetString(14), ExactUsdText.Read(reader, 15));

        return await reader.ReadAsync(ct).ConfigureAwait(false) ? Result<GuildSpendBill?>.Failure(Conflict) : Result<GuildSpendBill?>.Success(bill);
    }

    private sealed record GuildSpendBill(string Id, string Run, int Type, string Provider, string Model, string Purpose,
        DateTimeOffset Started, DateTimeOffset Completed, long Input, long Output, long Reasoning, long Cached,
        string Pricing, int Status, string? ProviderRequestId, decimal Usd)
    {
        internal bool Matches(GuildInitialBillSnapshot initial) => Guid.TryParse(Run, out Guid run) && run == initial.RunId
            && Type == (int)initial.OperationType && Provider == initial.Provider && Model == initial.Model && Purpose == initial.Purpose
            && Started == initial.StartedAt && Completed == initial.CompletedAt && Pricing == initial.PricingSnapshotJson
            && Status == (int)initial.Status && ProviderRequestId == initial.ProviderRequestId;
    }
}
