using System.Data.Common;
using Microsoft.Extensions.Logging;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Data.Annals;

namespace RetroDownfall.Arcanum.Infrastructure.Lexicon;

internal sealed partial class LexiconService
{
    private const string VersionColumns = """
        v.VersionId, v.ClaimId, v.Sequence, v.Revision, v.OperationCode, v.OriginCode,
        v.ScopeKindCode, v.CampaignId, v.SensitivityCode, v.ContentHashFormatCode, v.ContentHash,
        v.ValidFromUtc, v.ValidToUtc, v.RecordedAtUtc,
        (SELECT s.RecordedAtUtc FROM annal_versions s WHERE s.PredecessorVersionId = v.VersionId) AS RecordedUntilUtc,
        v.PredecessorVersionId
        """;

    public Task<Result<LexiconInspectionResult<LexiconEntryDetail>>> ShowExactAsync(
        LexiconCurationScope scope,
        string name,
        ICovenantSnapshotReadLease? readLease,
        CancellationToken cancellationToken = default) =>
        InspectNamedAsync(scope, name, readLease, scope,
            "WHERE NameNormalized = @name AND ScopeCampaignId = @scope LIMIT 1", cancellationToken);

    public Task<Result<LexiconInspectionResult<LexiconEntryDetail>>> ShowEffectiveAsync(
        LexiconCurationScope requestedScope,
        string name,
        ICovenantSnapshotReadLease? installationReadLease,
        CancellationToken cancellationToken = default) =>
        InspectNamedAsync(requestedScope, name, installationReadLease,
            requestedScope?.Kind == LexiconScopeKind.Global ? requestedScope : null,
            "WHERE NameNormalized = @name AND RetiredAtUtc IS NULL AND ScopeCampaignId IN (@scope, '') "
                + "ORDER BY CASE WHEN ScopeCampaignId = @scope THEN 0 ELSE 1 END LIMIT 1", cancellationToken);

    private Task<Result<LexiconInspectionResult<LexiconEntryDetail>>> InspectNamedAsync(
        LexiconCurationScope scope,
        string name,
        ICovenantSnapshotReadLease? readLease,
        LexiconCurationScope? requiredLeaseScope,
        string selection,
        CancellationToken cancellationToken)
    {
        if (scope is null || scope.Validate().IsFailure)
        {
            return Task.FromResult(Result<LexiconInspectionResult<LexiconEntryDetail>>.Failure(
                new Error(ErrorCodes.Lexicon.InvalidScope, "A valid exact Lexicon scope is required.")));
        }

        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > LexiconLimits.MaxNameLength)
        {
            return Task.FromResult(Result<LexiconInspectionResult<LexiconEntryDetail>>.Failure(
                new Error(ErrorCodes.Lexicon.InvalidName, "A valid Lexicon entity name is required.")));
        }

