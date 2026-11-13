using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Operations;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildSpendRefreshPreparation(string? Provider, string? Model, Guid? OperationId,
    GuildSpendReceiptDto? Receipt, bool Created = false, Guid? RootQuestId = null);

internal sealed partial class GuildQuestStore
{
    internal Task<Result<GuildSpendRefreshPreparation>> PrepareProviderSpendRefreshAsync(GuildCallerContext caller, Guid questId,
        Guid effectId, Guid refreshId, CancellationToken ct) =>
        WithSpendOwnerAsync<GuildSpendRefreshPreparation>(caller, questId, async (connection, transaction, quest, ledger, token) =>
        {
            if (effectId == Guid.Empty || refreshId == Guid.Empty || caller.PrincipalId is not { } principal) return OwnerUnavailable;

            await using (SqliteCommand existing = Command(connection, transaction, """
                SELECT EffectId,OperationId,RequestDigest,AcceptedReceiptId FROM guild_spend_refreshes WHERE RefreshId=$refresh
                """))
            {
                Add(existing, "$refresh", Id(refreshId));

                await using SqliteDataReader row = await existing.ExecuteReaderAsync(token).ConfigureAwait(false);

                if (await row.ReadAsync(token).ConfigureAwait(false))
                {
                    if (row.GetValue(0) is not string storedEffect || storedEffect != Id(effectId)
                        || row.GetValue(1) is not string rawOperation || !Guid.TryParseExact(rawOperation, "N", out Guid operation)
                        || operation == Guid.Empty || row.GetValue(2) is not byte[] digest || digest.Length != 32
                        || !CryptographicOperations.FixedTimeEquals(digest, RefreshRequestDigest(caller.InstallationGeneration, principal,
                            questId, effectId, refreshId))) return Conflict;

                    // This is a read-only lookup of the authenticated saved request, not authority to start new work.
                    if (row.IsDBNull(3)) return new GuildSpendRefreshPreparation(null, null, operation, null);

                    if (row.GetValue(3) is not string rawReceipt || !Guid.TryParseExact(rawReceipt, "D", out Guid receiptId)
                        || receiptId == Guid.Empty) return Conflict;

                    await row.DisposeAsync().ConfigureAwait(false);

                    Result<GuildSpendReceiptDto> receipt = await ReadSpendReceiptWithinAsync(connection, transaction, questId,
                        effectId, receiptId, token).ConfigureAwait(false);

                    if (receipt.IsFailure) return receipt.Error;

                    return receipt.Value.Origin == GuildSpendOrigin.ProviderReported
                        ? new GuildSpendRefreshPreparation(null, null, operation, receipt.Value) : Conflict;
                }
            }

            Result ownerProof = await GuildSpendRefreshOwnerProof.RequireWithinAsync(connection, transaction, questId,
                quest.Quest.RootQuestId, token).ConfigureAwait(false);

            if (ownerProof.IsFailure) return ownerProof.Error;

            Result<GuildBudgetEffect> found = await ledger.ReadEffectWithinAsync(connection, transaction, effectId, token).ConfigureAwait(false);

            if (found.IsFailure) return found.Error;

            if (found.Value.Request.QuestId != questId || found.Value.Request.Kind != GuildBudgetEffectKind.LocalProvider)
                return OwnerUnavailable;

            await using SqliteCommand source = Command(connection, transaction, """
                SELECT Provider,Model,SourceKindCode FROM guild_spend_sources WHERE EffectId=$effect
                """);

            Add(source, "$effect", Id(effectId));

            await using SqliteDataReader selected = await source.ExecuteReaderAsync(token).ConfigureAwait(false);

            if (!await selected.ReadAsync(token).ConfigureAwait(false)) return GuildSpendSourceFailure.SourceMissing;

            if (selected.GetValue(0) is not string provider || string.IsNullOrWhiteSpace(provider)
                || selected.GetValue(1) is not string model || string.IsNullOrWhiteSpace(model)
                || selected.GetValue(2) is not long kind || kind != 1) return GuildSpendSourceFailure.SourceInvalid;

            return new GuildSpendRefreshPreparation(provider, model, null, null, RootQuestId: quest.Quest.RootQuestId);
        }, ct);

