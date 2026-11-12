using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal Task<Result<GuildSpendEffectDto>> ReadOperatorSpendEffectAsync(GuildCallerContext caller, Guid questId, Guid effectId, CancellationToken ct) =>
        WithSpendOwnerAsync(caller, questId, (connection, transaction, quest, ledger, token) =>
            ReadSpendEffectWithinAsync(connection, transaction, quest, ledger, effectId, token), ct);

    internal Task<Result<GuildSpendReceiptDto>> ReadOperatorSpendReceiptAsync(GuildCallerContext caller, Guid questId, Guid effectId, Guid receiptId, CancellationToken ct) =>
        WithSpendOwnerAsync(caller, questId, async (connection, transaction, quest, ledger, token) =>
        {
            Result<GuildBudgetEffect> effect = await ledger.ReadEffectWithinAsync(connection, transaction, effectId, token).ConfigureAwait(false);

            if (effect.IsFailure) return effect.Error;

            if (effect.Value.Request.QuestId != quest.Quest.Id) return OwnerUnavailable;

            return await ReadSpendReceiptWithinAsync(connection, transaction, quest.Quest.Id, effectId, receiptId, token).ConfigureAwait(false);
        }, ct);

    internal Task<Result<GuildSpendEffectPageDto>> ListOperatorSpendEffectsAsync(GuildCallerContext caller, Guid questId, string? cursor, int pageSize, CancellationToken ct) =>
        WithSpendOwnerAsync<GuildSpendEffectPageDto>(caller, questId, async (connection, transaction, quest, ledger, token) =>
        {
            Result<SpendPage<GuildSpendEffectDto>> page = await SpendPageWithinAsync(connection, transaction, caller, quest,
                4, questId, cursor, pageSize, (id, cancellation) => ReadSpendEffectWithinAsync(connection, transaction, quest, ledger, id, cancellation),
                (items, next, snapshot) => JsonSerializer.SerializeToUtf8Bytes(new GuildSpendEffectPageDto(items, next, snapshot), GuildJsonContext.Default.GuildSpendEffectPageDto).Length,
                token).ConfigureAwait(false);

            return page.IsFailure ? page.Error : new GuildSpendEffectPageDto(page.Value.Items, page.Value.Next, page.Value.Snapshot);
        }, ct);

    internal Task<Result<GuildSpendReceiptPageDto>> ListOperatorSpendReceiptsAsync(GuildCallerContext caller, Guid questId, Guid effectId, string? cursor, int pageSize, CancellationToken ct) =>
        WithSpendOwnerAsync<GuildSpendReceiptPageDto>(caller, questId, async (connection, transaction, quest, ledger, token) =>
        {
            Result<GuildBudgetEffect> effect = await ledger.ReadEffectWithinAsync(connection, transaction, effectId, token).ConfigureAwait(false);

            if (effect.IsFailure) return effect.Error;

            if (effect.Value.Request.QuestId != quest.Quest.Id) return OwnerUnavailable;

            Result<SpendPage<GuildSpendReceiptDto>> page = await SpendPageWithinAsync(connection, transaction, caller, quest,
                5, effectId, cursor, pageSize, (id, cancellation) => ReadSpendReceiptWithinAsync(connection, transaction, questId, effectId, id, cancellation),
                (items, next, snapshot) => JsonSerializer.SerializeToUtf8Bytes(new GuildSpendReceiptPageDto(items, next, snapshot), GuildJsonContext.Default.GuildSpendReceiptPageDto).Length,
                token).ConfigureAwait(false);

            return page.IsFailure ? page.Error : new GuildSpendReceiptPageDto(page.Value.Items, page.Value.Next, page.Value.Snapshot);
        }, ct);

    private async Task<Result<T>> WithSpendOwnerAsync<T>(GuildCallerContext caller, Guid questId,
        Func<SqliteConnection, SqliteTransaction, GuildQuestStored, GuildBudgetLedger, CancellationToken, Task<Result<T>>> action, CancellationToken ct)
    {
        if (caller is null || caller.Kind != GuildCallerKind.Operator || questId == Guid.Empty || allocations is not GuildBudgetLedger ledger)
            return OwnerUnavailable;

        Result<GuildQuestStored> located = await GetAsync(caller, questId, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error;

        Guid root = located.Value.Quest.RootQuestId;

        return await authority.InQuestTransactionAsync<T>(root, async (connection, transaction, _, token) =>
        {
            Result<GuildQuestStored> quest = await ReadWithinAsync(connection, transaction, questId, token).ConfigureAwait(false);

            if (quest.IsFailure) return quest.Error;

            if (quest.Value.Quest.RootQuestId != root) return OwnerUnavailable;

            Result visible = await RequireVisibleAsync(connection, transaction, caller, quest.Value, token).ConfigureAwait(false);

            if (visible.IsFailure) return visible.Error;

            Result<Guid> epoch = await GuildSignetStore.EpochAsync(connection, transaction, token).ConfigureAwait(false);

            if (epoch.IsFailure) return epoch.Error;

            await using SqliteCommand current = Command(connection, transaction, "SELECT COUNT(*) FROM guild_signets WHERE SignetId=$signet AND QuestId=$quest AND RootQuestId=$root AND InstallationGeneration=$generation AND AuthorityEpoch=$epoch");
            Add(current, "$signet", Id(quest.Value.Quest.SignetId));

            Add(current, "$quest", Id(questId));

            Add(current, "$root", Id(root));

            Add(current, "$generation", Id(caller.InstallationGeneration));

            Add(current, "$epoch", Id(epoch.Value));

            if (!Equals(1L, await current.ExecuteScalarAsync(token).ConfigureAwait(false))) return OwnerUnavailable;

            return await action(connection, transaction, quest.Value, ledger, token).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    private static async Task<Result<GuildSpendEffectDto>> ReadSpendEffectWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildQuestStored quest, GuildBudgetLedger ledger, Guid effectId, CancellationToken ct)
    {
        if (effectId == Guid.Empty) return OwnerUnavailable;

        Result<GuildBudgetEffect> found = await ledger.ReadEffectWithinAsync(connection, transaction, effectId, ct).ConfigureAwait(false);

        if (found.IsFailure) return found.Error;

        GuildBudgetEffect effect = found.Value;

        if (effect.Request.QuestId != quest.Quest.Id) return OwnerUnavailable;

        Guid? lastReceipt = null;
        GuildSpendReceiptDto? last = null;

        await using (SqliteCommand recent = Command(connection, transaction, "SELECT ReceiptId FROM guild_spend_receipts WHERE EffectId=$effect ORDER BY EvidenceRevision DESC LIMIT 1"))
        {
            Add(recent, "$effect", Id(effectId));

            if (await recent.ExecuteScalarAsync(ct).ConfigureAwait(false) is string raw)
            {
                if (!Guid.TryParseExact(raw, "D", out Guid id) || id == Guid.Empty) return Conflict;
                lastReceipt = id;
            }
        }

        if (lastReceipt is { } receiptId)
        {
            Result<GuildSpendReceiptDto> loaded = await ReadSpendReceiptWithinAsync(connection, transaction, quest.Quest.Id, effectId, receiptId, ct).ConfigureAwait(false);

            if (loaded.IsFailure) return loaded.Error;

            last = loaded.Value;
        }

        GuildSpendLocalTokensDto? local = null;
        string? operatorIneligibility = "remoteEffect";
        string? providerIneligibility = "sourceUnavailable";

        if (effect.Request.Kind == GuildBudgetEffectKind.LocalProvider)
        {
            Result<GuildStoredInitialSpend?> initial = await GuildSpendSourceStore.LoadInitialWithinAsync(connection, transaction, effectId, ct).ConfigureAwait(false);

            if (initial.IsFailure) return initial.Error;

            Result<GuildSpendBill?> billResult = await ReadCorrectionBillWithinAsync(connection, transaction, effect.Request, ct).ConfigureAwait(false);

            if (billResult.IsFailure) return billResult.Error;

            GuildSpendBill? bill = billResult.Value;

            operatorIneligibility = bill is null ? "sourceExpired" : initial.Value is null ? "initialEvidenceUnavailable" : null;

            if (last is not null && last.EvidenceRevision == effect.Revision && last.Facts.KnownUsd == effect.KnownUsd
                && last.Facts.KnownTokens == effect.KnownTokens && last.Facts.UnknownUsd == effect.UnknownUsd
                && last.Facts.UnknownTokens == effect.UnknownTokens && last.Facts.Complete == (effect.State == GuildBudgetEffectState.Complete))
                local = last.Facts.LocalTokens;
            else if (initial.Value is { } saved && saved.Outcome.Revision == effect.Revision && saved.Outcome.KnownUsd == effect.KnownUsd
                && saved.Outcome.KnownTokens == effect.KnownTokens && saved.Outcome.UnknownUsd == effect.UnknownUsd
                && saved.Outcome.UnknownTokens == effect.UnknownTokens && saved.Outcome.State == effect.State)
                local = new(saved.Snapshot.InputTokens, saved.Snapshot.OutputTokens, saved.Snapshot.CachedTokens, saved.Snapshot.ReasoningTokens);

            if (bill is not null && initial.Value is { } original)
            {
                if (!bill.Matches(original.Snapshot) || local is null || bill.Usd != effect.KnownUsd
                    || bill.Input != local.InputTokens || bill.Output != local.OutputTokens
                    || bill.Cached != local.CachedTokens || bill.Reasoning != local.ReasoningTokens) return Conflict;
            }
            else if (bill is not null) local = null;
        }

        return new GuildSpendEffectDto(quest.Quest.Id, effectId,
            effect.Request.Kind == GuildBudgetEffectKind.LocalProvider ? GuildSpendEffectKind.LocalProvider : GuildSpendEffectKind.Remote,
            new(effect.KnownUsd, effect.KnownTokens, effect.UnknownUsd, effect.UnknownTokens, effect.State == GuildBudgetEffectState.Complete, local),
            effect.Revision, lastReceipt, operatorIneligibility, providerIneligibility);
    }

    private static async Task<Result<GuildSpendReceiptDto>> ReadSpendReceiptWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid questId, Guid effectId, Guid receiptId, CancellationToken ct)
    {
        if (effectId == Guid.Empty || receiptId == Guid.Empty) return OwnerUnavailable;

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT CASE WHEN typeof(r.OriginalResult)='blob' AND length(r.OriginalResult)<=4096 THEN r.OriginalResult END,
                r.OriginalEventSequence,r.EvidenceRevision,r.OriginCode,r.AcceptedAtUtc,
                e.KnownUsd,e.KnownTokens,e.UnknownUsd,e.UnknownTokens,e.Complete,e.NoDispatchProven,
                r.RequestIdentity,r.RequestDigest,f.DispatchId,f.AccountingAssociationId,f.SourceId,s.OriginPrincipalId,s.InstallationGeneration,e.Digest
            FROM guild_spend_receipts r JOIN guild_budget_evidence e ON e.ReceiptId=r.EvidenceReceiptId
                AND e.EffectId=r.EffectId AND e.AppliedRevision=r.EvidenceRevision
            JOIN guild_budget_effects f ON f.EffectId=r.EffectId
            JOIN guild_quests q ON q.Id=f.QuestId JOIN guild_signets s ON s.SignetId=q.SignetId
            WHERE r.ReceiptId=$receipt AND r.EffectId=$effect
            """);
        Add(command, "$receipt", Id(receiptId));

        Add(command, "$effect", Id(effectId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return new Error(ErrorCodes.Guild.NotFound, "The spend receipt is unavailable.");

        if (reader.IsDBNull(0) || !UtcInstantText.TryParse(reader.GetString(4), out DateTimeOffset acceptedAt)) return Conflict;

        GuildSpendReceiptDto? receipt;

        try { receipt = JsonSerializer.Deserialize((byte[])reader.GetValue(0), GuildSpendStorageJsonContext.Default.GuildSpendReceiptDto); }
        catch (JsonException) { return Conflict; }

        if (!ValidSpendReceipt(receipt) || receipt!.QuestId != questId || receipt.EffectId != effectId || receipt.ReceiptId != receiptId
            || receipt.EventSequence != reader.GetInt64(1) || receipt.EvidenceRevision != reader.GetInt64(2)
            || (int)receipt.Origin != reader.GetInt32(3) || receipt.AcceptedAt != acceptedAt || !ReceiptMatchesEvidence(reader, 5, receipt)
            || !SpendDigestsMatch(reader, 12, 18)
            || !((byte[])reader.GetValue(0)).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(receipt, GuildSpendStorageJsonContext.Default.GuildSpendReceiptDto))) return Conflict;

        if (receipt.Origin == GuildSpendOrigin.OperatorAttested && !OperatorReceiptMatchesIdentity(reader, receipt)) return Conflict;

        return receipt;
    }

    private static async Task<Result> SavePhysicalSpendReceiptWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildSpendReceiptDto receipt, string identity, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            INSERT INTO guild_spend_receipts(ReceiptId,EffectId,EvidenceReceiptId,EvidenceRevision,OriginCode,RequestIdentity,
                RequestDigest,OriginalResult,OriginalEventSequence,AcceptedAtUtc)
            SELECT $receipt,$effect,e.ReceiptId,$revision,$origin,$identity,e.Digest,$result,$sequence,$now
            FROM guild_budget_evidence e WHERE e.ReceiptId=$receipt AND e.EffectId=$effect AND e.AppliedRevision=$revision
            """);

        Add(command, "$receipt", Id(receipt.ReceiptId));

        Add(command, "$effect", Id(receipt.EffectId));

        Add(command, "$revision", receipt.EvidenceRevision);

        Add(command, "$origin", (int)receipt.Origin);

        Add(command, "$identity", identity);

        Add(command, "$result", JsonSerializer.SerializeToUtf8Bytes(receipt, GuildSpendStorageJsonContext.Default.GuildSpendReceiptDto));

        Add(command, "$sequence", receipt.EventSequence);

        Add(command, "$now", UtcInstantText.Format(receipt.AcceptedAt));

        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1 ? Result.Success() : Conflict;
    }

    private static bool SpendDigestsMatch(SqliteDataReader reader, int requestIndex, int evidenceIndex) =>
        reader.GetValue(requestIndex) is byte[] request && request.Length == 32
        && reader.GetValue(evidenceIndex) is byte[] evidence && evidence.Length == 32
        && CryptographicOperations.FixedTimeEquals(request, evidence);

    private static bool OperatorReceiptMatchesIdentity(SqliteDataReader reader, GuildSpendReceiptDto receipt)
    {
        if (receipt.EvidenceRevision <= 1 || receipt.Facts.LocalTokens is not { } local
            || !Guid.TryParseExact(reader.GetString(11), "D", out Guid key) || key == Guid.Empty
            || reader.GetValue(12) is not byte[] digest || digest.Length != 32
            || !Guid.TryParseExact(reader.GetString(13), "D", out Guid dispatch)
            || !Guid.TryParseExact(reader.GetString(14), "D", out Guid run)
            || !Guid.TryParseExact(reader.GetString(15), "D", out Guid source)
            || !Guid.TryParseExact(reader.GetString(16), "D", out Guid principal)
            || !Guid.TryParseExact(reader.GetString(17), "D", out Guid generation)) return false;

        GuildBudgetEffectRequest binding = new(receipt.EffectId, receipt.QuestId, dispatch, run,
            GuildBudgetEffectKind.LocalProvider, source, null, null, null);

        GuildSpendCorrectionRequest request = new(key, receipt.EvidenceRevision - 1, GuildSpendOrigin.OperatorAttested,
            new(local.InputTokens, local.OutputTokens, local.CachedTokens, local.ReasoningTokens, receipt.Facts.KnownUsd), receipt.EvidenceDigest);

        return CryptographicOperations.FixedTimeEquals(digest, GuildSpendReceiptIdentity.OperatorDigest(generation, principal, binding, request));
    }

    private static bool ReceiptMatchesEvidence(SqliteDataReader reader, int offset, GuildSpendReceiptDto receipt)
    {
        try
        {
            if (reader.GetValue(offset + 1) is not long tokens || reader.GetValue(offset + 2) is not long unknownUsd
                || reader.GetValue(offset + 3) is not long unknownTokens || reader.GetValue(offset + 4) is not long complete
                || reader.GetValue(offset + 5) is not long noDispatch || unknownUsd is not (0 or 1)
                || unknownTokens is not (0 or 1) || complete is not (0 or 1) || noDispatch != 0) return false;

            return ExactUsdText.Read(reader, offset) == receipt.Facts.KnownUsd && tokens == receipt.Facts.KnownTokens
                && (unknownUsd == 1) == receipt.Facts.UnknownUsd && (unknownTokens == 1) == receipt.Facts.UnknownTokens
                && (complete == 1) == receipt.Facts.Complete;
        }
        catch (Exception error) when (error is FormatException or OverflowException or InvalidCastException)
        {
            return false;
        }
    }

    private static bool ValidSpendReceipt(GuildSpendReceiptDto? receipt)
    {
        if (receipt is null || receipt.ReceiptId == Guid.Empty || receipt.QuestId == Guid.Empty || receipt.EffectId == Guid.Empty
            || receipt.EvidenceRevision < 1 || receipt.EventSequence < 1 || !Enum.IsDefined(receipt.Origin)
            || receipt.Facts is not { } facts || facts.KnownUsd < 0 || facts.KnownTokens < 0) return false;

        if (receipt.Origin is GuildSpendOrigin.OperatorAttested or GuildSpendOrigin.ProviderReported
            && (receipt.EvidenceDigest is not { Length: 64 } digest || !digest.All(Uri.IsHexDigit))) return false;

        if (facts.LocalTokens is not { } local) return receipt.Origin == GuildSpendOrigin.RemoteReport;

        return receipt.Origin != GuildSpendOrigin.RemoteReport && local.InputTokens >= 0 && local.OutputTokens >= 0
            && local.InputTokens <= long.MaxValue - local.OutputTokens && local.InputTokens + local.OutputTokens == facts.KnownTokens
            && local.CachedTokens >= 0 && local.CachedTokens <= local.InputTokens
            && local.ReasoningTokens >= 0 && local.ReasoningTokens <= local.OutputTokens;
    }

    private sealed record SpendPage<T>(ImmutableArray<T> Items, string? Next, long Snapshot);

    private async Task<Result<SpendPage<T>>> SpendPageWithinAsync<T>(SqliteConnection connection, SqliteTransaction transaction,
        GuildCallerContext caller, GuildQuestStored quest, int kind, Guid scopeId, string? cursor, int pageSize,
        Func<Guid, CancellationToken, Task<Result<T>>> load, Func<ImmutableArray<T>, string?, long, int> size, CancellationToken ct)
    {
        if (scopeId == Guid.Empty || pageSize is < 1 or > 100) return new Error(ErrorCodes.Guild.InvalidRequest, "The spend page is invalid.");

        Result<Guid> epoch = await GuildSignetStore.EpochAsync(connection, transaction, ct).ConfigureAwait(false);
        if (epoch.IsFailure) return epoch.Error;
        Result<string> visibility = await GuildOperatorVisibility.ReadAsync(connection, transaction, policy, ct).ConfigureAwait(false);
        if (visibility.IsFailure) return visibility.Error;

        string scope = string.Join(":", "spend", kind.ToString(CultureInfo.InvariantCulture), Id(quest.Quest.Id), Id(scopeId), pageSize.ToString(CultureInfo.InvariantCulture));
        string callerKey = TreeCallerKey(caller);
        long after = 0;
        long snapshot;

        if (cursor is null)
        {
            await using SqliteCommand high = Command(connection, transaction, "SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name='guild_operator_sequences'),0)");
            if (await high.ExecuteScalarAsync(ct).ConfigureAwait(false) is not long value || value < 0) return Unavailable;
            snapshot = value;
        }
        else
        {
            if (cursor.Length != 32 || !Guid.TryParseExact(cursor, "N", out Guid id) || id == Guid.Empty) return TreeCursorInvalid;
            await using SqliteCommand saved = Command(connection, transaction, """
                SELECT InstallationGeneration,AuthorityEpoch,CallerKey,RootQuestId,ScopeKey,AfterSequence,SnapshotSequence,VisibilityKey
                FROM guild_portal_cursors WHERE Id=$id
                """);
            Add(saved, "$id", Id(id));
            await using SqliteDataReader reader = await saved.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return TreeCursorInvalid;
            if (reader.GetString(2) != callerKey || reader.GetString(3) != Id(quest.Quest.RootQuestId) || reader.GetString(4) != scope) return TreeCursorInvalid;
            if (reader.GetString(0) != Id(caller.InstallationGeneration) || reader.GetString(1) != Id(epoch.Value) || reader.GetString(7) != visibility.Value) return TreeCursorGone;
            if (reader.GetValue(5) is not long position || reader.GetValue(6) is not long high || position < 0 || high < position) return Unavailable;
            after = position;
            snapshot = high;
        }

        List<(Guid Id, long Sequence)> batch = [];
        await using (SqliteCommand select = Command(connection, transaction, """
            SELECT ObjectId,Sequence FROM guild_operator_sequences
            WHERE KindCode=$kind AND ScopeId=$scope AND Sequence>$after AND Sequence<=$snapshot ORDER BY Sequence LIMIT $limit
            """))
        {
            Add(select, "$kind", kind);

            Add(select, "$scope", Id(scopeId));

            Add(select, "$after", after);

            Add(select, "$snapshot", snapshot);

            Add(select, "$limit", pageSize + 1);
            await using SqliteDataReader reader = await select.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!Guid.TryParseExact(reader.GetString(0), "D", out Guid id) || id == Guid.Empty || reader.GetValue(1) is not long sequence || sequence <= after) return Unavailable;
                batch.Add((id, sequence));
            }
        }

        ImmutableArray<T>.Builder items = ImmutableArray.CreateBuilder<T>();
        bool more = false;
        foreach ((Guid id, long sequence) in batch)
        {
            if (items.Count == pageSize) { more = true; break; }
            Result<T> item = await load(id, ct).ConfigureAwait(false);
            if (item.IsFailure) return item.Error;
            items.Add(item.Value);
            if (size(items.ToImmutable(), new string('x', 32), snapshot) > TreePageMaximumBytes)
            {
                items.RemoveAt(items.Count - 1);
                if (items.Count == 0) return new Error(ErrorCodes.Guild.PayloadTooLarge, "The spend record exceeds the page boundary.");
                more = true;
                break;
            }
            after = sequence;
        }

        string? next = null;
        if (more)
        {
            Guid id = Guid.NewGuid();
            await using SqliteCommand insert = Command(connection, transaction, """
                INSERT INTO guild_portal_cursors(Id,InstallationGeneration,AuthorityEpoch,CallerKey,RootQuestId,ScopeKey,
                    AfterSequence,SnapshotSequence,CreatedAtUtc,VisibilityKey)
                VALUES($id,$generation,$epoch,$caller,$root,$scope,$after,$snapshot,$now,$visibility)
                """);
            Add(insert, "$id", Id(id));

            Add(insert, "$generation", Id(caller.InstallationGeneration));

            Add(insert, "$epoch", Id(epoch.Value));

            Add(insert, "$caller", callerKey);

            Add(insert, "$root", Id(quest.Quest.RootQuestId));

            Add(insert, "$scope", scope);

            Add(insert, "$after", after);

            Add(insert, "$snapshot", snapshot);

            Add(insert, "$now", UtcInstantText.Format(time.GetUtcNow()));

            Add(insert, "$visibility", visibility.Value);
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            next = id.ToString("N");
        }
        return new SpendPage<T>(items.ToImmutable(), next, snapshot);
    }
}