        return InInspectionSnapshotAsync(readLease, requiredLeaseScope, async connection =>
        {
            List<(Guid Id, LexiconCurationScope Scope)> selected = await ReadInspectionIdentitiesAsync(
                connection, selection, NormalizeName(name), ScopeKey(scope), cancellationToken).ConfigureAwait(false);

            if (selected.Count == 0)
            {
                throw new InspectionException(new Error(ErrorCodes.Lexicon.NotFound, "Lexicon entity was not found."));
            }

            (Guid id, LexiconCurationScope storedScope) = selected[0];

            ArtifactSensitivityLabel? label = await ReadVerifiedLabelAsync(connection, id, storedScope, cancellationToken).ConfigureAwait(false);

            RequireProtectedLease(label, readLease);

            InspectionRow row = await ReadInspectionRowAsync(connection, id, cancellationToken).ConfigureAwait(false);

            LexiconFactProvenance[] provenance = await ReadFactProvenanceAsync(connection, id, cancellationToken).ConfigureAwait(false);

            row = row with { Entry = row.Entry with { FactProvenance = provenance } };

            VerifyCurrentProvenance(row.Entry);

            AnnalClaimVersion? head = await ReadVerifiedHeadAsync(connection, row, label, cancellationToken).ConfigureAwait(false);

            AnnalClaimVersion[] history = head is null ? [] : await ReadInspectionHistoryAsync(connection, head, storedScope, cancellationToken).ConfigureAwait(false);

            LexiconAnnalFactProvenance[] historicalSources = head is null ? [] : await ReadHistoricalSourcesAsync(connection, head.ClaimId, cancellationToken).ConfigureAwait(false);

            LexiconEntryLifecycle lifecycle = new(row.Entry.RetiredAtUtc, row.Entry.PinnedAtUtc);

            LexiconCurationAnnalHead headTarget = head is null
                ? new(false, null, null, null, null, null, null)
                : new(true, head.ClaimId, head.VersionId, head.Revision, head.Operation,
                    head.ContentHashFormat, head.ContentHash is null ? null : Convert.ToHexString(head.ContentHash));

            LexiconCurationSensitivityLabel labelTarget = label is null
                ? new(false, null, null, null, null)
                : new(true, label.LabelId, label.ArtifactRevision,
                    Convert.ToHexString(label.ArtifactContentDigest.Bytes), label.Provenance);

            string digest = LexiconSnapshotDigest.ComputeHex(row.Canonical);

            LexiconCurationTarget target = new(storedScope, row.Canonical.NameNormalized, id,
                row.Entry.CurationGeneration, digest, lifecycle, headTarget, labelTarget);

            RequireIntegrity(target.Validate().IsSuccess);

            return new LexiconInspectionResult<LexiconEntryDetail>(
                new LexiconEntryDetail(row.Entry, storedScope, head?.Origin, lifecycle, row.Entry.Eligibility,
                    row.Entry.CurationGeneration, digest, target, history, historicalSources), label is not null);
        }, cancellationToken);
    }

    public Task<Result<LexiconInspectionResult<IReadOnlyList<LexiconEntryDto>>>> ListInspectionAsync(
        ICovenantSnapshotReadLease? readLease,
        CancellationToken cancellationToken = default) =>
        InInspectionSnapshotAsync<IReadOnlyList<LexiconEntryDto>>(readLease, requiredScope: null, async connection =>
        {
            List<(Guid Id, LexiconCurationScope Scope)> identities = await ReadInspectionIdentitiesAsync(
                connection, "ORDER BY Name COLLATE NOCASE, ScopeCampaignId, Id", null, null, cancellationToken).ConfigureAwait(false);

            // Validate authority before materializing any protected content. All identity, label, row,
            // provenance and head reads share this operation's deferred snapshot.
            Dictionary<Guid, ArtifactSensitivityLabel?> labels = [];

            foreach ((Guid id, LexiconCurationScope scope) in identities)
            {
                ArtifactSensitivityLabel? label = await ReadVerifiedLabelAsync(connection, id, scope, cancellationToken).ConfigureAwait(false);

                RequireProtectedLease(label, readLease);

                labels.Add(id, label);
            }

            Dictionary<Guid, LexiconFactProvenance[]> provenance = identities.Count == 0 ? []
                : await ReadAllFactProvenanceAsync(connection, cancellationToken).ConfigureAwait(false);

            List<LexiconEntryDto> entries = new(identities.Count);

            foreach ((Guid id, _) in identities)
            {
                InspectionRow row = await ReadInspectionRowAsync(connection, id, cancellationToken).ConfigureAwait(false);

                row = row with { Entry = row.Entry with { FactProvenance = provenance.GetValueOrDefault(id) ?? [] } };

                VerifyCurrentProvenance(row.Entry);

                _ = await ReadVerifiedHeadAsync(connection, row, labels[id], cancellationToken).ConfigureAwait(false);

                entries.Add(row.Entry);
            }

            return new LexiconInspectionResult<IReadOnlyList<LexiconEntryDto>>(entries, labels.Values.Any(label => label is not null));
        }, cancellationToken);

    private async Task<Result<LexiconInspectionResult<T>>> InInspectionSnapshotAsync<T>(
        ICovenantSnapshotReadLease? lease,
        LexiconCurationScope? requiredScope,
        Func<DbConnection, Task<LexiconInspectionResult<T>>> read,
        CancellationToken cancellationToken)
    {
        try
        {
            Require(await ValidateInspectionLeaseAsync(lease, requiredScope, cancellationToken).ConfigureAwait(false));

            return await SqliteBusyRetry.ExecuteAsync(async () =>
            {
                DbConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

                // Raw transaction control matches the existing scoped Lexicon connection owner. Every
                // helper receives this exact connection; none opens another connection or snapshot.
                await ExecuteNonQueryAsync(connection, cancellationToken, "BEGIN DEFERRED").ConfigureAwait(false);

                try
                {
                    LexiconInspectionResult<T> result = await read(connection).ConfigureAwait(false);

                    Require(await ValidateInspectionLeaseAsync(lease, requiredScope, cancellationToken).ConfigureAwait(false));

                    await ExecuteNonQueryAsync(connection, cancellationToken, "COMMIT").ConfigureAwait(false);

                    return Result<LexiconInspectionResult<T>>.Success(result);
                }
                catch
                {
                    await TryRollbackAsync(connection, "inspection").ConfigureAwait(false);

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
            logger.LogWarning(exception, "Lexicon inspection integrity verification failed.");

            return IntegrityError;
        }
    }

    private static async ValueTask<Result> ValidateInspectionLeaseAsync(
        ICovenantSnapshotReadLease? lease, LexiconCurationScope? required, CancellationToken cancellationToken)
    {
        if (lease is null)
        {
            return Result.Success();
        }

        CovenantOperationLeaseSnapshot snapshot = lease.Snapshot;

        bool matches = required is null
            ? snapshot.Kind == CovenantLeaseKind.InstallationRead && snapshot.Coverage == CovenantLeaseCoverage.Installation && snapshot.Scope is null
            : snapshot.Kind == CovenantLeaseKind.Read && snapshot.Coverage == CovenantLeaseCoverage.Scoped
                && snapshot.Scope is { IsInitialized: true } held
                && held.Kind == (required.Kind == LexiconScopeKind.Global ? CovenantScope.Global : CovenantScope.Campaign)
                && held.CampaignId == required.CampaignId;

        return matches ? await lease.RevalidateAsync(cancellationToken).ConfigureAwait(false) : LeaseError;
    }

    private static void RequireProtectedLease(ArtifactSensitivityLabel? label, ICovenantSnapshotReadLease? lease)
    {
        if (label is not null && lease is null)
        {
            throw new InspectionException(LeaseError);
        }
    }

    private static async Task<List<(Guid Id, LexiconCurationScope Scope)>> ReadInspectionIdentitiesAsync(
        DbConnection connection, string selection, string? name, string? scope, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = "SELECT Id, ScopeCampaignId FROM lexicon_entries " + selection;

        if (name is not null)
        {
            AddParameter(command, "@name", name);

            AddParameter(command, "@scope", scope!);
        }

        List<(Guid, LexiconCurationScope)> identities = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            Guid id = Guid.Parse(reader.GetString(0));

            RequireIntegrity(id != Guid.Empty);

            identities.Add((id, ScopeFromKey(reader.GetString(1))));
        }

        return identities;
    }

    private static async Task<InspectionRow> ReadInspectionRowAsync(DbConnection connection, Guid id, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT {SelectColumns}, NameNormalized, FactsText FROM lexicon_entries WHERE Id = @id";

        AddParameter(command, "@id", id.ToString("N"));

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        RequireIntegrity(await reader.ReadAsync(cancellationToken).ConfigureAwait(false));

        LexiconEntryDto entry = ReadEntry(reader);

        Result<LexiconCanonicalValue> canonical = LexiconValueNormalizer.NormalizeCorrection(entry.Name, entry.Type, entry.Facts);

        RequireIntegrity(canonical.IsSuccess);

        RequireIntegrity(entry.Id == id && entry.CurationGeneration > 0
            && entry.Name == canonical.Value.Name && entry.Type == canonical.Value.Type
            && entry.Facts.SequenceEqual(canonical.Value.Facts, StringComparer.Ordinal)
            && reader.GetString(9) == canonical.Value.NameNormalized && reader.GetString(10) == canonical.Value.FactsText);

        return new InspectionRow(entry, canonical.Value);
    }

    private static void VerifyCurrentProvenance(LexiconEntryDto entry)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (LexiconFactProvenance fact in entry.FactProvenance ?? [])
        {
            RequireIntegrity(entry.Facts.Contains(fact.Fact, StringComparer.Ordinal) && seen.Add(fact.Fact)
                && fact.Source.SessionId != Guid.Empty && fact.Source.AttachmentId != Guid.Empty
                && fact.Source.Version > 0 && !string.IsNullOrWhiteSpace(fact.Source.LogicalKey)
                && !string.IsNullOrWhiteSpace(fact.Source.ContentHash));
        }
    }

    private static async Task<AnnalClaimVersion?> ReadVerifiedHeadAsync(
        DbConnection connection, InspectionRow row, ArtifactSensitivityLabel? label, CancellationToken cancellationToken)
    {
        if (label is not null)
        {
            RequireIntegrity(label.ArtifactContentDigest == DerivedArtifactContentDigest.ForBytes(LexiconSnapshotDigest.Encode(row.Canonical)));
        }

        await using DbCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT c.ClaimId, h.CurrentVersionId, h.CurrentRevision, h.CurrentOperationCode, h.SubjectStoreCode
            FROM annal_claims c LEFT JOIN annal_heads h ON h.ClaimId = c.ClaimId
            WHERE c.SubjectStoreCode = 2 AND c.SubjectId = @id
            """;

        AddParameter(command, "@id", row.Entry.Id.ToString("N"));

        string claimId;

        string versionId;

        int revision;

        int operation;

        await using (DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                RequireIntegrity(row.Entry.RetiredAtUtc is null);

                return null;
            }

            RequireIntegrity(!reader.IsDBNull(1) && AnnalsStore.ReadCode(reader, 4, 2, 2) == 2);

            claimId = reader.GetString(0);

            versionId = reader.GetString(1);

            revision = AnnalsStore.ReadCode(reader, 2, 1, int.MaxValue);

            operation = AnnalsStore.ReadCode(reader, 3, 1, 3);
        }

        command.Parameters.Clear();

        // The summary validates only the current version. It never expands complete history.
        command.CommandText = """
            SELECT v.VersionId, v.ClaimId, v.Sequence, v.Revision, v.OperationCode, v.OriginCode,
                   v.ScopeKindCode, v.CampaignId, v.SensitivityCode, v.ContentHashFormatCode, v.ContentHash,
                   v.ValidFromUtc, v.ValidToUtc, v.RecordedAtUtc, NULL, v.PredecessorVersionId
            FROM annal_versions v WHERE v.VersionId = @version AND v.ClaimId = @claim
                AND NOT EXISTS (SELECT 1 FROM annal_versions later WHERE later.ClaimId = v.ClaimId AND later.Revision > v.Revision)
            """;

        AddParameter(command, "@version", versionId);

        AddParameter(command, "@claim", claimId);

        await using DbDataReader versionReader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        RequireIntegrity(await versionReader.ReadAsync(cancellationToken).ConfigureAwait(false));

        AnnalClaimVersion head = ReadInspectionVersion(versionReader);

        VerifyVersionScope(head, ScopeForEntry(row.Entry));

        RequireIntegrity(head.Revision == revision && (int)head.Operation == operation
            && (head.Operation == AnnalOperation.Retire) == (row.Entry.RetiredAtUtc is not null)
            && head.Sensitivity == (label?.Sensitivity ?? ContentSensitivity.None));

        if (head.Operation != AnnalOperation.Retire)
        {
            byte[] digest = head.ContentHashFormat == AnnalContentHashFormat.LegacyStoreDigest
                ? AnnalContentDigest.ForLexiconEntry(row.Canonical.Type, row.Canonical.FactsText)
                : LexiconSnapshotDigest.Compute(row.Canonical);

            RequireIntegrity(head.ContentHash!.AsSpan().SequenceEqual(digest));
        }

        return head;
    }

    private static async Task<AnnalClaimVersion[]> ReadInspectionHistoryAsync(
        DbConnection connection, AnnalClaimVersion head, LexiconCurationScope scope, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT {VersionColumns} FROM annal_versions v WHERE v.ClaimId = @claim ORDER BY v.Revision";

        AddParameter(command, "@claim", head.ClaimId);

        List<AnnalClaimVersion> versions = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            AnnalClaimVersion version = ReadInspectionVersion(reader);

            VerifyVersionScope(version, scope);

            RequireIntegrity(version.Revision == versions.Count + 1 && version.Sensitivity <= head.Sensitivity
                && version.PredecessorVersionId == (versions.Count == 0 ? null : versions[^1].VersionId));

            versions.Add(version);
        }

        RequireIntegrity(versions.Count == head.Revision && versions[^1].VersionId == head.VersionId);

        return [.. versions];
    }

    private static AnnalClaimVersion ReadInspectionVersion(DbDataReader reader)
    {
        AnnalClaimVersion version = new(reader.GetString(0), reader.GetString(1), ReadPositiveInteger(reader, 2), AnnalsStore.ReadCode(reader, 3, 1, int.MaxValue),
            (AnnalOperation)AnnalsStore.ReadCode(reader, 4, 1, 3), (AnnalOrigin)AnnalsStore.ReadCode(reader, 5, 1, 4),
            (SagaMemoryScopeKind)AnnalsStore.ReadCode(reader, 6, 0, 3), reader.IsDBNull(7) ? null : Guid.Parse(reader.GetString(7)),
            (ContentSensitivity)AnnalsStore.ReadCode(reader, 8, 0, 1), (AnnalContentHashFormat)AnnalsStore.ReadCode(reader, 9, 1, 2),
            reader.IsDBNull(10) ? null : (byte[])reader.GetValue(10), UtcInstantText.Parse(reader.GetString(11)),
            reader.IsDBNull(12) ? null : UtcInstantText.Parse(reader.GetString(12)), UtcInstantText.Parse(reader.GetString(13)),
            reader.IsDBNull(14) ? null : UtcInstantText.Parse(reader.GetString(14)), reader.IsDBNull(15) ? null : reader.GetString(15));

        RequireIntegrity(!string.IsNullOrWhiteSpace(version.VersionId) && !string.IsNullOrWhiteSpace(version.ClaimId)
            && version.Sequence > 0 && version.Revision > 0
            && (version.Operation == AnnalOperation.Retire ? version.ContentHash is null : version.ContentHash is { Length: 32 })
            && (version.Revision == 1 ? version.PredecessorVersionId is null : !string.IsNullOrWhiteSpace(version.PredecessorVersionId)));

        return version;
    }

    private static void VerifyVersionScope(AnnalClaimVersion version, LexiconCurationScope scope) =>
        RequireIntegrity(version.ScopeKind == (scope.Kind == LexiconScopeKind.Global ? SagaMemoryScopeKind.Global : SagaMemoryScopeKind.Campaign)
            && version.CampaignId == scope.CampaignId);

    private static async Task<LexiconAnnalFactProvenance[]> ReadHistoricalSourcesAsync(
        DbConnection connection, string claimId, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT p.AnnalVersionId, p.FactOrdinal, p.SessionId, p.AttachmentId, p.LogicalKey,
                   p.AttachmentVersion, p.AttachmentContentHash, p.MaterializedAt, p.SourceType,
                   v.ContentHashFormatCode, v.OperationCode
            FROM lexicon_annal_fact_provenance p JOIN annal_versions v ON v.VersionId = p.AnnalVersionId
            WHERE v.ClaimId = @claim ORDER BY v.Revision, p.FactOrdinal
            """;

        AddParameter(command, "@claim", claimId);

        List<LexiconAnnalFactProvenance> sources = [];

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            LexiconAnnalFactProvenance source = new(reader.GetString(0), AnnalsStore.ReadCode(reader, 1, 0, int.MaxValue), Guid.Parse(reader.GetString(2)),
                Guid.Parse(reader.GetString(3)), reader.GetString(4), AnnalsStore.ReadCode(reader, 5, 1, int.MaxValue), reader.GetString(6),
                UtcInstantText.Parse(reader.GetString(7)), reader.GetString(8));

            RequireIntegrity(source.FactOrdinal >= 0 && source.SessionId != Guid.Empty && source.AttachmentId != Guid.Empty
                && source.AttachmentVersion > 0 && !string.IsNullOrWhiteSpace(source.LogicalKey)
                && !string.IsNullOrWhiteSpace(source.AttachmentContentHash)
                && AnnalsStore.ReadCode(reader, 9, 2, 2) == 2 && AnnalsStore.ReadCode(reader, 10, 1, 2) is 1 or 2);

            sources.Add(source);
        }

        return [.. sources];
    }

    private static async Task<ArtifactSensitivityLabel?> ReadVerifiedLabelAsync(
        DbConnection connection, Guid id, LexiconCurationScope scope, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT LabelId, ArtifactKindCode, ArtifactId, SessionId, CampaignId, TurnId,
                   ArtifactRevision, ArtifactContentDigest, SensitivityCode, ProvenanceModeCode,
                   ExactGenerationIds, GenerationBloom, SensitivityDigest, ProducingPlanDigest,
                   ProducingAdmissionDigest, ProducingMaintenanceReceiptDigest, ArtifactLabelDigest, CreatedAtUtc
            FROM artifact_sensitivity WHERE ArtifactKindCode = 7 AND ArtifactId = @id
            """;

        AddParameter(command, "@id", id.ToString("D").ToUpperInvariant());

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        int mode = AnnalsStore.ReadCode(reader, 9, 1, 2);

        GenerationProvenance provenance;

        if (mode == (int)GenerationProvenanceMode.Exact)
        {
            RequireIntegrity(!reader.IsDBNull(10) && reader.IsDBNull(11));

            byte[] bytes = (byte[])reader.GetValue(10);

            RequireIntegrity(bytes.Length is >= 16 and <= 128 && bytes.Length % 16 == 0);

            List<Guid> generations = [];

            for (int offset = 0; offset < bytes.Length; offset += 16)
            {
                generations.Add(new Guid(bytes.AsSpan(offset, 16), bigEndian: true));
            }

            provenance = new GenerationProvenance(GenerationProvenanceMode.Exact, [.. generations], []);
        }
        else
        {
            RequireIntegrity(reader.IsDBNull(10) && !reader.IsDBNull(11));

            provenance = GenerationProvenance.CreateBloom((byte[])reader.GetValue(11));
        }

        ArtifactSensitivityLabel label = new(Guid.Parse(reader.GetString(0)),
            (SensitiveArtifactKind)AnnalsStore.ReadCode(reader, 1, 7, 7), Guid.Parse(reader.GetString(2)),
            ReadOptionalGuid(reader, 3), ReadOptionalGuid(reader, 4), ReadOptionalGuid(reader, 5),
            checked((ulong)ReadPositiveInteger(reader, 6)), new CovenantDigest((byte[])reader.GetValue(7)),
            (ContentSensitivity)AnnalsStore.ReadCode(reader, 8, 1, 1), provenance,
            new CovenantDigest((byte[])reader.GetValue(12)), ReadOptionalDigest(reader, 13),
            ReadOptionalDigest(reader, 14), ReadOptionalDigest(reader, 15),
            new CovenantDigest((byte[])reader.GetValue(16)), UtcInstantText.Parse(reader.GetString(17)));

        RequireIntegrity(label.ArtifactId == id && label.CampaignId == scope.CampaignId && label.ArtifactRevision > 0
            && (label.ProducingPlanDigest is null) == (label.ProducingAdmissionDigest is null));

        return label;
    }

    private static Guid? ReadOptionalGuid(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : Guid.Parse(reader.GetString(ordinal));

    private static long ReadPositiveInteger(DbDataReader reader, int ordinal) =>
        reader.GetValue(ordinal) is long value && value > 0 ? value : throw new InspectionException(IntegrityError);

    private static CovenantDigest? ReadOptionalDigest(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : new CovenantDigest((byte[])reader.GetValue(ordinal));

    private static string ScopeKey(LexiconCurationScope scope) => scope.CampaignId?.ToString("D") ?? string.Empty;

    private static LexiconCurationScope ScopeForEntry(LexiconEntryDto entry) => new(
        entry.ScopeCampaignId is null ? LexiconScopeKind.Global : LexiconScopeKind.Campaign, entry.ScopeCampaignId);

    private static LexiconCurationScope ScopeFromKey(string key)
    {
        LexiconCurationScope scope = new(key.Length == 0 ? LexiconScopeKind.Global : LexiconScopeKind.Campaign,
            key.Length == 0 ? null : Guid.Parse(key));

        RequireIntegrity(scope.Validate().IsSuccess);

        return scope;
    }

    private static Error IntegrityError => new(ErrorCodes.Lexicon.CurationIntegrityFailed, "Lexicon inspection evidence is inconsistent.");

    private static Error LeaseError => new(ErrorCodes.Covenant.ForbiddenAuthority, "Lexicon inspection requires the matching Covenant read capability.");

    private static void RequireIntegrity(bool condition)
    {
        if (!condition)
        {
            throw new InspectionException(IntegrityError);
        }
    }

    private static void Require(Result result)
    {
        if (result.IsFailure)
        {
            throw new InspectionException(result.Error);
        }
    }

    private sealed class InspectionException(Error error) : Exception(error.Message)
    {
        internal Error Error { get; } = error;
    }

    private sealed record InspectionRow(LexiconEntryDto Entry, LexiconCanonicalValue Canonical);
}