    internal Task<Result<GuildSpendRefreshPreparation>> AdmitProviderSpendRefreshAsync(GuildCallerContext caller, Guid questId,
        Guid effectId, Guid refreshId, GuildSpendRefreshCurrentness currentness, CancellationToken ct) =>
        WithSpendOwnerAsync<GuildSpendRefreshPreparation>(caller, questId, async (connection, transaction, quest, ledger, token) =>
        {
            if (effectId == Guid.Empty || refreshId == Guid.Empty || caller.PrincipalId is not { } principal) return OwnerUnavailable;

            byte[] digest = RefreshRequestDigest(caller.InstallationGeneration, principal, questId, effectId, refreshId);

            await using (SqliteCommand existing = Command(connection, transaction, """
                SELECT EffectId,OperationId,RequestDigest,AcceptedReceiptId FROM guild_spend_refreshes WHERE RefreshId=$refresh
                """))
            {
                Add(existing, "$refresh", Id(refreshId));

                await using SqliteDataReader row = await existing.ExecuteReaderAsync(token).ConfigureAwait(false);

                if (await row.ReadAsync(token).ConfigureAwait(false))
                {
                    if (row.GetValue(0) is not string storedEffect || storedEffect != Id(effectId)
                        || row.GetValue(1) is not string rawOperation || !Guid.TryParseExact(rawOperation, "N", out Guid operation)
                        || operation == Guid.Empty || row.GetValue(2) is not byte[] saved || saved.Length != 32
                        || !CryptographicOperations.FixedTimeEquals(digest, saved)) return Conflict;

                    // This is a read-only lookup of the authenticated saved request, not authority to start new work.
                    if (row.IsDBNull(3)) return new GuildSpendRefreshPreparation(null, null, operation, null);

                    if (row.GetValue(3) is not string rawReceipt || !Guid.TryParseExact(rawReceipt, "D", out Guid receiptId)
                        || receiptId == Guid.Empty) return Conflict;

                    await row.DisposeAsync().ConfigureAwait(false);

                    Result<GuildSpendReceiptDto> receipt = await ReadSpendReceiptWithinAsync(connection, transaction, questId,
                        effectId, receiptId, token).ConfigureAwait(false);

                    if (receipt.IsFailure) return receipt.Error;

                    return receipt.Value.Origin == GuildSpendOrigin.ProviderReported
                        ? new GuildSpendRefreshPreparation(null, null, operation, receipt.Value) : Conflict;
                }
            }

            if (currentness is null) return OwnerUnavailable;

            Result<RefreshAdmissibility> admissible = await ValidateRefreshSourceWithinAsync(connection, transaction, quest, ledger,
                caller, questId, effectId, currentness, token).ConfigureAwait(false);

            if (admissible.IsFailure) return admissible.Error;

            GuildBudgetEffect effect = admissible.Value.Effect;

            Guid signetEpoch = admissible.Value.SignetEpoch;

            Guid spendEpoch = admissible.Value.SpendEpoch;

            Guid spendKey = admissible.Value.SpendKey;

            Guid operationId = Guid.NewGuid();

            GuildSpendRefreshCheckpoint checkpoint = new(1, refreshId, questId, quest.Quest.RootQuestId, effectId,
                effect.Request.SourceId, operationId, caller.InstallationGeneration, principal, signetEpoch, spendEpoch, spendKey, "admitted");

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(checkpoint, GuildSpendRefreshCheckpointJsonContext.Default.GuildSpendRefreshCheckpoint);

            await LongRunningOperationStore.CreateWithinAsync(connection, transaction, operationId,
                new(LongRunningOperationKinds.GuildSpendRefresh, LongRunningOperationRecoveryPolicy.ReconcileAndComplete, "Provider spend refresh", time.GetUtcNow()),
                1, payload, null, token).ConfigureAwait(false);

            await using SqliteCommand insert = Command(connection, transaction, """
                INSERT INTO guild_spend_refreshes(RefreshId,EffectId,OperationId,RequestDigest)
                VALUES($refresh,$effect,$operation,$digest)
                """);

            Add(insert, "$refresh", Id(refreshId));

            Add(insert, "$effect", Id(effectId));

            Add(insert, "$operation", operationId.ToString("N"));

            Add(insert, "$digest", digest);

            await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);

            if (!currentness.IsCurrent()) return OwnerUnavailable;

            token.ThrowIfCancellationRequested();

            return new GuildSpendRefreshPreparation(null, null, operationId, null, true);
        }, ct);

    private sealed record RefreshAdmissibility(GuildBudgetEffect Effect, Guid SignetEpoch, Guid SpendEpoch, Guid SpendKey);

    private static async Task<Result<RefreshAdmissibility>> ValidateRefreshSourceWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildQuestStored quest, GuildBudgetLedger ledger, GuildCallerContext caller, Guid questId,
        Guid effectId, GuildSpendRefreshCurrentness currentness, CancellationToken token,
        IReadOnlyDictionary<Guid, SpendBillIdentity>? identities = null)
    {
        Result ownerProof = await GuildSpendRefreshOwnerProof.RequireWithinAsync(connection, transaction, questId,
            quest.Quest.RootQuestId, token).ConfigureAwait(false);

        if (ownerProof.IsFailure) return ownerProof.Error;

        Result<GuildBudgetEffect> effectResult = await ledger.ReadEffectWithinAsync(connection, transaction, effectId, token).ConfigureAwait(false);

        if (effectResult.IsFailure) return effectResult.Error;

        GuildBudgetEffect effect = effectResult.Value;

        if (effect.Request.QuestId != questId || effect.Request.Kind != GuildBudgetEffectKind.LocalProvider) return OwnerUnavailable;

        string? exactBillId = null;

        if (identities is not null)
        {
            if (!identities.TryGetValue(effect.Request.SourceId, out SpendBillIdentity identity) || identity.Count > 1
                || identity.Count == 1 && identity.ExactId is null) return Conflict;

            if (identity.Count == 0) return new Error(ErrorCodes.Guild.SourceExpired, "The physical billing source is unavailable.");

            exactBillId = identity.ExactId;
        }

        Result<GuildSpendSourceCredential.VerifiedSource> source = await currentness.Credential.VerifyWithinAsync(connection, transaction,
            effectId, caller.InstallationGeneration, token, exactBillId).ConfigureAwait(false);

        if (source.IsFailure) return source.Error;

        Result<GuildSpendEffectDto> current = await ReadSpendEffectWithinAsync(connection, transaction, quest, ledger, effectId, token, identities).ConfigureAwait(false);

        if (current.IsFailure) return current.Error;

        Result daily = await ledger.ValidateDailyCorrectionWithinAsync(connection, transaction, effect, token).ConfigureAwait(false);

        if (daily.IsFailure) return daily.Error;

        await using (SqliteCommand dispatch = Command(connection, transaction, """
            SELECT COUNT(*) FROM guild_dispatches WHERE DispatchId=$dispatch AND QuestId=$quest AND AccountingAssociationId=$run
            """))
        {
            Add(dispatch, "$dispatch", Id(effect.Request.DispatchId));

            Add(dispatch, "$quest", Id(questId));

            Add(dispatch, "$run", Id(effect.Request.AccountingAssociationId));

            if (!Equals(1L, await dispatch.ExecuteScalarAsync(token).ConfigureAwait(false))) return Conflict;
        }

        Guid signetEpoch;

        Guid spendEpoch;

        Guid spendKey;

        await using (SqliteCommand epochs = Command(connection, transaction, """
            SELECT g.AuthorityEpoch,s.AuthorityEpoch,s.AuthorityKeyId FROM guild_signets g
            JOIN guild_spend_sources s ON s.EffectId=$effect WHERE g.SignetId=$signet
            """))
        {
            Add(epochs, "$effect", Id(effectId));

            Add(epochs, "$signet", Id(quest.Quest.SignetId));

            await using SqliteDataReader row = await epochs.ExecuteReaderAsync(token).ConfigureAwait(false);

            if (!await row.ReadAsync(token).ConfigureAwait(false)
                || row.GetValue(0) is not string signet || !Guid.TryParseExact(signet, "D", out signetEpoch)
                || row.GetValue(1) is not string spend || !Guid.TryParseExact(spend, "D", out spendEpoch)
                || row.GetValue(2) is not string key || !Guid.TryParseExact(key, "D", out spendKey)
                || signetEpoch == Guid.Empty || spendEpoch == Guid.Empty || spendKey == Guid.Empty) return Conflict;
        }

        if (identities is null)
        {
            if (!currentness.IsCurrent()) return OwnerUnavailable;
        }
        else
        {
            bool isCurrent;

            try { isCurrent = currentness.IsCurrent(); }
            catch (Exception)
            {
                token.ThrowIfCancellationRequested();

                return OwnerUnavailable;
            }

            token.ThrowIfCancellationRequested();

            if (!isCurrent) return OwnerUnavailable;
        }

        return new RefreshAdmissibility(effect, signetEpoch, spendEpoch, spendKey);
    }

    internal Task<Result<GuildSpendRefreshPreparation>> ReadProviderEligibilitySelectionAsync(GuildCallerContext caller,
        Guid questId, Guid effectId, CancellationToken ct) =>
        WithSpendOwnerAsync<GuildSpendRefreshPreparation>(caller, questId, async (connection, transaction, quest, ledger, token) =>
        {
            Result owner = await GuildSpendRefreshOwnerProof.RequireWithinAsync(connection, transaction, questId,
                quest.Quest.RootQuestId, token).ConfigureAwait(false);

            if (owner.IsFailure) return owner.Error;

            Result<GuildBudgetEffect> effect = await ledger.ReadEffectWithinAsync(connection, transaction, effectId, token).ConfigureAwait(false);

            if (effect.IsFailure) return effect.Error;

            if (effect.Value.Request.QuestId != questId || effect.Value.Request.Kind != GuildBudgetEffectKind.LocalProvider) return OwnerUnavailable;

            Result<GuildStoredInitialSpend?> initial = await GuildSpendSourceStore.LoadInitialWithinAsync(connection, transaction, effectId, token).ConfigureAwait(false);

            if (initial.IsFailure) return initial.Error;

            if (initial.Value is not { } saved) return GuildSpendSourceFailure.InitialEvidenceUnavailable;

            return new GuildSpendRefreshPreparation(saved.Snapshot.Provider, saved.Snapshot.Model, null, null, RootQuestId: quest.Quest.RootQuestId);
        }, ct);

    internal Task<Result<Dictionary<Guid, Result>>> CheckProviderRefreshEligibilityBatchAsync(GuildCallerContext caller,
        Guid questId, IReadOnlyList<(Guid EffectId, GuildSpendRefreshCurrentness Currentness)> candidates, CancellationToken ct) =>
        WithSpendOwnerAsync<Dictionary<Guid, Result>>(caller, questId, async (connection, transaction, quest, ledger, token) =>
        {
            if (candidates.Count > 100 || candidates.Select(candidate => candidate.EffectId).Distinct().Count() != candidates.Count)
                return Conflict;

            Dictionary<Guid, SpendBillIdentity> identities = [];

            await CensusSpendBillsWithinAsync(connection, transaction, candidates.Select(candidate => candidate.EffectId).ToArray(), identities, token).ConfigureAwait(false);

            Dictionary<Guid, Result> results = [];

            foreach ((Guid effectId, GuildSpendRefreshCurrentness currentness) in candidates)
            {
                Result<RefreshAdmissibility> result = await ValidateRefreshSourceWithinAsync(connection, transaction, quest, ledger,
                    caller, questId, effectId, currentness, token, identities).ConfigureAwait(false);

                results.Add(effectId, result.IsFailure ? Result.Failure(result.Error) : Result.Success());
            }

            return results;
        }, ct);

    internal Task<Result<GuildSpendRefreshPreparation>> SelectProviderSpendRefreshSourceAsync(GuildCallerContext caller,
        Guid questId, Guid effectId, CancellationToken ct) =>
        WithSpendOwnerAsync<GuildSpendRefreshPreparation>(caller, questId, async (connection, transaction, quest, ledger, token) =>
        {
            Result ownerProof = await GuildSpendRefreshOwnerProof.RequireWithinAsync(connection, transaction, questId,
                quest.Quest.RootQuestId, token).ConfigureAwait(false);

            if (ownerProof.IsFailure) return ownerProof.Error;

            Result<GuildBudgetEffect> effect = await ledger.ReadEffectWithinAsync(connection, transaction, effectId, token).ConfigureAwait(false);

            if (effect.IsFailure) return effect.Error;

            if (effect.Value.Request.QuestId != questId || effect.Value.Request.Kind != GuildBudgetEffectKind.LocalProvider) return OwnerUnavailable;

            await using SqliteCommand source = Command(connection, transaction,
                "SELECT Provider,Model,SourceKindCode FROM guild_spend_sources WHERE EffectId=$effect");

            Add(source, "$effect", Id(effectId));

            await using SqliteDataReader row = await source.ExecuteReaderAsync(token).ConfigureAwait(false);

            if (!await row.ReadAsync(token).ConfigureAwait(false)) return GuildSpendSourceFailure.SourceMissing;

            if (row.GetValue(0) is not string provider || string.IsNullOrWhiteSpace(provider)
                || row.GetValue(1) is not string model || string.IsNullOrWhiteSpace(model)
                || row.GetValue(2) is not long kind || kind != 1) return GuildSpendSourceFailure.SourceInvalid;

            return new GuildSpendRefreshPreparation(provider, model, null, null, RootQuestId: quest.Quest.RootQuestId);
        }, ct);
}
