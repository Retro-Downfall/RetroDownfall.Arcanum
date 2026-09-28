using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;

namespace RetroDownfall.Arcanum.Infrastructure.Lexicon;

internal sealed partial class LexiconService
{
    public async Task<Result<LexiconCurationResult>> CorrectAsync(
        LexiconCurationTarget target,
        LexiconReplacementContent replacement,
        CovenantWriteLease? writeLease,
        CancellationToken cancellationToken = default)
    {
        if (target is null || target.Validate().IsFailure)
        {
            return new Error(ErrorCodes.Lexicon.InvalidCurationTarget, "A complete Lexicon target is required.");
        }

        Result<LexiconCanonicalValue> normalized = LexiconValueNormalizer.NormalizeCorrection(
            target.NormalizedName, replacement?.Type, replacement?.Facts);

        if (normalized.IsFailure)
        {
            return normalized.Error;
        }

        if (target.SensitivityLabel.IsPresent && writeLease is null)
        {
            return new Error(ErrorCodes.Lexicon.ProtectedMutationRefused,
                "A protected Lexicon correction requires an exact-scope write capability.");
        }

        try
        {
            Require(await ValidateCurationLeaseAsync(writeLease, target.Scope, cancellationToken).ConfigureAwait(false));

            return await SqliteBusyRetry.ExecuteAsync(async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                await ExecuteNonQueryAsync(connection, cancellationToken, "BEGIN IMMEDIATE").ConfigureAwait(false);

                try
                {
                    CurationState state = await ReadCurationStateAsync(connection, target, cancellationToken).ConfigureAwait(false);

                    if (!TargetsEqual(target, state.Detail.Target))
                    {
                        throw new InspectionException(StaleTargetError);
                    }

                    if (state.Row.Entry.CurationGeneration == long.MaxValue)
                    {
                        throw new InspectionException(new Error(ErrorCodes.Lexicon.CurationGenerationExhausted,
                            "The Lexicon curation generation is exhausted."));
                    }

                    if (state.Row.Entry.RetiredAtUtc is not null)
                    {
                        throw new InspectionException(new Error(ErrorCodes.Lexicon.RetiredMutationRefused,
                            "A retired Lexicon entry must be reinstated before correction."));
                    }

                    if (state.Label is not null && writeLease is null)
                    {
                        throw new InspectionException(new Error(ErrorCodes.Lexicon.ProtectedMutationRefused,
                            "A protected Lexicon correction requires an exact-scope write capability."));
                    }

                    LexiconCurationOutcomeKind outcome = LexiconCurationOutcomeKind.Unchanged;

                    if (state.Row.Canonical.Type != normalized.Value.Type
                        || !state.Row.Canonical.Facts.SequenceEqual(normalized.Value.Facts, StringComparer.Ordinal))
                    {
                        if (state.Label?.ArtifactRevision >= (ulong)long.MaxValue)
                        {
                            throw new InspectionException(new Error(ErrorCodes.Lexicon.ArtifactRevisionExhausted,
                                "The Lexicon artifact revision is exhausted."));
                        }

                        DateTimeOffset now = DateTimeOffset.UtcNow;

                        now = now > state.Row.Entry.UpdatedAt ? now : state.Row.Entry.UpdatedAt.AddTicks(1);

                        await EnsureCurationBaselineAsync(connection, state, now, cancellationToken).ConfigureAwait(false);

                        await ReplaceCanonicalAsync(connection, state.Row, normalized.Value, now, cancellationToken).ConfigureAwait(false);

                        _ = await ReplaceFactProvenanceAsync(connection, target.EntryId, state.Row.Entry.FactProvenance ?? [],
                            [], normalized.Value.Facts, null, cancellationToken).ConfigureAwait(false);

                        if (state.Label is { } label)
                        {
                            Result<ArtifactSensitivityLabel> replaced = await ArtifactSensitivityLedger.ReplaceLexiconWithinAsync(
                                (SqliteConnection)connection, null, label,
                                DerivedArtifactContentDigest.ForBytes(LexiconSnapshotDigest.Encode(normalized.Value)),
                                now, cancellationToken).ConfigureAwait(false);

                            if (replaced.IsFailure)
                            {
                                throw new InspectionException(replaced.Error);
                            }
                        }

                        _ = await AppendCurationContentAsync(connection, state, normalized.Value,
                            AnnalOrigin.OperatorStated, now, now, cancellationToken).ConfigureAwait(false);

                        state = await ReadCurationStateAsync(connection, target, cancellationToken).ConfigureAwait(false);

                        outcome = LexiconCurationOutcomeKind.Applied;
                    }

                    Require(await ValidateCurationLeaseAsync(writeLease, target.Scope, cancellationToken).ConfigureAwait(false));

                    await ExecuteNonQueryAsync(connection, cancellationToken, "COMMIT").ConfigureAwait(false);

                    return Result<LexiconCurationResult>.Success(new(outcome, state.Detail));
                }
                catch
                {
                    await TryRollbackAsync(connection, "correction").ConfigureAwait(false);

                    throw;
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (InspectionException exception)
        {
            return exception.Error;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Lexicon correction failed.");

            return new Error(ErrorCodes.Lexicon.WriteFailed, "The Lexicon correction could not be persisted.");
        }
    }

    private static async ValueTask<Result> ValidateCurationLeaseAsync(
        CovenantWriteLease? lease, LexiconCurationScope scope, CancellationToken cancellationToken)
    {
        if (lease is null)
        {
            return Result.Success();
        }

        CovenantOperationLeaseSnapshot held = lease.Snapshot;

        return held.Kind == CovenantLeaseKind.Write && held.Coverage == CovenantLeaseCoverage.Scoped
            && held.Scope is { IsInitialized: true } exact
            && exact.Kind == (scope.Kind == LexiconScopeKind.Global ? CovenantScope.Global : CovenantScope.Campaign)
            && exact.CampaignId == scope.CampaignId
            ? await lease.RevalidateAsync(cancellationToken).ConfigureAwait(false)
            : new Error(ErrorCodes.Covenant.ForbiddenAuthority, "Lexicon correction requires a matching exact-scope write capability.");
    }

    private static async Task<CurationState> ReadCurationStateAsync(
        DbConnection connection, LexiconCurationTarget target, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadCurationStateCoreAsync(connection, target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException
            or InvalidCastException or InvalidOperationException or System.Text.Json.JsonException)
        {
            // Materializers verify digest-bound label fields as well as relational evidence.
            // Malformed persisted bytes are integrity refusals, before desired-state handling.
            throw new InspectionException(IntegrityError);
        }
    }

    private static async Task<CurationState> ReadCurationStateCoreAsync(
        DbConnection connection, LexiconCurationTarget target, CancellationToken cancellationToken)
    {
        List<(Guid Id, LexiconCurationScope Scope)> identities = await ReadInspectionIdentitiesAsync(connection,
            "WHERE NameNormalized = @name AND ScopeCampaignId = @scope LIMIT 1",
            target.NormalizedName, ScopeKey(target.Scope), cancellationToken).ConfigureAwait(false);

        if (identities.Count != 1 || identities[0].Id != target.EntryId)
        {
            throw new InspectionException(StaleTargetError);
        }

        ArtifactSensitivityLabel? label = await ReadVerifiedLabelAsync(connection, target.EntryId, target.Scope, cancellationToken).ConfigureAwait(false);

        if (label is not null && !target.SensitivityLabel.IsPresent)
        {
            throw new InspectionException(StaleTargetError);
        }

        InspectionRow row = await ReadInspectionRowAsync(connection, target.EntryId, cancellationToken).ConfigureAwait(false);

        row = row with { Entry = row.Entry with { FactProvenance = await ReadFactProvenanceAsync(connection, target.EntryId, cancellationToken).ConfigureAwait(false) } };

        VerifyCurrentProvenance(row.Entry);

        AnnalClaimVersion? head = await ReadVerifiedHeadAsync(connection, row, label, cancellationToken).ConfigureAwait(false);

        AnnalClaimVersion[] history = head is null ? [] : await ReadInspectionHistoryAsync(connection, head, target.Scope, cancellationToken).ConfigureAwait(false);

        LexiconAnnalFactProvenance[] sources = head is null ? [] : await ReadHistoricalSourcesAsync(connection, head.ClaimId, cancellationToken).ConfigureAwait(false);

        LexiconEntryLifecycle lifecycle = new(row.Entry.RetiredAtUtc, row.Entry.PinnedAtUtc);

        string digest = LexiconSnapshotDigest.ComputeHex(row.Canonical);

        LexiconCurationTarget current = new(target.Scope, row.Canonical.NameNormalized, row.Entry.Id,
            row.Entry.CurationGeneration, digest, lifecycle,
            head is null ? new(false, null, null, null, null, null, null)
                : new(true, head.ClaimId, head.VersionId, head.Revision, head.Operation, head.ContentHashFormat,
                    head.ContentHash is null ? null : Convert.ToHexString(head.ContentHash)),
            label is null ? new(false, null, null, null, null)
                : new(true, label.LabelId, label.ArtifactRevision, Convert.ToHexString(label.ArtifactContentDigest.Bytes), label.Provenance));

        RequireIntegrity(current.Validate().IsSuccess);

        return new(row, label, head, new(row.Entry, target.Scope, head?.Origin, lifecycle,
            row.Entry.Eligibility, row.Entry.CurationGeneration, digest, current, history, sources));
    }

    private static bool TargetsEqual(LexiconCurationTarget expected, LexiconCurationTarget actual) =>
        expected.Scope == actual.Scope && expected.NormalizedName == actual.NormalizedName
        && expected.EntryId == actual.EntryId && expected.CurationGeneration == actual.CurationGeneration
        && string.Equals(expected.SnapshotDigest, actual.SnapshotDigest, StringComparison.OrdinalIgnoreCase)
        && expected.Lifecycle == actual.Lifecycle
        && expected.AnnalHead == actual.AnnalHead
        && expected.SensitivityLabel == actual.SensitivityLabel;

    private static async Task ReplaceCanonicalAsync(
        DbConnection connection, InspectionRow row, LexiconCanonicalValue replacement, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        // The trigger publishes the complete active FTS representation in this same transaction.
        command.CommandText = """
            UPDATE lexicon_entries SET Type = @type, FactsJson = @facts, FactsText = @text,
                UpdatedAt = @at, CurationGeneration = CurationGeneration + 1
            WHERE Id = @id AND CurationGeneration = @generation
            """;

        AddParameter(command, "@type", replacement.Type);

        AddParameter(command, "@facts", replacement.FactsJson);

        AddParameter(command, "@text", replacement.FactsText);

        AddParameter(command, "@at", UtcInstantText.Format(now));

        AddParameter(command, "@id", row.Entry.Id.ToString("N"));

        AddParameter(command, "@generation", row.Entry.CurationGeneration);

        RequireIntegrity(await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1);
    }

    private static async Task EnsureCurationBaselineAsync(
        DbConnection connection, CurationState state, DateTimeOffset recordedAt, CancellationToken cancellationToken)
    {
        if (state.Head?.ContentHashFormat == AnnalContentHashFormat.LexiconStructuredSnapshot)
        {
            return;
        }

        // Baselines capture pre-mutation source ordinals. Operator evidence is always recorded,
        // independently of the ordinary agent-capture feature switch.
        _ = await AppendCurationContentAsync(connection, state, state.Row.Canonical,
            state.Head is null ? AnnalOrigin.AgentAsserted : AnnalOrigin.SystemBackfilled,
            state.Row.Entry.UpdatedAt, recordedAt, cancellationToken).ConfigureAwait(false);
    }

    private static Task<string?> AppendCurationContentAsync(
        DbConnection connection, CurationState state, LexiconCanonicalValue content,
        AnnalOrigin origin, DateTimeOffset validFrom, DateTimeOffset recordedAt, CancellationToken cancellationToken) =>
        AnnalsClaimWriter.AppendCorrectionAsync(connection, null, AnnalSubjectStore.Lexicon,
            state.Row.Entry.Id.ToString("N"), origin,
            state.Detail.Scope.Kind == LexiconScopeKind.Global ? SagaMemoryScopeKind.Global : SagaMemoryScopeKind.Campaign,
            state.Detail.Scope.CampaignId?.ToString("D"), state.Label?.Sensitivity ?? ContentSensitivity.None,
            AnnalContentHashFormat.LexiconStructuredSnapshot, LexiconSnapshotDigest.Compute(content),
            validFrom, recordedAt, null, cancellationToken);

    private static Error StaleTargetError => new(ErrorCodes.Lexicon.StaleCurationTarget, "The inspected Lexicon target has changed.");

    private sealed record CurationState(InspectionRow Row, ArtifactSensitivityLabel? Label, AnnalClaimVersion? Head, LexiconEntryDetail Detail);
}
