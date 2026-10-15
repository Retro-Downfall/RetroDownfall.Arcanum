using System.Collections.Immutable;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Weave;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

internal sealed partial class LongRestDiscoveryStore
{
    private static readonly UTF8Encoding HierarchyUtf8 = new(false, true);

    public Task<Result<LongRestAcceptedHierarchySummary>> CommitAcceptedHierarchySummaryAsync(LongRestAcceptedHierarchySummary summary,
        LongRestMaintenanceCapability? capability, CancellationToken cancellationToken) =>
        CommitAcceptedHierarchySummaryCoreAsync(summary, null, null, capability, cancellationToken, false);

    public Task<Result<LongRestAcceptedHierarchySummary>> CommitAcceptedHierarchySummaryAsync(LongRestAcceptedHierarchySummary summary, LongRestDispatchCompletion completion, string workerOwner,
        LongRestMaintenanceCapability? capability, CancellationToken cancellationToken, bool committedOnly = false) =>
        CommitAcceptedHierarchySummaryCoreAsync(summary, completion, workerOwner, capability, cancellationToken, committedOnly);

    private async Task<Result<LongRestAcceptedHierarchySummary>> CommitAcceptedHierarchySummaryCoreAsync(LongRestAcceptedHierarchySummary summary, LongRestDispatchCompletion? completion, string? workerOwner,
        LongRestMaintenanceCapability? capability, CancellationToken cancellationToken, bool committedOnly)
    {
        try
        {
            if (summary is null || summary.Fold is null)
            {
                return Refuse<LongRestAcceptedHierarchySummary>(ErrorCodes.LongRest.InvalidRequest, "An accepted summary requires exact fold evidence.");
            }

            SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            LongRestVerificationCounters.Transaction(deferred: false);

            LongRestDiscoveryGeneration? generation = await ReadGenerationAsync(connection, transaction, "GenerationId = $id", Format(summary.GenerationId), cancellationToken).ConfigureAwait(false);

            if (generation is null || generation.Phase != LongRestSweepPhase.Hierarchy)
            {
                return Refuse<LongRestAcceptedHierarchySummary>(ErrorCodes.LongRest.StaleInput, "The hierarchy generation moved before summary acceptance.");
            }

            LongRestHierarchyFold current = await ResolveCurrentFoldAsync(connection, transaction, generation, summary.Fold.Layer,
                summary.Fold.Group, capability, LongRestVerificationDepth.Step, cancellationToken).ConfigureAwait(false);

            if (Math.Max(1, generation.FoldLayer) != current.Layer || generation.FoldGroup != current.Group)
            {
                throw new InvalidDataException();
            }

            LongRestAcceptedHierarchySummary? existing = await ReadAcceptedSummaryAsync(connection, transaction, current, generation.Recipe, cancellationToken).ConfigureAwait(false);

            if (existing is not null)
            {
                if (StoredSummaryJson(existing) != StoredSummaryJson(summary))
                {
                    throw new InvalidDataException();
                }

                if (completion is not null)
                {
                    await LongRestAttemptStore.AcceptWithinAsync(connection, transaction, summary.PaidEvidence, completion,
                        timeProvider.GetUtcNow(), cancellationToken, committedOnly: true).ConfigureAwait(false);
                }

                return existing;
            }

            if (committedOnly) throw new InvalidDataException();

            ValidateSummary(summary, current, generation.Recipe);

            if (LongRestDiscoveryHash.Work(summary.Fold.Work).Value != LongRestDiscoveryHash.Work(current.Work).Value)
            {
                throw new InvalidDataException();
            }

            if (completion is not null && (await LongRestLiveFenceReader.ReadWorkerWithinAsync(connection, transaction,
                generation, workerOwner ?? string.Empty, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false)).IsFailure)
            {
                throw new InvalidDataException();
            }

            if (completion is not null)
            {
                await LongRestAttemptStore.AcceptWithinAsync(connection, transaction, summary.PaidEvidence, completion,
                    timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            }

            await VerifyCurrentPaidAsync(connection, transaction, current.Work, summary.PaidEvidence, LongRestProviderPayloadKind.Summary,
                summary.PayloadDigest, generation.Recipe.SummaryDestinationDigest, cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(connection, transaction, """
                INSERT INTO long_rest_accepted_summaries (WorkItemId, WorkDigest, GenerationId, NodeId, MembershipDigest, SourceDigest,
                    RecipeDigest, PayloadDigest, Summary, SummaryDigest, EvidenceJson, CreatedAtUtc)
                SELECT WorkItemId, WorkDigest, GenerationId, $node, $membership, SourceDigest, RecipeDigest, $payload, $summary, $digest, $evidence, $at
                FROM long_rest_work_items WHERE WorkDigest = $work AND DispositionCode = 1;
                """, cancellationToken, ("$node", Format(summary.NodeId)), ("$membership", summary.Fold.MembershipDigest.Bytes),
                ("$payload", summary.PayloadDigest.Bytes), ("$summary", summary.Summary), ("$digest", summary.SummaryDigest.Bytes),
                ("$evidence", StoredSummaryJson(summary)), ("$at", UtcInstantText.Format(timeProvider.GetUtcNow())),
                ("$work", LongRestDiscoveryHash.Work(current.Work).Value.Bytes)).ConfigureAwait(false);

            LongRestAcceptedHierarchySummary? persisted = await ReadAcceptedSummaryAsync(connection, transaction, current, generation.Recipe, cancellationToken).ConfigureAwait(false);

            if (persisted is null || (await ValidateFoldAuthorityAsync(current.Work, capability, cancellationToken).ConfigureAwait(false)).IsFailure)
            {
                throw new InvalidDataException();
            }

            if (completion is not null && (await LongRestLiveFenceReader.ReadWorkerWithinAsync(connection, transaction,
                generation, workerOwner ?? string.Empty, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false)).IsFailure)
            {
                throw new InvalidDataException();
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return persisted;
        }
        catch (Exception exception) when (StorageFailure(exception))
        {
            return Refuse<LongRestAcceptedHierarchySummary>(ErrorCodes.LongRest.StaleInput, "The accepted summary does not prove its exact current fold and paid completion.");
        }
    }

    public Task<Result<LongRestDiscoveryGeneration>> CommitFoldAsync(LongRestHierarchyPublication publication,
        LongRestMaintenanceCapability? capability, CancellationToken cancellationToken) =>
        CommitFoldCoreAsync(publication, null, null, capability, cancellationToken, false);

    public Task<Result<LongRestDiscoveryGeneration>> CommitFoldAsync(LongRestHierarchyPublication publication, LongRestDispatchCompletion completion, string workerOwner,
        LongRestMaintenanceCapability? capability, CancellationToken cancellationToken, bool committedOnly = false) =>
        CommitFoldCoreAsync(publication, completion, workerOwner, capability, cancellationToken, committedOnly);

    private async Task<Result<LongRestDiscoveryGeneration>> CommitFoldCoreAsync(LongRestHierarchyPublication publication, LongRestDispatchCompletion? completion, string? workerOwner,
        LongRestMaintenanceCapability? capability, CancellationToken cancellationToken, bool committedOnly)
    {
        try
        {
            if (publication?.AcceptedSummary?.Fold is null)
            {
                return Refuse<LongRestDiscoveryGeneration>(ErrorCodes.LongRest.InvalidRequest, "A fold publication requires an exact accepted stage.");
            }

            SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

            await using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);

            LongRestVerificationCounters.Transaction(deferred: false);

            LongRestAcceptedHierarchySummary supplied = publication.AcceptedSummary;

            LongRestDiscoveryGeneration? generation = await ReadGenerationAsync(connection, transaction, "GenerationId = $id", Format(supplied.GenerationId), cancellationToken).ConfigureAwait(false);

            if (generation is null || generation.Phase is LongRestSweepPhase.Capture or LongRestSweepPhase.Invalidated or LongRestSweepPhase.Deferred or LongRestSweepPhase.Retired)
            {
                return Refuse<LongRestDiscoveryGeneration>(ErrorCodes.LongRest.StaleInput, "The generation no longer owns publishable hierarchy work.");
            }

            LongRestHierarchyFold current = await ResolveCurrentFoldAsync(connection, transaction, generation, supplied.Fold.Layer,
                supplied.Fold.Group, capability, LongRestVerificationDepth.Step, cancellationToken).ConfigureAwait(false);

            LongRestAcceptedHierarchySummary accepted = await ReadAcceptedSummaryAsync(connection, transaction, current, generation.Recipe, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException();

            if (StoredSummaryJson(accepted) != StoredSummaryJson(supplied))
            {
                throw new InvalidDataException();
            }

            CovenantDigest payload = LongRestEmbeddingPayloadHash.Create(generation.Recipe.EmbeddingProvider, generation.Recipe.EmbeddingModel,
                generation.Recipe.EmbeddingDimensions, accepted.Summary).Value;

            if (publication.Vector.IsDefaultOrEmpty || publication.Vector.Length != generation.Recipe.EmbeddingDimensions
                || publication.Vector.Any(value => !float.IsFinite(value)) || publication.EmbeddingBinding.Provider != generation.Recipe.EmbeddingProvider
                || publication.EmbeddingBinding.Model != generation.Recipe.EmbeddingModel || publication.EmbeddingBinding.Dimensions != generation.Recipe.EmbeddingDimensions
                || EmbeddingBlobCodec.NormSquared(publication.Vector.AsSpan()) <= 0 || !double.IsFinite(EmbeddingBlobCodec.NormSquared(publication.Vector.AsSpan())))
            {
                throw new InvalidDataException();
            }

            byte[] vector = EmbeddingBlobCodec.Encode(publication.Vector.ToArray());

            if (new CovenantDigest(SHA256.HashData(vector)) != publication.EmbeddingBinding.ValueDigest)
            {
                throw new InvalidDataException();
            }

            await using (SqliteCommand replay = Command(connection, transaction,
                "SELECT DispositionCode FROM long_rest_work_items WHERE WorkDigest = $work;", ("$work", LongRestDiscoveryHash.Work(current.Work).Value.Bytes)))
            {
                if (await replay.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is long disposition && disposition == 2)
                {
                    if (completion is not null)
                    {
                        await LongRestAttemptStore.AcceptWithinAsync(connection, transaction, publication.EmbeddingPaidEvidence, completion,
                            timeProvider.GetUtcNow(), cancellationToken, committedOnly: true).ConfigureAwait(false);
                    }

                    await VerifyCurrentPaidAsync(connection, transaction, current.Work, publication.EmbeddingPaidEvidence, LongRestProviderPayloadKind.Embedding,
                        payload, generation.Recipe.EmbeddingDestinationDigest, cancellationToken).ConfigureAwait(false);

                    LongRestHierarchyInput existing = await ReadHierarchyNodeAsync(connection, transaction, generation, accepted.NodeId, capability, cancellationToken).ConfigureAwait(false);

                    if (!existing.Vector.AsSpan().SequenceEqual(publication.Vector.AsSpan()))
                    {
                        throw new InvalidDataException();
                    }

                    return generation;
                }
            }

            if (committedOnly) throw new InvalidDataException();

            if (generation.Phase != LongRestSweepPhase.Hierarchy || Math.Max(1, generation.FoldLayer) != current.Layer || generation.FoldGroup != current.Group)
            {
                throw new InvalidDataException();
            }

            if (completion is not null && (await LongRestLiveFenceReader.ReadWorkerWithinAsync(connection, transaction,
                generation, workerOwner ?? string.Empty, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false)).IsFailure)
            {
                throw new InvalidDataException();
            }

            if (completion is not null)
            {
                await LongRestAttemptStore.AcceptWithinAsync(connection, transaction, publication.EmbeddingPaidEvidence, completion,
                    timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            }

            await VerifyCurrentPaidAsync(connection, transaction, current.Work, publication.EmbeddingPaidEvidence, LongRestProviderPayloadKind.Embedding,
                payload, generation.Recipe.EmbeddingDestinationDigest, cancellationToken).ConfigureAwait(false);

            await ExecuteAsync(connection, transaction, """
                INSERT INTO long_rest_discovery_nodes (NodeId, GenerationId, Layer, GroupOrdinal, MembershipDigest, SourceDigest, RecipeDigest,
                    Summary, SummaryDigest, SensitivityCode, ProvenanceJson, PartitionDigest, Revision)
                VALUES ($node, $generation, $layer, $group, $membership, $source, $recipe, $summary, $digest, $sensitivity, $provenance, $partition, 0);
                """, cancellationToken, ("$node", Format(accepted.NodeId)), ("$generation", Format(generation.GenerationId)), ("$layer", current.Layer),
                ("$group", current.Group), ("$membership", current.MembershipDigest.Bytes), ("$source", current.Work.SourceDigest.Bytes),
                ("$recipe", current.Work.RecipeDigest.Bytes), ("$summary", accepted.Summary), ("$digest", accepted.SummaryDigest.Bytes),
                ("$sensitivity", (int)accepted.Sensitivity), ("$provenance", generation.Scope.ProtectedPartitionDigest is null ? null : JsonSerializer.Serialize(accepted.Provenance, LongRestJsonContext.Default.GenerationProvenance)),
                ("$partition", generation.Scope.ProtectedPartitionDigest?.Bytes)).ConfigureAwait(false);

            await ExecuteAsync(connection, transaction, """
                INSERT INTO long_rest_discovery_embeddings (NodeId, Provider, Model, Dimensions, ValueDigest, Vector, PaidEvidenceJson)
                VALUES ($node, $provider, $model, $dimensions, $value, $vector, $paid);
                """, cancellationToken, ("$node", Format(accepted.NodeId)), ("$provider", generation.Recipe.EmbeddingProvider),
                ("$model", generation.Recipe.EmbeddingModel), ("$dimensions", generation.Recipe.EmbeddingDimensions),
                ("$value", publication.EmbeddingBinding.ValueDigest.Bytes), ("$vector", vector),
                ("$paid", JsonSerializer.Serialize(LongRestStoredPaid.From(publication.EmbeddingPaidEvidence), LongRestJsonContext.Default.LongRestStoredPaid))).ConfigureAwait(false);

            for (int index = 0; index < current.Children.Length; index++)
            {
                await ExecuteAsync(connection, transaction, "INSERT INTO long_rest_discovery_edges VALUES ($generation, $parent, $ordinal, $child);",
                    cancellationToken, ("$generation", Format(generation.GenerationId)), ("$parent", Format(accepted.NodeId)),
                    ("$ordinal", index + 1), ("$child", Format(current.Children[index].NodeId))).ConfigureAwait(false);
            }

            if (generation.Scope.ProtectedPartitionDigest is not null)
            {
                Result<LabeledArtifactWriteReceipt> label = await ArtifactSensitivityLedger.WriteWithinAsync(connection, transaction,
                    new DerivedArtifactWrite(SensitiveArtifactKind.LongRestDiscovery, accepted.NodeId, null, generation.Scope.CampaignId, null, 0,
                        accepted.SummaryDigest, accepted.Sensitivity, accepted.Provenance,
                        producingMaintenanceReceiptDigest: publication.EmbeddingPaidEvidence.Attempt.DisclosureReceiptDigest), cancellationToken).ConfigureAwait(false);

                if (label.IsFailure)
                {
                    throw new InvalidDataException();
                }
            }

            // A summary is not indexed for text search: lexical discovery ranks captured leaves only, so an indexed summary
            // would be plaintext that only costs space and a retention surface.
            await using SqliteCommand finish = Command(connection, transaction,
                "UPDATE long_rest_work_items SET DispositionCode = 2, Revision = Revision + 1 WHERE WorkDigest = $work AND DispositionCode = 1;",
                ("$work", LongRestDiscoveryHash.Work(current.Work).Value.Bytes));

            if (await finish.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) != 1
                || (await ValidateFoldAuthorityAsync(current.Work, capability, cancellationToken).ConfigureAwait(false)).IsFailure)
            {
                throw new InvalidDataException();
            }

            LongRestDiscoveryGeneration committed = await MoveHierarchyCursorAsync(connection, transaction, generation, current.Layer,
                checked(current.Group + 1), LongRestSweepPhase.Hierarchy, cancellationToken).ConfigureAwait(false);

            if (completion is not null && (await LongRestLiveFenceReader.ReadWorkerWithinAsync(connection, transaction,
                generation, workerOwner ?? string.Empty, timeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false)).IsFailure)
            {
                throw new InvalidDataException();
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            return committed;
        }
        catch (Exception exception) when (StorageFailure(exception))
        {
            return Refuse<LongRestDiscoveryGeneration>(ErrorCodes.LongRest.StaleInput, "The fold could not atomically publish exact accepted summary, paid embedding, children and cursor.");
        }
    }

    private static async Task<LongRestHierarchyFold> ResolveCurrentFoldAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestDiscoveryGeneration generation, int layer, long group, LongRestMaintenanceCapability? capability, LongRestVerificationDepth depth,
        CancellationToken cancellationToken)
    {
        if (layer is < 1 or > 6 || group < 0 || (await ValidateReadAuthorityAsync(generation, capability, cancellationToken).ConfigureAwait(false)).IsFailure)
        {
            throw new InvalidDataException();
        }

        await VerifyFrozenSourcesAsync(connection, transaction, generation, capability, depth, cancellationToken).ConfigureAwait(false);

        ImmutableArray<LongRestHierarchyInput> page = await ReadFrontierPageAsync(connection, transaction, generation, layer,
            group / LongRestDiscoveryLimits.SourcePage, capability, cancellationToken).ConfigureAwait(false);

        var planned = LongRestHierarchy.PlanPage(page, layer, LongRestDiscoveryHash.Recipe(generation.Recipe).Value,
            generation.Recipe.ProviderInputUtf8Bytes, cancellationToken);

        int slot = (int)(group % LongRestDiscoveryLimits.SourcePage);

        if (planned.IsFailure || slot >= planned.Value.Length || planned.Value[slot].Length < 2)
        {
            throw new InvalidDataException();
        }

        LongRestHierarchyFold fold = LongRestHierarchyBuilder.CreateFold(generation, layer, group, planned.Value[slot]);

        if ((await ValidateFoldAuthorityAsync(fold.Work, capability, cancellationToken).ConfigureAwait(false)).IsFailure)
        {
            throw new InvalidDataException();
        }

        return fold;
    }

    private static async Task<LongRestAcceptedHierarchySummary?> ReadAcceptedSummaryAsync(SqliteConnection connection, SqliteTransaction transaction,
        LongRestHierarchyFold current, LongRestRecipeIdentity recipe, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT accepted.EvidenceJson, accepted.WorkDigest, accepted.NodeId, accepted.MembershipDigest, accepted.SourceDigest,
                accepted.RecipeDigest, accepted.PayloadDigest, accepted.Summary, accepted.SummaryDigest, accepted.GenerationId
            FROM long_rest_accepted_summaries accepted JOIN long_rest_work_items unit ON unit.WorkItemId = accepted.WorkItemId
            WHERE unit.GenerationId = $generation AND unit.OperationId = $operation AND unit.KindCode = 2 AND unit.ItemId = $item;
            """, ("$generation", Format(current.Work.GenerationId)), ("$operation", current.Work.OperationId.ToString("N")), ("$item", current.Work.ItemId));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        LongRestAcceptedHierarchySummary summary = (JsonSerializer.Deserialize(reader.GetString(0), LongRestJsonContext.Default.LongRestStoredSummary)
            ?? throw new InvalidDataException()).ToDomain();

        ValidateSummary(summary, current with { Work = summary.Fold.Work }, recipe);

        await ValidateStoredWorkAssociationAsync(connection, transaction, summary.Fold.Work, current.Work, cancellationToken).ConfigureAwait(false);

        if (new CovenantDigest((byte[])reader[1]) != LongRestDiscoveryHash.Work(summary.Fold.Work).Value
            || reader.GetString(2) != Format(summary.NodeId) || new CovenantDigest((byte[])reader[3]) != summary.Fold.MembershipDigest
            || new CovenantDigest((byte[])reader[4]) != summary.Fold.Work.SourceDigest || new CovenantDigest((byte[])reader[5]) != summary.RecipeDigest
            || new CovenantDigest((byte[])reader[6]) != summary.PayloadDigest || reader.GetString(7) != summary.Summary
            || new CovenantDigest((byte[])reader[8]) != summary.SummaryDigest || reader.GetString(9) != Format(summary.GenerationId))
        {
            throw new InvalidDataException();
        }

        return summary;
    }

    private static void ValidateSummary(LongRestAcceptedHierarchySummary summary, LongRestHierarchyFold current, LongRestRecipeIdentity recipe)
    {
        LongRestWorkIdentity historical = summary.Fold.Work;

        if (summary.GenerationId != current.Work.GenerationId || summary.NodeId == Guid.Empty
            || LongRestDiscoveryHash.Work(historical).IsFailure || historical with { Fence = current.Work.Fence } != current.Work
            || (historical.Fence != current.Work.Fence && !CanReadmit(historical.Fence, current.Work.Fence))
            || summary.Fold.Layer != current.Layer || summary.Fold.Group != current.Group || summary.Fold.MembershipDigest != current.MembershipDigest
            || LongRestDiscoveryHash.HierarchyFold(summary.Fold).IsFailure || LongRestDiscoveryHash.HierarchyFold(summary.Fold).Value != current.Work.SourceDigest
            || summary.RecipeDigest != current.Work.RecipeDigest || summary.RecipeDigest != LongRestDiscoveryHash.Recipe(recipe).Value
            || string.IsNullOrWhiteSpace(summary.Summary) || HierarchyUtf8.GetByteCount(summary.Summary) > LongRestDiscoveryLimits.SummaryUtf8Bytes
            || summary.SummaryDigest != DerivedArtifactContentDigest.ForText(summary.Summary)
            || summary.Sensitivity != current.Children[0].Sensitivity || !SameProvenance(summary.Sensitivity, summary.Provenance, current.Children[0].Provenance)
            || !summary.LabelDigests.OrderBy(digest => digest.ToString(), StringComparer.Ordinal).SequenceEqual(current.Children.SelectMany(child => child.LabelDigests).Distinct().OrderBy(digest => digest.ToString(), StringComparer.Ordinal))
            || summary.Fold.Children.Length != current.Children.Length)
        {
            throw new InvalidDataException();
        }

        foreach (LongRestHierarchyChild child in current.Children)
        {
            LongRestHierarchyChild original = summary.Fold.Children.Single(item => item.NodeId == child.NodeId);

            if (original.Content != child.Content)
            {
                throw new InvalidDataException();
            }
        }

        ValidatePaidShape(summary.PaidEvidence, historical, LongRestProviderPayloadKind.Summary, summary.PayloadDigest, recipe.SummaryDestinationDigest);
    }

    private static bool SameProvenance(ContentSensitivity sensitivity, GenerationProvenance left, GenerationProvenance right) =>
        CovenantDigests.Sensitivity(left.ToDigestInput(sensitivity)) == CovenantDigests.Sensitivity(right.ToDigestInput(sensitivity));

    private static void ValidatePaidShape(LongRestPaidEvidence paid, LongRestWorkIdentity work, LongRestProviderPayloadKind kind,
        CovenantDigest payload, CovenantDigest destination)
    {
        if (paid is null || paid.Attempt is null || paid.Attempt.AttemptId == Guid.Empty || paid.Attempt.PhysicalOrdinal == 0
            || paid.Attempt.PhysicalOrdinal > long.MaxValue || paid.Attempt.Revision < 0 || paid.InputTokens < 0 || paid.OutputTokens < 0 || paid.CostUsd < 0
            || paid.Attempt.State != LongRestDispatchAttemptState.Accepted || paid.Attempt.PayloadKind != kind
            || paid.Attempt.PayloadDigest != payload || !payload.IsValid || paid.Attempt.DestinationDigest != destination
            || LongRestDiscoveryHash.Work(paid.Attempt.Work).IsFailure || LongRestDiscoveryHash.Work(paid.Attempt.Work).Value != LongRestDiscoveryHash.Work(work).Value
            || (work.Scope.ProtectedPartitionDigest is not null && paid.Attempt.DisclosureReceiptDigest is not { IsValid: true }))
        {
            throw new InvalidDataException();
        }
    }

    private static async Task VerifyCurrentPaidAsync(SqliteConnection connection, SqliteTransaction transaction, LongRestWorkIdentity work,
        LongRestPaidEvidence paid, LongRestProviderPayloadKind kind, CovenantDigest payload, CovenantDigest destination, CancellationToken cancellationToken)
    {
        ValidatePaidShape(paid, work, kind, payload, destination);

        LongRestDispatchAttempt attempt = paid.Attempt;

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT effect.CompletionJson, effect.OriginalWorkJson FROM long_rest_provider_effects effect JOIN long_rest_work_items unit ON unit.WorkItemId = effect.WorkItemId
            WHERE unit.WorkDigest = $work AND effect.WorkDigest = $work AND unit.OperationId = $operation AND unit.GenerationId = $generation
                AND unit.ItemId = $item AND unit.KindCode = $workKind AND effect.AttemptId = $attempt AND effect.PayloadKindCode = $kind
                AND effect.PhysicalOrdinal = $ordinal AND effect.Revision = $revision AND effect.StateCode = 4
                AND effect.PayloadDigest = $payload AND effect.DestinationDigest = $destination AND effect.DisclosureReceiptDigest IS $receipt;
            """, ("$work", LongRestDiscoveryHash.Work(work).Value.Bytes), ("$operation", work.OperationId.ToString("N")),
            ("$generation", Format(work.GenerationId)), ("$item", work.ItemId), ("$attempt", Format(attempt.AttemptId)),
            ("$kind", (int)kind), ("$workKind", (int)work.Kind), ("$ordinal", checked((long)attempt.PhysicalOrdinal)), ("$revision", attempt.Revision),
            ("$payload", payload.Bytes), ("$destination", destination.Bytes), ("$receipt", attempt.DisclosureReceiptDigest?.Bytes));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.IsDBNull(0)) throw new InvalidDataException();

        string json = reader.GetString(0);

        if (HierarchyUtf8.GetByteCount(json) > 4096
            || LongRestAttemptStore.ReadOriginalWork(reader.GetString(1), LongRestDiscoveryHash.Work(work).Value) != work)
        {
            throw new InvalidDataException();
        }

        LongRestDispatchCompletion completion = JsonSerializer.Deserialize(json, LongRestJsonContext.Default.LongRestDispatchCompletion) ?? throw new InvalidDataException();

        if (completion.State != LongRestDispatchAttemptState.Accepted || completion.Usage is null
            || completion.Usage.InputTokens != paid.InputTokens || completion.Usage.OutputTokens != paid.OutputTokens || completion.CostUsd != paid.CostUsd
            || completion.Usage.TotalTokens < 0 || completion.Usage.CachedInputTokens < 0 || completion.Usage.ReasoningTokens < 0 || completion.FailureCode is not null)
        {
            throw new InvalidDataException();
        }
    }

    private static string StoredSummaryJson(LongRestAcceptedHierarchySummary summary) =>
        JsonSerializer.Serialize(LongRestStoredSummary.From(summary), LongRestJsonContext.Default.LongRestStoredSummary);
}
