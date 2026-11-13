using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildSpendRefreshCheckpoint(int Version, Guid RefreshId, Guid QuestId, Guid RootQuestId,
    Guid EffectId, Guid SourceId, Guid OperationId, Guid InstallationGeneration, Guid PrincipalId,
    Guid SignetEpoch, Guid SpendEpoch, Guid SpendKeyId, string Phase);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GuildSpendRefreshCheckpoint))]
internal sealed partial class GuildSpendRefreshCheckpointJsonContext : JsonSerializerContext;

internal sealed partial class GuildQuestStore
{
    internal Task<Result<GuildSpendCorrectionResultDto>> ApplyProviderSpendRefreshAsync(GuildCallerContext caller, Guid questId,
        Guid effectId, Guid refreshId, string? leaseOwner, GuildSpendRefreshCurrentness? currentness,
        GuildSpendObservation? observation, CancellationToken ct) =>
        WithSpendOwnerAsync<GuildSpendCorrectionResultDto>(caller, questId, async (connection, transaction, quest, ledger, token) =>
        {
            if (effectId == Guid.Empty || refreshId == Guid.Empty || caller.PrincipalId is not { } principal) return OwnerUnavailable;

            Guid operationId;
            Guid? acceptedId;

            await using (SqliteCommand request = Command(connection, transaction, """
                SELECT EffectId,OperationId,RequestDigest,AcceptedReceiptId FROM guild_spend_refreshes WHERE RefreshId=$refresh
                """))
            {
                Add(request, "$refresh", Id(refreshId));

                await using SqliteDataReader row = await request.ExecuteReaderAsync(token).ConfigureAwait(false);

                if (!await row.ReadAsync(token).ConfigureAwait(false)) return OwnerUnavailable;

                if (row.GetString(0) != Id(effectId) || row.GetValue(2) is not byte[] saved || saved.Length != 32
                    || !CryptographicOperations.FixedTimeEquals(saved, RefreshRequestDigest(caller.InstallationGeneration, principal,
                        questId, effectId, refreshId)) || !Guid.TryParseExact(row.GetString(1), "N", out operationId)
                    || operationId == Guid.Empty) return Conflict;

                if (row.IsDBNull(3)) acceptedId = null;
                else if (Guid.TryParseExact(row.GetString(3), "D", out Guid receipt) && receipt != Guid.Empty) acceptedId = receipt;
                else return Conflict;
            }

            if (acceptedId is { } prior)
            {
                Result<GuildSpendReceiptDto> receipt = await ReadSpendReceiptWithinAsync(connection, transaction, questId, effectId, prior, token).ConfigureAwait(false);

                if (receipt.IsFailure) return receipt.Error;

                return receipt.Value.Origin == GuildSpendOrigin.ProviderReported
                    ? new GuildSpendCorrectionResultDto(receipt.Value, true) : Conflict;
            }

            if (string.IsNullOrWhiteSpace(leaseOwner) || currentness is null || observation is null) return OwnerUnavailable;

            LongRunningOperation? operation = await LongRunningOperationStore.ReadWithinAsync(connection, transaction, operationId, token).ConfigureAwait(false);

            if (operation is null || operation.Kind != "guild-spend-refresh"
                || operation.RecoveryPolicy != LongRunningOperationRecoveryPolicy.ReconcileAndComplete || operation.CheckpointVersion != 1
                || operation.State is not (LongRunningOperationState.Running or LongRunningOperationState.Waiting)
                || operation.LeaseOwner != leaseOwner || operation.LeaseExpiresAt is not { } expiry || expiry <= time.GetUtcNow()
                || operation.CheckpointPayload is not { Length: > 0 and <= 2048 } payload) return OwnerUnavailable;

            GuildSpendRefreshCheckpoint? checkpoint;

            try { checkpoint = JsonSerializer.Deserialize(payload, GuildSpendRefreshCheckpointJsonContext.Default.GuildSpendRefreshCheckpoint); }
            catch (JsonException) { return Conflict; }

            if (checkpoint is null || checkpoint.Version != 1 || checkpoint.Phase != "admitted"
                || checkpoint.RefreshId != refreshId || checkpoint.QuestId != questId || checkpoint.RootQuestId != quest.Quest.RootQuestId
                || checkpoint.EffectId != effectId || checkpoint.OperationId != operationId
                || checkpoint.InstallationGeneration != caller.InstallationGeneration || checkpoint.PrincipalId != principal
                || !payload.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(checkpoint, GuildSpendRefreshCheckpointJsonContext.Default.GuildSpendRefreshCheckpoint))) return Conflict;

            Result<GuildBudgetEffect> effect = await ledger.ReadEffectWithinAsync(connection, transaction, effectId, token).ConfigureAwait(false);

            if (effect.IsFailure) return effect.Error;

            if (effect.Value.Request.QuestId != questId || effect.Value.Request.Kind != GuildBudgetEffectKind.LocalProvider
                || effect.Value.Request.SourceId != checkpoint.SourceId) return OwnerUnavailable;

            await using (SqliteCommand authorityCheck = Command(connection, transaction, """
                SELECT COUNT(*) FROM guild_signets g JOIN guild_signet_authority_state a ON a.StateKey=1 AND a.Epoch=g.AuthorityEpoch
                JOIN guild_spend_sources s ON s.EffectId=$effect JOIN guild_spend_authority_state p ON p.StateKey=1
                    AND p.AuthorityEpoch=s.AuthorityEpoch AND p.KeyId=s.AuthorityKeyId
                WHERE g.SignetId=$signet AND g.AuthorityEpoch=$signetEpoch AND s.AuthorityEpoch=$spendEpoch AND s.AuthorityKeyId=$spendKey
                """))
            {
                Add(authorityCheck, "$effect", Id(effectId));
                Add(authorityCheck, "$signet", Id(quest.Quest.SignetId));
                Add(authorityCheck, "$signetEpoch", Id(checkpoint.SignetEpoch));
                Add(authorityCheck, "$spendEpoch", Id(checkpoint.SpendEpoch));
                Add(authorityCheck, "$spendKey", Id(checkpoint.SpendKeyId));

                if (!Equals(1L, await authorityCheck.ExecuteScalarAsync(token).ConfigureAwait(false))) return OwnerUnavailable;
            }

            Result<GuildSpendSourceCredential.VerifiedSource> current = await currentness.Credential.VerifyWithinAsync(connection, transaction,
                effectId, caller.InstallationGeneration, token).ConfigureAwait(false);

            if (current.IsFailure) return current.Error;

            if (!observation.IsFrom(current.Value) || expiry <= time.GetUtcNow() || !currentness.IsCurrent()) return OwnerUnavailable;

            token.ThrowIfCancellationRequested();

            byte[] digest = GuildSpendReceiptIdentity.ProviderDigest(observation);

            Result<GuildSpendCorrectionResultDto> corrected = await ApplySpendWithinAsync(connection, transaction, ledger, quest,
                effect.Value, observation.Amounts, new(GuildSpendOrigin.ProviderReported, Convert.ToHexString(digest), digest,
                    observation.EvidenceDigest, null), token).ConfigureAwait(false);

            if (corrected.IsFailure) return corrected.Error;

            await using SqliteCommand pointer = Command(connection, transaction, """
                UPDATE guild_spend_refreshes SET AcceptedReceiptId=$receipt
                WHERE RefreshId=$refresh AND OperationId=$operation AND AcceptedReceiptId IS NULL
                """);

            Add(pointer, "$receipt", Id(corrected.Value.Receipt.ReceiptId));
            Add(pointer, "$refresh", Id(refreshId));
            Add(pointer, "$operation", operationId.ToString("N"));

            if (await pointer.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) return Conflict;

            if (expiry <= time.GetUtcNow()) return OwnerUnavailable;

            token.ThrowIfCancellationRequested();

            return corrected.Value;
        }, ct);

    internal static byte[] RefreshRequestDigest(Guid installation, Guid principal, Guid quest, Guid effect, Guid refresh) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", "Arcanum.Guild.RefreshRequest.v1", Id(installation), Id(principal),
            Id(quest), Id(effect), Id(refresh))));
}
