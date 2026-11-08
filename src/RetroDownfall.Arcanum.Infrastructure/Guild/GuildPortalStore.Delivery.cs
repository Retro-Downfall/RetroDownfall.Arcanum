using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildPortalClaim(GuildQuestDriver Driver, Guid InboxId, Guid EnvelopeId, long InboxRevision);

internal sealed partial class GuildPortalStore
{
    internal Task<Result<GuildPortalClaim>> ClaimAsync(GuildQuestDriver driver, CancellationToken ct)
    {
        if (driver?.Execution is null) return Task.FromResult(Result<GuildPortalClaim>.Failure(Forbidden));

        return authority.InQuestTransactionAsync<GuildPortalClaim>(driver.Execution.RootQuestId, async (connection, transaction, signets, token) =>
        {
            var admitted = await quests.RequireDriverWithinAsync(connection, transaction, signets, driver, true, false, token).ConfigureAwait(false);

            if (admitted.IsFailure) return admitted.Error;

            if (admitted.Value.Row.Quest.CancellationRequested) return ClosedRecipient;

            await using SqliteCommand next = Command(connection, transaction, """
                SELECT i.Id,i.EnvelopeId,i.Revision
                FROM guild_inbox i JOIN guild_envelopes e ON e.Id=i.EnvelopeId
                WHERE i.RecipientKey=$recipient AND i.RecipientQuestId=$quest AND i.DeliveryStateCode=1
                  AND (i.LeaseOwner IS NULL OR i.LeaseOwner<>$holder OR i.LeaseRevision<>$lease OR i.OperationId<>$operation)
                  AND NOT EXISTS(SELECT 1 FROM guild_inbox earlier WHERE earlier.RecipientKey=i.RecipientKey
                    AND earlier.StreamId=i.StreamId AND earlier.DeliveryStateCode=1 AND earlier.SenderSequence<i.SenderSequence)
                ORDER BY e.CreatedAtUtc,i.Id LIMIT 1
                """);

            Add(next, "$recipient", "quest:" + Id(driver.Execution.QuestId));

            Add(next, "$quest", Id(driver.Execution.QuestId));

            Add(next, "$holder", driver.LeaseOwner);

            Add(next, "$lease", driver.OperationRevision);

            Add(next, "$operation", admitted.Value.Row.Quest.OperationId.ToString("N"));

            Guid inbox, envelope;

            long revision;

            await using (SqliteDataReader reader = await next.ExecuteReaderAsync(token).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(token).ConfigureAwait(false)) return NoInbox;

                inbox = Guid.Parse(reader.GetString(0));

                envelope = Guid.Parse(reader.GetString(1));

                revision = reader.GetInt64(2);
            }

            if (revision == long.MaxValue) return Exhausted;

            await using SqliteCommand claim = Command(connection, transaction, """
                UPDATE guild_inbox SET Revision=Revision+1,LeaseOwner=$holder,LeaseRevision=$lease,OperationId=$operation
                WHERE Id=$inbox AND Revision=$revision AND DeliveryStateCode=1
                """);

            Add(claim, "$holder", driver.LeaseOwner);

            Add(claim, "$lease", driver.OperationRevision);

            Add(claim, "$operation", admitted.Value.Row.Quest.OperationId.ToString("N"));

            Add(claim, "$inbox", Id(inbox));

            Add(claim, "$revision", revision);

            if (await claim.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return ClaimConflict;

            var current = await quests.RequireDriverWithinAsync(connection, transaction, signets, driver, true, false, token).ConfigureAwait(false);

            return current.IsFailure ? current.Error : new GuildPortalClaim(driver, inbox, envelope, revision + 1);
        }, ct);
    }

