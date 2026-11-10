using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildBudgetLedger
{
    internal async Task<Result<GuildBudgetEffectAdmission>> ExposeEffectWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid effectId, long expectedRevision, CancellationToken ct)
    {
        if (effectId == Guid.Empty) return Invalid;

        Result<GuildBudgetEffect> found = await ReadEffectWithinAsync(connection, transaction, effectId, ct).ConfigureAwait(false);

        if (found.IsFailure) return found.Error;

        GuildBudgetEffect effect = found.Value;

        // An adopted durable boundary is evidence, never permission to repeat a physical effect.
        if (effect.State != GuildBudgetEffectState.Prepared) return new GuildBudgetEffectAdmission(effect, false);

        if (effect.Revision != expectedRevision) return RevisionConflict;

        if (effect.Revision == long.MaxValue) return Exhausted;

        Result<Guid> binding = await EffectAccountAsync(connection, transaction, effect.Request, ct).ConfigureAwait(false);

        if (binding.IsFailure) return binding.Error;

        Result open = await RequireOpenLineageAsync(connection, transaction, binding.Value, ct).ConfigureAwait(false);

        if (open.IsFailure) return open.Error;

        Result<GuildBudgetSnapshot> account = await ReadWithinAsync(connection, transaction, binding.Value, ct).ConfigureAwait(false);

        if (account.IsFailure) return account.Error;

        if (!Fits(0m, 0L, account.Value)) return Exceeded;

        if (account.Value.Revision == long.MaxValue) return Exhausted;

        if (effect.Request.ReservationId is null && daily?.RequiresReservation == true)
            return new Error(ErrorCodes.Guild.ReconciliationRequired,
                "The daily gate now requires an owned reservation. Prepare a new physical effect before dispatch.");

        if (effect.Request.ReservationId is { } reservation)
        {
            if (daily is null) return new Error(ErrorCodes.Guild.AuthorityUnavailable, "The real daily reservation owner is unavailable.");

            Result exposed = await daily.RecheckBeforeDispatchWithinAsync(connection, transaction,
                reservation, effect.Request.AccountingAssociationId, ct).ConfigureAwait(false);

            if (exposed.IsFailure) return exposed.Error;
        }

        await using SqliteCommand update = Command(connection, transaction, """
            UPDATE guild_budget_effects SET StateCode=1,UnknownUsd=1,UnknownTokens=1,Revision=Revision+1
            WHERE EffectId=$id AND StateCode=0 AND Revision=$revision
            """);

        Add(update, "$id", Id(effectId));
        Add(update, "$revision", expectedRevision);

        if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return RevisionConflict;

        Result changed = await TouchAsync(connection, transaction, binding.Value, ct).ConfigureAwait(false);

        return changed.IsFailure ? changed.Error : new GuildBudgetEffectAdmission(effect with
        {
            State = GuildBudgetEffectState.PossiblyDispatched, UnknownUsd = true, UnknownTokens = true, Revision = effect.Revision + 1,
        }, true);
    }

    internal async Task<Result<GuildBudgetEffect>> PrepareEffectWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildBudgetEffectRequest request, CancellationToken ct)
    {
        if (request is null || request.EffectId == Guid.Empty || request.QuestId == Guid.Empty || request.DispatchId == Guid.Empty
            || request.AccountingAssociationId == Guid.Empty || request.SourceId == Guid.Empty || request.ReservationId == Guid.Empty
            || !Enum.IsDefined(request.Kind) || request.UsdBound < 0 || request.TokenBound < 0) return Invalid;

        await using (SqliteCommand prior = Command(connection, transaction, EffectColumns +
            " WHERE EffectId=$id OR (KindCode=$kind AND SourceId=$source) OR (ReservationId=$reservation AND $reservation IS NOT NULL)"))
        {
            Add(prior, "$id", Id(request.EffectId));
            Add(prior, "$kind", (int)request.Kind);
            Add(prior, "$source", Id(request.SourceId));
            Add(prior, "$reservation", request.ReservationId is { } reserved ? Id(reserved) : null);

            await using SqliteDataReader reader = await prior.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                GuildBudgetEffect existing = ParseEffect(reader);

                return existing.Request == request ? existing : Conflict;
            }
        }

        Result<Guid> binding = await EffectAccountAsync(connection, transaction, request, ct).ConfigureAwait(false);

        if (binding.IsFailure) return binding.Error;

        Result open = await RequireOpenLineageAsync(connection, transaction, binding.Value, ct).ConfigureAwait(false);

        if (open.IsFailure) return open.Error;

        Result<GuildBudgetSnapshot> read = await ReadWithinAsync(connection, transaction, binding.Value, ct).ConfigureAwait(false);

        if (read.IsFailure) return read.Error;

        GuildBudgetSnapshot account = read.Value;

        if (account.Closed) return Closed;

        if (request.Kind == GuildBudgetEffectKind.External
            && (account.Grant.Usd.Mode == GuildBudgetMode.Finite || account.Grant.Tokens.Mode == GuildBudgetMode.Finite))
            return new Error(ErrorCodes.Guild.BudgetCapabilityUnavailable, "General remote transport has no qualified finite spend contract.");

        if (account.Grant.Usd.Mode == GuildBudgetMode.Finite && request.UsdBound is null)
            return new Error(ErrorCodes.Guild.PriceUnavailable, "A finite USD grant requires a trustworthy conservative price bound.");

        if (account.Grant.Tokens.Mode == GuildBudgetMode.Finite && request.TokenBound is null)
            return new Error(ErrorCodes.Guild.BudgetCapabilityUnavailable, "A finite token grant requires a trustworthy conservative token bound.");

        if (!Fits(request.UsdBound, request.TokenBound, account)) return Exceeded;

        if (account.Revision == long.MaxValue) return Exhausted;

        await using SqliteCommand command = Command(connection, transaction, """
            INSERT INTO guild_budget_effects(EffectId,AccountId,QuestId,DispatchId,AccountingAssociationId,KindCode,SourceId,
                ReservationId,UsdBound,TokenBound,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,StateCode,Revision)
            VALUES($id,$account,$quest,$dispatch,$association,$kind,$source,$reservation,$usd,$tokens,'0',0,0,0,0,1)
            """);

        Add(command, "$id", Id(request.EffectId));
        Add(command, "$account", Id(binding.Value));
        Add(command, "$quest", Id(request.QuestId));
        Add(command, "$dispatch", Id(request.DispatchId));
        Add(command, "$association", Id(request.AccountingAssociationId));
        Add(command, "$kind", (int)request.Kind);
        Add(command, "$source", Id(request.SourceId));
        Add(command, "$reservation", request.ReservationId is { } reservation ? Id(reservation) : null);
        Add(command, "$usd", request.UsdBound is { } usd ? ExactUsdText.Format(usd) : null);
        Add(command, "$tokens", request.TokenBound);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        Result changed = await TouchAsync(connection, transaction, binding.Value, ct).ConfigureAwait(false);

        return changed.IsFailure ? changed.Error : new GuildBudgetEffect(request, 0m, 0, false, false, GuildBudgetEffectState.Prepared, 1);
    }

    public async Task<Result> ExposeAsync(SqliteConnection connection, SqliteTransaction transaction, Guid questId,
        Guid accountId, Guid dispatchId, Guid accountingAssociationId, CancellationToken cancellationToken)
    {
        Result open = await RequireOpenLineageAsync(connection, transaction, accountId, cancellationToken).ConfigureAwait(false);

        if (open.IsFailure) return open;

        Result<GuildBudgetSnapshot> account = await ReadWithinAsync(connection, transaction, accountId, cancellationToken).ConfigureAwait(false);

        if (account.IsFailure) return account.Error;

        if (!Fits(0m, 0L, account.Value)) return Exceeded;

        List<GuildBudgetEffect> effects = [];

        await using (SqliteCommand command = Command(connection, transaction, EffectColumns + " WHERE DispatchId=$dispatch"))
        {
            Add(command, "$dispatch", Id(dispatchId));

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) effects.Add(ParseEffect(reader));
        }

        if (effects.Count == 0)
        {
            // A continuation advances the same peer task and cumulative source. Its new dispatch
            // journal is the one-send authority; the open source is neither duplicated nor reset.
            await using SqliteCommand remote = Command(connection, transaction, EffectColumns + """
                 WHERE EffectId=$quest AND QuestId=$quest AND KindCode=$external AND SourceId=$quest AND StateCode=1
                   AND EXISTS(SELECT 1 FROM guild_remote_effects r JOIN guild_dispatches original ON original.DispatchId=r.DispatchId
                       WHERE r.EffectId=guild_budget_effects.EffectId AND r.QuestId=$quest AND r.SettledAtUtc IS NULL
                         AND original.EvidenceCode=3 AND original.RemoteTaskId IS NOT NULL)
                   AND EXISTS(SELECT 1 FROM guild_dispatches d JOIN guild_continuations c ON c.ContinuationId=d.DispatchId
                       WHERE d.DispatchId=$dispatch AND d.QuestId=$quest AND c.QuestId=$quest AND d.IsContinuation=1
                         AND d.EvidenceCode=1 AND d.AccountingAssociationId=$association)
                   AND EXISTS(SELECT 1 FROM guild_budget_evidence e WHERE e.EffectId=guild_budget_effects.EffectId
                       AND e.AppliedRevision=guild_budget_effects.Revision AND e.Complete=0 AND e.NoDispatchProven=0)
                """);

            Add(remote, "$quest", Id(questId));
            Add(remote, "$dispatch", Id(dispatchId));
            Add(remote, "$association", Id(accountingAssociationId));
            Add(remote, "$external", (int)GuildBudgetEffectKind.External);

            await using SqliteDataReader reader = await remote.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) effects.Add(ParseEffect(reader));

            if (effects.Count != 1 || daily?.RequiresReservation == true)
                return new Error(ErrorCodes.Guild.ReconciliationRequired, "No qualified physical effect was prepared for this dispatch.");
        }

        foreach (GuildBudgetEffect effect in effects)
        {
            if (effect.Request.QuestId != questId || effect.Request.AccountingAssociationId != accountingAssociationId) return Conflict;

            Result<Guid> binding = await EffectAccountAsync(connection, transaction, effect.Request, cancellationToken).ConfigureAwait(false);

            if (binding.IsFailure) return binding.Error;

            if (binding.Value != accountId) return Conflict;

            Result<GuildBudgetEffectAdmission> admitted = await ExposeEffectWithinAsync(connection, transaction,
                effect.Request.EffectId, effect.Revision, cancellationToken).ConfigureAwait(false);

            if (admitted.IsFailure) return admitted.Error;
        }

        return Result.Success();
    }

    internal async Task<Result<GuildBudgetEffect>> ApplyEvidenceWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid effectId, GuildBudgetEvidence evidence, CancellationToken ct)
    {
        if (effectId == Guid.Empty || evidence is null || evidence.ReceiptId == Guid.Empty || evidence.KnownUsd < 0
            || evidence.KnownTokens < 0 || evidence.Digest is not { Length: 64 }
            || !evidence.Digest.All(value => value is >= '0' and <= '9' or >= 'A' and <= 'F')
            || evidence.NoDispatchProven && (!evidence.Complete || evidence.KnownUsd != 0m || evidence.KnownTokens != 0
                || evidence.UnknownUsd || evidence.UnknownTokens)) return Invalid;

        Result<GuildBudgetEffect> found = await ReadEffectWithinAsync(connection, transaction, effectId, ct).ConfigureAwait(false);

        if (found.IsFailure) return found.Error;

        GuildBudgetEffect effect = found.Value;

        await using (SqliteCommand prior = Command(connection, transaction, """
            SELECT EffectId,Digest,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,Complete,NoDispatchProven
            FROM guild_budget_evidence WHERE ReceiptId=$receipt
            """))
        {
            Add(prior, "$receipt", Id(evidence.ReceiptId));

            await using SqliteDataReader reader = await prior.ExecuteReaderAsync(ct).ConfigureAwait(false);

            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                return Guid.Parse(reader.GetString(0)) == effectId && Convert.ToHexString((byte[])reader.GetValue(1)) == evidence.Digest
                    && ExactUsdText.Read(reader, 2) == evidence.KnownUsd && reader.GetInt64(3) == evidence.KnownTokens
                    && reader.GetBoolean(4) == evidence.UnknownUsd && reader.GetBoolean(5) == evidence.UnknownTokens
                    && reader.GetBoolean(6) == evidence.Complete && reader.GetBoolean(7) == evidence.NoDispatchProven ? effect : Conflict;
            }
        }

        if (evidence.KnownUsd < effect.KnownUsd || evidence.KnownTokens < effect.KnownTokens
            || effect.State == GuildBudgetEffectState.ProvenNoDispatch && !evidence.NoDispatchProven
            || effect.State == GuildBudgetEffectState.Complete && (!evidence.Complete || evidence.NoDispatchProven)
            || effect.State == GuildBudgetEffectState.Prepared && !evidence.NoDispatchProven) return Conflict;

        if (effect.Revision == long.MaxValue) return Exhausted;

        GuildBudgetEffectState next = evidence.NoDispatchProven ? GuildBudgetEffectState.ProvenNoDispatch
            : evidence.Complete ? GuildBudgetEffectState.Complete : GuildBudgetEffectState.PossiblyDispatched;

        await using (SqliteCommand insert = Command(connection, transaction, """
            INSERT INTO guild_budget_evidence(ReceiptId,EffectId,Digest,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,
                Complete,NoDispatchProven,AppliedRevision)
            VALUES($receipt,$effect,$digest,$usd,$tokens,$unknownUsd,$unknownTokens,$complete,$noDispatch,$revision)
            """))
        {
            BindEvidence(insert, evidence);
            Add(insert, "$effect", Id(effectId));
            Add(insert, "$revision", effect.Revision + 1);

            _ = await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (SqliteCommand update = Command(connection, transaction, """
            UPDATE guild_budget_effects SET KnownUsd=$usd,KnownTokens=$tokens,UnknownUsd=$unknownUsd,
                UnknownTokens=$unknownTokens,StateCode=$state,Revision=Revision+1 WHERE EffectId=$effect AND Revision=$revision
            """))
        {
            Add(update, "$usd", ExactUsdText.Format(evidence.KnownUsd));
            Add(update, "$tokens", evidence.KnownTokens);
            Add(update, "$unknownUsd", evidence.UnknownUsd ? 1 : 0);
            Add(update, "$unknownTokens", evidence.UnknownTokens ? 1 : 0);
            Add(update, "$state", (int)next);
            Add(update, "$effect", Id(effectId));
            Add(update, "$revision", effect.Revision);

            if (await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return RevisionConflict;
        }

        await using SqliteCommand account = Command(connection, transaction, "SELECT AccountId FROM guild_budget_effects WHERE EffectId=$effect");

        Add(account, "$effect", Id(effectId));

        Guid accountId = Guid.Parse((string)(await account.ExecuteScalarAsync(ct).ConfigureAwait(false))!);

        Result changed = await TouchAsync(connection, transaction, accountId, ct).ConfigureAwait(false);

        return changed.IsFailure ? changed.Error : effect with { KnownUsd = evidence.KnownUsd, KnownTokens = evidence.KnownTokens,
            UnknownUsd = evidence.UnknownUsd, UnknownTokens = evidence.UnknownTokens, State = next, Revision = effect.Revision + 1 };
    }

    internal async Task<Result<GuildBudgetEffect>> ReadEffectWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid effectId, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, EffectColumns + " WHERE EffectId=$effect");

        Add(command, "$effect", Id(effectId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false) ? ParseEffect(reader) : Missing;
    }

    private static async Task<Result<Guid>> EffectAccountAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildBudgetEffectRequest request, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT b.AccountId FROM guild_quest_budget b JOIN guild_quests q ON q.Id=b.QuestId AND q.AllocationAccountId=b.AccountId
            JOIN guild_budget_accounts a ON a.AccountId=b.AccountId AND a.OwnerKindCode=0 AND a.OwnerId=q.Id
            JOIN guild_dispatches d ON d.QuestId=q.Id
            WHERE q.Id=$quest AND d.DispatchId=$dispatch AND d.AccountingAssociationId=$association
                AND d.EvidenceCode<>4 AND q.CancellationRequested=0 AND q.Retired=0
                AND NOT EXISTS(SELECT 1 FROM guild_result_receipts r WHERE r.QuestId=q.Id)
            """);

        Add(command, "$quest", Id(request.QuestId));
        Add(command, "$dispatch", Id(request.DispatchId));
        Add(command, "$association", Id(request.AccountingAssociationId));

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string account ? Guid.Parse(account) : Missing;
    }

    private const string EffectColumns = """
        SELECT EffectId,QuestId,DispatchId,AccountingAssociationId,KindCode,SourceId,ReservationId,
            UsdBound,TokenBound,KnownUsd,KnownTokens,UnknownUsd,UnknownTokens,StateCode,Revision FROM guild_budget_effects
        """;

    private static GuildBudgetEffect ParseEffect(SqliteDataReader reader) => new(new(Guid.Parse(reader.GetString(0)),
        Guid.Parse(reader.GetString(1)), Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3)),
        (GuildBudgetEffectKind)reader.GetInt32(4), Guid.Parse(reader.GetString(5)),
        reader.IsDBNull(6) ? null : Guid.Parse(reader.GetString(6)), reader.IsDBNull(7) ? null : ExactUsdText.Read(reader, 7),
        reader.IsDBNull(8) ? null : reader.GetInt64(8)), ExactUsdText.Read(reader, 9), reader.GetInt64(10),
        reader.GetBoolean(11), reader.GetBoolean(12), (GuildBudgetEffectState)reader.GetInt32(13), reader.GetInt64(14));

    private static void BindEvidence(SqliteCommand command, GuildBudgetEvidence evidence)
    {
        Add(command, "$receipt", Id(evidence.ReceiptId));
        Add(command, "$digest", Convert.FromHexString(evidence.Digest));
        Add(command, "$usd", ExactUsdText.Format(evidence.KnownUsd));
        Add(command, "$tokens", evidence.KnownTokens);
        Add(command, "$unknownUsd", evidence.UnknownUsd ? 1 : 0);
        Add(command, "$unknownTokens", evidence.UnknownTokens ? 1 : 0);
        Add(command, "$complete", evidence.Complete ? 1 : 0);
        Add(command, "$noDispatch", evidence.NoDispatchProven ? 1 : 0);
    }
}
