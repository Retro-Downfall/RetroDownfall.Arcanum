using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal Task<Result<bool>> RetryProviderSpendRefreshAsync(GuildCallerContext caller, Guid questId, Guid effectId,
        Guid refreshId, LongRunningOperation operation, GuildSpendRefreshCurrentness currentness,
        LongRunningOperationOwnership ownership, GuildSpendRefreshTracker.Entry entry, CancellationToken ct) =>
        WithSpendOwnerAsync<bool>(caller, questId, async (connection, transaction, quest, ledger, token) =>
        {
            if (entry.Root != quest.Quest.RootQuestId || caller.PrincipalId is not { } principal || operation.Kind != LongRunningOperationKinds.GuildSpendRefresh
                || operation.RecoveryPolicy != LongRunningOperationRecoveryPolicy.ReconcileAndComplete
                || operation.CheckpointVersion != 1 || operation.CheckpointPayload is not { Length: > 0 and <= 2048 } payload)
                return OwnerUnavailable;

            GuildSpendRefreshCheckpoint? checkpoint;

            try { checkpoint = JsonSerializer.Deserialize(payload, GuildSpendRefreshCheckpointJsonContext.Default.GuildSpendRefreshCheckpoint); }
            catch (JsonException) { return Conflict; }

            if (checkpoint is null || checkpoint.Version != 1 || checkpoint.Phase != "admitted"
                || checkpoint.QuestId != questId || checkpoint.RootQuestId != quest.Quest.RootQuestId
                || checkpoint.EffectId != effectId || checkpoint.RefreshId != refreshId || checkpoint.OperationId != operation.Id
                || checkpoint.InstallationGeneration != caller.InstallationGeneration || checkpoint.PrincipalId != principal
                || !payload.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(checkpoint, GuildSpendRefreshCheckpointJsonContext.Default.GuildSpendRefreshCheckpoint)))
                return Conflict;

            byte[] digest = RefreshRequestDigest(caller.InstallationGeneration, principal, questId, effectId, refreshId);

            await using (SqliteCommand request = Command(connection, transaction, """
                SELECT r.EffectId,r.OperationId,r.RequestDigest,r.AcceptedReceiptId,o.Kind,o.RecoveryPolicy,o.CheckpointVersion,
                    o.CheckpointPayload,o.Revision,o.State,o.LeaseExpiresAt,o.LeaseOwner,
                    (SELECT COUNT(*) FROM LongRunningOperations alias INDEXED BY IX_LongRunningOperations_Id_Norm
                        WHERE lower(replace(alias.Id,'-',''))=lower(replace(o.Id,'-','')))
                FROM guild_spend_refreshes r JOIN LongRunningOperations o ON o.Id=r.OperationId WHERE r.RefreshId=$refresh
                """))
            {
                Add(request, "$refresh", Id(refreshId));

                await using SqliteDataReader row = await request.ExecuteReaderAsync(token).ConfigureAwait(false);

                if (!await row.ReadAsync(token).ConfigureAwait(false) || row.GetValue(0) is not string savedEffect || savedEffect != Id(effectId)
                    || row.GetValue(1) is not string savedOperation || savedOperation != operation.Id.ToString("N")
                    || row.GetValue(2) is not byte[] savedDigest || savedDigest.Length != 32
                    || !CryptographicOperations.FixedTimeEquals(savedDigest, digest) || !row.IsDBNull(3)
                    || row.GetValue(4) is not string kind || kind != LongRunningOperationKinds.GuildSpendRefresh
                    || row.GetValue(5) is not long policy || policy != (long)LongRunningOperationRecoveryPolicy.ReconcileAndComplete
                    || row.GetValue(6) is not long version || version != 1 || row.GetValue(7) is not byte[] savedPayload
                    || !savedPayload.AsSpan().SequenceEqual(payload) || row.GetValue(8) is not long revision || revision != operation.Revision
                    || row.GetValue(9) is not long state || state != (long)operation.State || state is not (5 or 6 or 7)
                    || !row.IsDBNull(10) || !row.IsDBNull(11)
                    || !Equals(1L, row.GetValue(12))) return Conflict;
            }

            Result<GuildBudgetEffect> effect = await ledger.ReadEffectWithinAsync(connection, transaction, effectId, token).ConfigureAwait(false);

            if (effect.IsFailure) return effect.Error;

            if (effect.Value.Request.QuestId != questId || effect.Value.Request.Kind != GuildBudgetEffectKind.LocalProvider
                || effect.Value.Request.SourceId != checkpoint.SourceId) return OwnerUnavailable;

            await using (SqliteCommand authorities = Command(connection, transaction, """
                SELECT COUNT(*) FROM guild_signets g JOIN guild_signet_authority_state a ON a.StateKey=1 AND a.Epoch=g.AuthorityEpoch
                JOIN guild_spend_sources s ON s.EffectId=$effect JOIN guild_spend_authority_state p ON p.StateKey=1
                    AND p.AuthorityEpoch=s.AuthorityEpoch AND p.KeyId=s.AuthorityKeyId
                WHERE g.SignetId=$signet AND g.AuthorityEpoch=$signetEpoch AND s.AuthorityEpoch=$spendEpoch AND s.AuthorityKeyId=$spendKey
                """))
            {
                Add(authorities, "$effect", Id(effectId));

                Add(authorities, "$signet", Id(quest.Quest.SignetId));

                Add(authorities, "$signetEpoch", Id(checkpoint.SignetEpoch));

                Add(authorities, "$spendEpoch", Id(checkpoint.SpendEpoch));

                Add(authorities, "$spendKey", Id(checkpoint.SpendKeyId));

                if (!Equals(1L, await authorities.ExecuteScalarAsync(token).ConfigureAwait(false))) return OwnerUnavailable;
            }

            Result<GuildSpendSourceCredential.VerifiedSource> source = await currentness.Credential.VerifyWithinAsync(connection, transaction,
                effectId, caller.InstallationGeneration, token).ConfigureAwait(false);

            if (source.IsFailure) return source.Error;

            Result<GuildSpendEffectDto> current = await ReadSpendEffectWithinAsync(connection, transaction, quest, ledger, effectId, token).ConfigureAwait(false);

            if (current.IsFailure) return current.Error;

            Result daily = await ledger.ValidateDailyCorrectionWithinAsync(connection, transaction, effect.Value, token).ConfigureAwait(false);

            if (daily.IsFailure) return daily.Error;

            await using (SqliteCommand dispatch = Command(connection, transaction, """
                SELECT COUNT(*) FROM guild_dispatches WHERE DispatchId=$dispatch AND QuestId=$quest AND AccountingAssociationId=$run
                """))
            {
                Add(dispatch, "$dispatch", Id(effect.Value.Request.DispatchId));

                Add(dispatch, "$quest", Id(questId));

                Add(dispatch, "$run", Id(effect.Value.Request.AccountingAssociationId));

                if (!Equals(1L, await dispatch.ExecuteScalarAsync(token).ConfigureAwait(false))) return Conflict;
            }

            if (!currentness.IsCurrent() || !entry.IsOpen || ownership.IsClaimed(operation.Id) || !currentness.IsCurrent()) return OwnerUnavailable;

            await using SqliteCommand reset = Command(connection, transaction, """
                UPDATE LongRunningOperations SET State=0,CompletedAt=NULL,HeartbeatAt=$now,LeaseOwner=NULL,LeaseExpiresAt=NULL,
                    TerminalErrorCode=NULL,Revision=Revision+1 WHERE Id=$operation AND Revision=$revision AND State IN(5,6,7)
                """);

            Add(reset, "$now", UtcInstantText.Format(time.GetUtcNow()));

            Add(reset, "$operation", operation.Id.ToString("N"));

            Add(reset, "$revision", operation.Revision);

            if (await reset.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return Conflict;

            if (!currentness.IsCurrent() || !entry.IsOpen || ownership.IsClaimed(operation.Id) || !currentness.IsCurrent()) return OwnerUnavailable;

            token.ThrowIfCancellationRequested();

            return true;
        }, ct);
}