    internal Task<Result<byte[]>> ReadClaimAsync(GuildPortalClaim claim, CancellationToken ct)
    {
        if (claim?.Driver?.Execution is null) return Task.FromResult(Result<byte[]>.Failure(Forbidden));

        return authority.InQuestTransactionAsync<byte[]>(claim.Driver.Execution.RootQuestId, async (connection, transaction, signets, token) =>
        {
            var admitted = await quests.RequireDriverWithinAsync(connection, transaction, signets, claim.Driver, true, false, token).ConfigureAwait(false);

            if (admitted.IsFailure) return admitted.Error;

            Result exact = await RequireClaimWithinAsync(connection, transaction, claim, admitted.Value.Row, token).ConfigureAwait(false);

            if (exact.IsFailure) return exact.Error;

            Result<GuildCallerContext> caller = await DriverCallerWithinAsync(connection, transaction, claim.Driver, token).ConfigureAwait(false);

            if (caller.IsFailure) return caller.Error;

            Result<GuildEnvelope> envelope = await ReadEnvelopeWithinAsync(connection, transaction, claim.EnvelopeId, token).ConfigureAwait(false);

            if (envelope.IsFailure) return envelope.Error;

            Result<byte[]> body = await ReadBodyWithinAsync(connection, transaction, signets, caller.Value, envelope.Value, token).ConfigureAwait(false);

            if (body.IsFailure) return body.Error;

            var current = await quests.RequireDriverWithinAsync(connection, transaction, signets, claim.Driver, true, false, token).ConfigureAwait(false);

            return current.IsFailure ? current.Error : body;
        }, ct);
    }

    internal Task<Result<GuildCircleAcknowledgement>> ApplyAsync(GuildPortalClaim claim, CancellationToken ct)
    {
        if (claim?.Driver?.Execution is null) return Task.FromResult(Result<GuildCircleAcknowledgement>.Failure(Forbidden));

        return authority.InQuestTransactionAsync<GuildCircleAcknowledgement>(claim.Driver.Execution.RootQuestId,
            async (connection, transaction, signets, token) =>
            {
                var identity = await quests.RequireDriverWithinAsync(connection, transaction, signets, claim.Driver, false, false, token).ConfigureAwait(false);

                if (identity.IsFailure) return identity.Error;

                Result<GuildCallerContext> caller = await DriverCallerWithinAsync(connection, transaction, claim.Driver, token).ConfigureAwait(false);

                if (caller.IsFailure) return caller.Error;

                Result<GuildEnvelope> envelope = await ReadEnvelopeWithinAsync(connection, transaction, claim.EnvelopeId, token).ConfigureAwait(false);

                if (envelope.IsFailure) return envelope.Error;

                Result visible = await RequireReaderWithinAsync(connection, transaction, signets, caller.Value, envelope.Value, token).ConfigureAwait(false);

                if (visible.IsFailure) return visible.Error;

                Result<GuildCircleAcknowledgement?> prior = await ReadApplicationWithinAsync(connection, transaction, claim.InboxId,
                    claim.EnvelopeId, "quest:" + Id(claim.Driver.Execution.QuestId), token).ConfigureAwait(false);

                if (prior.IsFailure) return prior.Error;

                if (prior.Value is { } accepted) return accepted;

                var admitted = await quests.RequireDriverWithinAsync(connection, transaction, signets, claim.Driver, true, false, token).ConfigureAwait(false);

                if (admitted.IsFailure) return admitted.Error;

                if (admitted.Value.Row.Quest.CancellationRequested) return ClosedRecipient;

                Result exact = await RequireClaimWithinAsync(connection, transaction, claim, admitted.Value.Row, token).ConfigureAwait(false);

                if (exact.IsFailure) return exact.Error;

                Result<byte[]> disclosure = await ReadBodyWithinAsync(connection, transaction, signets, caller.Value, envelope.Value, token).ConfigureAwait(false);

                if (disclosure.IsFailure) return disclosure.Error;

                Guid receipt = Guid.NewGuid();

                DateTimeOffset now = time.GetUtcNow();

                bool resultEffect = envelope.Value.Kind == GuildEnvelopeKind.Result;

                if (resultEffect)
                {
                    Result<GuildQuestStored> source = await GuildQuestStore.ReadWithinAsync(connection, transaction, envelope.Value.QuestId, token).ConfigureAwait(false);

                    if (source.IsFailure) return source.Error;

                    await using SqliteCommand digest = Command(connection, transaction,
                        "SELECT Digest FROM guild_result_receipts WHERE QuestId=$quest AND ResultId=$result");

                    Add(digest, "$quest", Id(envelope.Value.QuestId));

                    Add(digest, "$result", Id(envelope.Value.Producer.ReceiptId));

                    if (await digest.ExecuteScalarAsync(token).ConfigureAwait(false) is not byte[] resultDigest || resultDigest.Length != 32) return Unavailable;

                    GuildResultApplicationRequest request = new(new(receipt, Convert.ToHexString(resultDigest)),
                        claim.Driver.Execution.QuestId, envelope.Value.QuestId, envelope.Value.Producer.ReceiptId!.Value,
                        Convert.ToHexString(resultDigest), admitted.Value.Row.Quest.Revision);

                    Result<GuildResultApplicationReceipt> applied = await quests.ApplyResultWithinAsync(connection, transaction,
                        signets, caller.Value, request, token).ConfigureAwait(false);

                    if (applied.IsFailure) return applied.Error;
                }

                Result<GuildCircleAcknowledgement> committed = await CommitInboxWithinAsync(connection, transaction,
                    claim.InboxId, claim.EnvelopeId, claim.InboxRevision, receipt, resultEffect ? 1 : 0,
                    claim.Driver.Execution.QuestId, now, token).ConfigureAwait(false);

                if (committed.IsFailure) return committed.Error;

                var current = await quests.RequireDriverWithinAsync(connection, transaction, signets, claim.Driver, true, false, token).ConfigureAwait(false);

                return current.IsFailure ? current.Error : committed;
            }, ct);
    }

    private async Task<Result<GuildCallerContext>> DriverCallerWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildQuestDriver driver, CancellationToken ct)
    {
        Result<GuildSignetRecord> signet = await GuildSignetStore.ReadAsync(connection, transaction, driver.Execution.SignetId, false, ct).ConfigureAwait(false);

        if (signet.IsFailure) return signet.Error;

        GuildCallerContext caller = driver.Caller ?? new(GuildCallerKind.Run, signet.Value.Binding.OriginPrincipalId,
            driver.Execution.RunId, policy.InstallationGeneration, driver.Execution);

        return caller.Kind == GuildCallerKind.Run && ReferenceEquals(caller.Execution, driver.Execution)
            && caller.PrincipalId == signet.Value.Binding.OriginPrincipalId && caller.InstallationGeneration == policy.InstallationGeneration
            ? caller : Forbidden;
    }

    private static async Task<Result> RequireClaimWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        GuildPortalClaim claim, GuildQuestStored recipient, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT 1 FROM guild_inbox i WHERE i.Id=$inbox AND i.EnvelopeId=$envelope AND i.RecipientQuestId=$quest
              AND i.RecipientKey=$recipient AND i.Revision=$revision AND i.DeliveryStateCode=1
              AND i.LeaseOwner=$holder AND i.LeaseRevision=$lease AND i.OperationId=$operation
              AND NOT EXISTS(SELECT 1 FROM guild_inbox earlier WHERE earlier.RecipientKey=i.RecipientKey
                AND earlier.StreamId=i.StreamId AND earlier.DeliveryStateCode=1 AND earlier.SenderSequence<i.SenderSequence)
            """);

        Add(command, "$inbox", Id(claim.InboxId));

        Add(command, "$envelope", Id(claim.EnvelopeId));

        Add(command, "$quest", Id(recipient.Quest.Id));

        Add(command, "$recipient", "quest:" + Id(recipient.Quest.Id));

        Add(command, "$revision", claim.InboxRevision);

        Add(command, "$holder", claim.Driver.LeaseOwner);

        Add(command, "$lease", claim.Driver.OperationRevision);

        Add(command, "$operation", recipient.Quest.OperationId.ToString("N"));

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null ? Result.Success() : ClaimConflict;
    }

    private static async Task<Result<GuildCircleAcknowledgement?>> ReadApplicationWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid inbox, Guid envelope, string recipientKey, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT i.DeliveryStateCode,i.Revision,r.ApplicationReceiptId,r.CreatedAtUtc,
              CASE WHEN r.Digest=e.IntentDigest AND r.EffectKindCode IN(0,1,2)
                AND (r.EffectKindCode=2 OR EXISTS(SELECT 1 FROM guild_portal_inputs p WHERE p.InboxId=i.Id
                  AND p.EnvelopeId=i.EnvelopeId AND p.RecipientQuestId=i.RecipientQuestId)) THEN 1 ELSE 0 END
            FROM guild_inbox i JOIN guild_envelopes e ON e.Id=i.EnvelopeId
            LEFT JOIN guild_portal_receipts r ON r.InboxId=i.Id
            WHERE i.Id=$inbox AND i.EnvelopeId=$envelope AND i.RecipientKey=$recipient
            """);

        Add(command, "$inbox", Id(inbox));

        Add(command, "$envelope", Id(envelope));

        Add(command, "$recipient", recipientKey);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return Forbidden;

        if (reader.GetInt32(0) == 3) return new Error(ErrorCodes.Guild.ContentErased, "The Portal effect was withheld.");

        if (reader.GetInt32(0) == 1) return Result<GuildCircleAcknowledgement?>.Success(null);

        if (reader.GetInt32(0) != 2 || reader.IsDBNull(2) || reader.GetInt32(4) != 1) return Unavailable;

        Guid receipt = Guid.Parse(reader.GetString(2));

        long revision = reader.GetInt64(1);

        return Result<GuildCircleAcknowledgement?>.Success(new(inbox, receipt, revision,
            new(receipt, receipt, true, UtcInstantText.Parse(reader.GetString(3)), revision)));
    }

    private static async Task<Result<GuildCircleAcknowledgement>> CommitInboxWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, Guid inbox, Guid envelope, long expectedRevision, Guid receipt, int effectKind,
        Guid? recipientQuest, DateTimeOffset now, CancellationToken ct)
    {
        if (expectedRevision == long.MaxValue) return Exhausted;

        await using SqliteCommand command = Command(connection, transaction, """
            INSERT INTO guild_portal_receipts(InboxId,ApplicationReceiptId,Digest,EffectKindCode,CreatedAtUtc)
            SELECT i.Id,$receipt,e.IntentDigest,$kind,$now FROM guild_inbox i JOIN guild_envelopes e ON e.Id=i.EnvelopeId
            WHERE i.Id=$inbox AND i.EnvelopeId=$envelope AND i.Revision=$revision AND i.DeliveryStateCode=1;
            """);

        Add(command, "$inbox", Id(inbox));

        Add(command, "$envelope", Id(envelope));

        Add(command, "$receipt", Id(receipt));

        Add(command, "$kind", effectKind);

        Add(command, "$now", UtcInstantText.Format(now));

        Add(command, "$revision", expectedRevision);

        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return ClaimConflict;

        if (recipientQuest is { } quest)
        {
            await using SqliteCommand input = Command(connection, transaction,
                "INSERT INTO guild_portal_inputs(InboxId,RecipientQuestId,EnvelopeId,CreatedAtUtc) VALUES($inbox,$quest,$envelope,$now)");

            Add(input, "$inbox", Id(inbox));

            Add(input, "$quest", Id(quest));

            Add(input, "$envelope", Id(envelope));

            Add(input, "$now", UtcInstantText.Format(now));

            _ = await input.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        command.CommandText = """
            UPDATE guild_inbox SET DeliveryStateCode=2,Revision=Revision+1,LeaseOwner=NULL,LeaseRevision=NULL,
              OperationId=NULL,AcknowledgedAtUtc=$now WHERE Id=$inbox AND Revision=$revision AND DeliveryStateCode=1
            """;

        command.Parameters.RemoveAt("$now");

        Add(command, "$now", UtcInstantText.Format(now));

        if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1) return ClaimConflict;

        Result<GuildEnvelope> stored = await ReadEnvelopeWithinAsync(connection, transaction, envelope, ct).ConfigureAwait(false);

        if (stored.IsFailure) return stored.Error;

        Result<long> projected = await GuildPortalEvents.AppendWithinAsync(connection, transaction, stored.Value.RootQuestId,
            recipientQuest ?? stored.Value.QuestId, GuildEventKind.MessageCommitted, inbox, expectedRevision + 1,
            envelope, now, ct, GuildPortalEventSource.Inbox).ConfigureAwait(false);

        if (projected.IsFailure) return projected.Error;

        return new GuildCircleAcknowledgement(inbox, receipt, expectedRevision + 1,
            new(receipt, receipt, false, now, expectedRevision + 1));
    }

    private static Error ClaimConflict => new(ErrorCodes.Guild.RevisionConflict, "The Portal claim no longer owns this effect.");

    private static Error NoInbox => new(ErrorCodes.Guild.NotFound, "No pending Portal effect is eligible.");

    private static Error ClosedRecipient => new(ErrorCodes.Guild.Rejected, "The recipient Quest is closed to new input.");
}
