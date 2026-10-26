using System.Collections.Frozen;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.Data.Sqlite;

using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;
using RetroDownfall.Arcanum.Infrastructure.Data.Schema;

namespace RetroDownfall.Arcanum.Infrastructure.Data.LongRest;

/// <summary>
/// Bounded, content-free report aggregates over the caller's existing read snapshot. One supported
/// single-source experiment supplies one observation about that exact retained version; it proves
/// neither universal usefulness nor permission to promote its source's authority.
/// </summary>
internal static class SagaRankingEvidenceReader
{
    private static readonly ConditionalWeakTable<SQLitePCL.sqlite3, EvidenceSlot> Slots = new();

    /// <summary>Owns a read snapshot, keeping only evidence proved unchanged through its commit.</summary>
    internal static async Task<T> InSnapshotAsync<T>(DbConnection connection, Func<Task<T>> read, CancellationToken ct, bool enabled = true)
    {
        if (!enabled || connection is not SqliteConnection sqlite)
        {
            return await GrimoireCoreSchemaVersion.InSnapshotAsync(connection, read, ct).ConfigureAwait(false);
        }

        if (MemoryErasureEvidence.InTransaction(sqlite))
        {
            return await read().ConfigureAwait(false);
        }

        EvidenceSlot slot = Slots.GetOrCreateValue(sqlite.Handle!);

        ChangeStamp before = await ReadStampAsync(sqlite, ct).ConfigureAwait(false);

        try
        {
            T result = await GrimoireCoreSchemaVersion.InSnapshotAsync(connection, async () =>
            {
                // This actual table read pins the database snapshot before the connection-local stamp.
                _ = await GrimoireCoreSchemaVersion.ReadAsync(connection, ct).ConfigureAwait(false);

                ChangeStamp pinned = await ReadStampAsync(sqlite, ct).ConfigureAwait(false);

                slot.Scope = before == pinned ? pinned : null;

                return await read().ConfigureAwait(false);
            }, ct).ConfigureAwait(false);

            ChangeStamp after = await ReadStampAsync(sqlite, ct).ConfigureAwait(false);

            slot.Cached = before == after ? slot.Pending ?? slot.Cached : null;

            return result;
        }
        catch
        {
            slot.Cached = null;

            throw;
        }
        finally
        {
            slot.Scope = null;

            slot.Pending = null;
        }
    }

    private sealed class EvidenceSlot
    {
        internal ChangeStamp? Scope { get; set; }

        internal EvidenceCut? Cached { get; set; }

        internal EvidenceCut? Pending { get; set; }
    }

    private sealed record ChangeStamp(long Schema, long Data, long Writes);

    private sealed record EvidenceCut(ChangeStamp Stamp, Guid Dataset, DateTimeOffset From, DateTimeOffset? Until,
        IReadOnlyDictionary<string, SagaRankingEvidence> Evidence);

    private static async Task<ChangeStamp> ReadStampAsync(SqliteConnection connection, CancellationToken ct)
    {
        await using SqliteCommand command = Command(connection,
            "SELECT (SELECT schema_version FROM pragma_schema_version), (SELECT data_version FROM pragma_data_version), total_changes();");

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2))
            : throw new InvalidDataException("The ranking evidence change stamp is unavailable.");
    }

    internal static async Task<IReadOnlyDictionary<string, SagaRankingEvidence>> ReadAsync(
        DbConnection connection,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (connection is not SqliteConnection sqlite || !Slots.TryGetValue(sqlite.Handle!, out EvidenceSlot? slot)
            || slot.Scope is not { } scope || !MemoryErasureEvidence.InTransaction(sqlite)
            || scope != await ReadStampAsync(sqlite, cancellationToken).ConfigureAwait(false)
            || !await HasTableAsync(sqlite, "covenant_state", cancellationToken).ConfigureAwait(false))
        {
            return await ReadUncachedAsync(connection, asOfUtc, cancellationToken).ConfigureAwait(false);
        }

        await using SqliteCommand state = Command(sqlite, "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1;");

        if (await state.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not byte[] bytes
            || bytes.Length != 16 || new Guid(bytes) == Guid.Empty)
        {
            return new Dictionary<string, SagaRankingEvidence>(0);
        }

        Guid dataset = new(bytes);

        if ((slot.Pending ?? slot.Cached) is { } cached && cached.Stamp == scope && cached.Dataset == dataset
            && asOfUtc >= cached.From && (cached.Until is null || asOfUtc < cached.Until))
        {
            return cached.Evidence;
        }

        IReadOnlyDictionary<string, SagaRankingEvidence> evidence = (await ReadUncachedAsync(connection, asOfUtc, cancellationToken)
            .ConfigureAwait(false)).ToFrozenDictionary(StringComparer.Ordinal);

        DateTimeOffset? until = asOfUtc;

        try
        {
            until = await NextBoundaryAsync(sqlite, evidence.Keys, asOfUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
            // The optional cache cannot strengthen the uncached reader's catalog requirements.
            // Missing/damaged optional tables disable reuse; the validated answer stays unchanged.
        }

        cancellationToken.ThrowIfCancellationRequested();

        slot.Pending = new(scope, dataset, asOfUtc, until, evidence);

        return evidence;
    }

    private static async Task<DateTimeOffset?> NextBoundaryAsync(
        SqliteConnection connection, IEnumerable<string> versions, DateTimeOffset asOf, CancellationToken ct)
    {
        // Future reports are quota-bounded. A validated source's start/recording cannot follow its
        // report completion, so absent evidence can first become eligible only at a future completion.
        // Source/use edges must follow validated coordinates, never potentially damaged SQL projections.
        string selectedVersions = JsonSerializer.Serialize(versions.ToArray(), GrimoireJsonContext.Default.StringArray);

        await using SqliteCommand command = Command(connection, """
            WITH sources AS (
                SELECT value AS VersionId FROM json_each($versions)
            ), edges(instant) AS (
                SELECT CompletedAtUtc FROM memory_evaluation_reports
                UNION ALL
                SELECT version.ValidFromUtc FROM sources source
                    JOIN annal_versions version ON version.VersionId = source.VersionId
                UNION ALL
                SELECT version.ValidToUtc FROM sources source
                    JOIN annal_versions version ON version.VersionId = source.VersionId
                UNION ALL
                SELECT version.RecordedAtUtc FROM sources source
                    JOIN annal_versions version ON version.VersionId = source.VersionId
                UNION ALL
                SELECT final.FinalizedAtUtc FROM sources source
                    JOIN saga_committed_uses used ON used.VersionId = source.VersionId
                    JOIN assistant_entry_finalizations final ON final.AssistantEntryId = used.AssistantEntryId
            )
            SELECT min(instant) FROM edges WHERE instant > $asOf;
            """, ("$versions", selectedVersions));

        UtcInstantSql.AddParameter(command, "$asOf", asOf);

        object? boundary = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);

        // Damaged optional evidence is ignored by the uncached reader. An unparseable edge must
        // disable reuse, not turn that isolated damage into a failure of ordinary Saga recall.
        return boundary is null or DBNull ? null
            : boundary is string instant && UtcInstantText.TryParse(instant, out DateTimeOffset parsed) && parsed > asOf
                ? parsed : asOf;
    }

    private static async Task<IReadOnlyDictionary<string, SagaRankingEvidence>> ReadUncachedAsync(
        DbConnection connection,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        Dictionary<string, SagaRankingEvidence> result = new(StringComparer.Ordinal);

        if (connection is not SqliteConnection sqlite
            || await GrimoireCoreSchemaVersion.ReadAsync(connection, cancellationToken).ConfigureAwait(false) < 19)
        {
            return result;
        }

        // Counterfactual execution requires a genuine dataset. An absent state cannot establish
        // that retained evidence still belongs to this installation's current authority generation.
        if (!await HasTableAsync(sqlite, "covenant_state", cancellationToken).ConfigureAwait(false))
        {
            return result;
        }

        await using SqliteCommand state = Command(sqlite, "SELECT DatasetGeneration FROM covenant_state WHERE StateKey = 1;");

        if (await state.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not byte[] bytes
            || bytes.Length != 16 || new Guid(bytes) == Guid.Empty)
        {
            return result;
        }

        Guid dataset = new(bytes);

        List<Guid> evaluations = [];

        await using (SqliteCommand roots = Command(sqlite, """
            SELECT root.EvaluationId FROM memory_evaluations root
            JOIN memory_evaluation_reports report ON report.EvaluationId = root.EvaluationId
            WHERE root.SourceCount = 1 AND report.StateCode = 3 AND report.OutcomeCode IN (1, 2, 3)
                AND report.CompletedAtUtc <= $asOf
                AND (SELECT count(*) FROM (SELECT 1 FROM memory_evaluation_sources source
                    WHERE source.EvaluationId = root.EvaluationId LIMIT 2)) = 1
            ORDER BY root.EvaluationId LIMIT $limit;
            """, ("$limit", MemoryEvaluationLimits.RetainedRegistrations)))
        {
            UtcInstantSql.AddParameter(roots, "$asOf", asOfUtc);

            await using SqliteDataReader reader = await roots.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (Guid.TryParseExact(reader.GetString(0), "N", out Guid id))
                {
                    evaluations.Add(id);
                }
            }
        }

        bool hasUses = await HasTableAsync(sqlite, "saga_committed_uses", cancellationToken).ConfigureAwait(false);

        Dictionary<string, (MemoryEvaluationSourceCoordinate Source, long Uses)> verified = new(StringComparer.Ordinal);

        foreach (Guid evaluation in evaluations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                MemoryEvaluationStoredEvidence? retained = await MemoryEvaluationEvidenceStore
                    .ReadAsync(sqlite, null, evaluation, cancellationToken).ConfigureAwait(false);

                if (retained?.Report is not { } report || retained.Sources.Length != 1
                    || retained.Registration.PolicyVersion != MemoryEvaluationLimits.PolicyVersion || retained.Registration.FormatVersion != 1
                    || !retained.Registration.Dependencies.IsEmpty || !report.Dependencies.IsEmpty
                    || retained.Registration.Ownership.DatasetGeneration != dataset
                    || report.State != MemoryEvaluationState.Completed || report.CompletedAtUtc > asOfUtc
                    || report.Decision.Outcome is not (MemoryEvaluationOutcome.Benefit or MemoryEvaluationOutcome.Harm or MemoryEvaluationOutcome.NoMeasurableEffect))
                {
                    continue;
                }

                MemoryEvaluationSourceCoordinate source = retained.Sources[0];

                if (source.Store != MemoryEvaluationSource.Saga || source.Role != MemoryEvaluationCoordinateRole.Intervention
                    || source.DependencyRelation is not null || source.Sensitivity != ContentSensitivity.None
                    || source.Lifecycle != MemoryEvaluationSourceLifecycle.Active || source.RecordedUntilUtc is not null
                    || source.ValidFromUtc > retained.Registration.SourceAsOfUtc
                    || source.RecordedAtUtc > retained.Registration.SourceAsOfUtc
                    || source.ValidToUtc <= retained.Registration.SourceAsOfUtc)
                {
                    continue;
                }

                long uses;

                if (verified.TryGetValue(source.VersionId, out var prior))
                {
                    if (prior.Source != source)
                    {
                        continue;
                    }

                    uses = prior.Uses;
                }
                else
                {
                    if (!await CurrentCleanSourceAsync(sqlite, source, asOfUtc, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    uses = hasUses ? await SuccessfulUsesAsync(sqlite, source, asOfUtc, cancellationToken).ConfigureAwait(false) : 0;

                    verified.Add(source.VersionId, (source, uses));
                }

                SagaRankingEvidence before = result.GetValueOrDefault(source.VersionId) ?? new();

                bool benefit = report.Decision.Outcome == MemoryEvaluationOutcome.Benefit;

                result[source.VersionId] = before with
                {
                    BenefitCount = before.BenefitCount + (benefit ? 1 : 0),

                    HarmCount = before.HarmCount + (report.Decision.Outcome == MemoryEvaluationOutcome.Harm ? 1 : 0),

                    NoEffectCount = before.NoEffectCount + (report.Decision.Outcome == MemoryEvaluationOutcome.NoMeasurableEffect ? 1 : 0),

                    LastBenefitAtUtc = benefit && (before.LastBenefitAtUtc is null || before.LastBenefitAtUtc < report.CompletedAtUtc)
                        ? report.CompletedAtUtc : before.LastBenefitAtUtc,

                    SuccessfulUseCount = uses,
                };
            }
            catch (Exception error) when (error is SqliteException or JsonException or ArgumentException
                or InvalidOperationException or InvalidCastException or FormatException or OverflowException)
            {
                // A damaged optional report supplies no credit. It must neither fail ordinary recall
                // nor prevent another independently valid retained report from being considered.
            }
        }

        return result;
    }

    private static async Task<bool> CurrentCleanSourceAsync(
        SqliteConnection connection,
        MemoryEvaluationSourceCoordinate source,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        bool suppression = await LongRestRecallSql.AvailableAsync(connection, cancellationToken).ConfigureAwait(false);

        await using SqliteCommand metadata = Command(connection, $"""
            SELECT version.OriginCode, version.ScopeKindCode, version.CampaignId, version.ValidFromUtc,
                version.ValidToUtc, version.RecordedAtUtc, version.SourceSessionId, memory.SessionId, version.Revision,
                length(CAST(memory.Content AS BLOB))
            FROM saga_memories memory
            JOIN annal_claims claim ON claim.SubjectStoreCode = 1 AND claim.SubjectId = memory.Id
            JOIN annal_heads head ON head.ClaimId = claim.ClaimId AND head.SubjectStoreCode = 1
            JOIN annal_versions version ON version.ClaimId = claim.ClaimId AND version.VersionId = head.CurrentVersionId
            WHERE memory.Id = $memory AND claim.ClaimId = $claim AND version.VersionId = $version
                AND version.Revision = head.CurrentRevision AND version.OperationCode = head.CurrentOperationCode
                AND version.ContentHashFormatCode = $format AND version.ContentHash = $digest
                AND version.OperationCode <> 3 AND memory.RetiredAtUtc IS NULL AND version.SensitivityCode = 0
                AND version.ScopeKindCode = memory.ScopeKindCode AND version.CampaignId COLLATE NOCASE IS memory.CampaignId
                AND {LongRestRecallSql.Eligible("memory", suppression)}
                AND NOT EXISTS (SELECT 1 FROM annal_dependencies dependency WHERE dependency.DependentVersionId = version.VersionId)
                AND NOT EXISTS (SELECT 1 FROM artifact_sensitivity label WHERE label.ArtifactKindCode = 6
                    AND lower(replace(label.ArtifactId, '-', '')) = lower(replace(memory.Id, '-', '')))
            """, ("$memory", source.SubjectId), ("$claim", source.ClaimId), ("$version", source.VersionId),
            ("$format", source.HashFormat), ("$digest", source.ContentDigest.Bytes));

        string? owner;

        await using (SqliteDataReader reader = await metadata.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            DateTimeOffset validFrom = UtcInstantText.Parse(reader.GetString(3));

            DateTimeOffset? validTo = reader.IsDBNull(4) ? null : UtcInstantText.Parse(reader.GetString(4));

            DateTimeOffset recorded = UtcInstantText.Parse(reader.GetString(5));

            owner = reader.IsDBNull(7) ? null : CanonicalSession(reader.GetString(7));

            if (reader.GetInt32(0) != (int?)source.Origin || reader.GetInt32(1) != (int)source.Scope
                || !SameCampaign(reader.IsDBNull(2) ? null : reader.GetString(2), source.CampaignId)
                || validFrom != source.ValidFromUtc || validTo != source.ValidToUtc || recorded != source.RecordedAtUtc
                || validFrom > asOfUtc || recorded > asOfUtc || validTo <= asOfUtc
                || (reader.IsDBNull(6) ? null : reader.GetString(6)) != source.SourceSessionId
                || owner != source.CanonicalOwnerSessionId || reader.GetInt32(8) != source.Revision
                || reader.GetInt64(9) > MemoryEvaluationLimits.RenderedMemoryBytes)
            {
                return false;
            }
        }

        string? campaign = source.CampaignId?.ToString("D").ToUpperInvariant();

        string? author = source.SourceSessionId is { } session ? CanonicalSession(session) : null;

        await using SqliteCommand owners = Command(connection, """
            SELECT ($owner IS NULL AND $scope = 1 OR EXISTS (
                    SELECT 1 FROM Sessions session JOIN session_campaign_bindings binding ON binding.SessionId = session.Id
                    WHERE session.Id = $owner COLLATE NOCASE AND binding.BindingKindCode = $scope
                        AND binding.CampaignId COLLATE NOCASE IS $campaign)),
                ($campaign IS NULL OR EXISTS (SELECT 1 FROM Campaigns WHERE Id = $campaign COLLATE NOCASE)),
                ($author IS NULL OR EXISTS (SELECT 1 FROM Sessions WHERE Id = $author COLLATE NOCASE)),
                NOT EXISTS (SELECT 1 FROM owner_deletion_events WHERE OwnerKindCode = 1 AND OwnerId = $campaign COLLATE NOCASE),
                NOT EXISTS (SELECT 1 FROM owner_deletion_events WHERE OwnerKindCode = 2 AND OwnerId COLLATE NOCASE IN ($owner, $author))
            """, ("$owner", owner), ("$scope", (int)source.Scope), ("$campaign", campaign), ("$author", author));

        await using (SqliteDataReader reader = await owners.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
                || Enumerable.Range(0, 5).Any(index => !reader.GetBoolean(index)))
            {
                return false;
            }
        }

        // Only after all metadata and normalized sensitivity aliases prove this source clean may its
        // bounded plaintext be hydrated. The caller retains the same read snapshot across both reads.
        await using SqliteCommand content = Command(connection,
            "SELECT Content FROM saga_memories WHERE Id = $memory AND length(CAST(Content AS BLOB)) <= $limit;",
            ("$memory", source.SubjectId), ("$limit", MemoryEvaluationLimits.RenderedMemoryBytes));

        return await content.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is string text
            && new CovenantDigest(AnnalContentDigest.ForSagaMemory(text)) == source.ContentDigest;
    }

    private static async Task<long> SuccessfulUsesAsync(SqliteConnection connection, MemoryEvaluationSourceCoordinate source,
        DateTimeOffset asOfUtc, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(connection, """
            SELECT count(*) FROM (
                SELECT 1 FROM saga_committed_uses used
                JOIN assistant_entry_finalizations final ON final.AssistantEntryId = used.AssistantEntryId
                JOIN Entries entry ON entry.Id = final.AssistantEntryId AND entry.SessionId = final.SessionId AND entry.Role = 2
                WHERE used.MemoryId = $memory AND used.ClaimId = $claim AND used.VersionId = $version
                    AND used.HashFormat = $format AND used.ContentDigest = $digest
                    AND final.OutcomeCode = 1 AND final.SourceEvidenceDigest IS NULL AND final.FinalizedAtUtc <= $asOf
                    AND final.FinalizedAtUtc >= $validFrom AND final.FinalizedAtUtc >= $recorded
                    AND ($validTo IS NULL OR final.FinalizedAtUtc < $validTo)
                    AND ($scope = 1 OR EXISTS (SELECT 1 FROM session_campaign_bindings binding
                        WHERE binding.SessionId = final.SessionId AND binding.BindingKindCode = 2 AND binding.CampaignId COLLATE NOCASE = $campaign))
                    AND NOT EXISTS (SELECT 1 FROM assistant_entry_erasure_receipts erased WHERE erased.AssistantEntryId = final.AssistantEntryId)
                    AND NOT EXISTS (SELECT 1 FROM session_turn_claims claim WHERE claim.AssistantEntryId = final.AssistantEntryId AND claim.StateCode <> 3)
                LIMIT $limit);
            """, ("$memory", source.SubjectId), ("$claim", source.ClaimId), ("$version", source.VersionId),
            ("$format", source.HashFormat), ("$digest", source.ContentDigest.Bytes), ("$limit", MemoryEvaluationLimits.RetainedRegistrations),
            ("$scope", (int)source.Scope), ("$campaign", source.CampaignId?.ToString("D").ToUpperInvariant()));

        UtcInstantSql.AddParameter(command, "$asOf", asOfUtc);

        UtcInstantSql.AddParameter(command, "$validFrom", source.ValidFromUtc!.Value);

        UtcInstantSql.AddParameter(command, "$recorded", source.RecordedAtUtc!.Value);

        if (source.ValidToUtc is { } validTo)
        {
            UtcInstantSql.AddParameter(command, "$validTo", validTo);
        }
        else
        {
            command.Parameters.AddWithValue("$validTo", DBNull.Value);
        }

        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    private static string CanonicalSession(string value) => Guid.Parse(value).ToString("D").ToUpperInvariant();

    private static bool SameCampaign(string? stored, Guid? expected) => stored is null
        ? expected is null : Guid.TryParse(stored, out Guid value) && value == expected;

    private static async Task<bool> HasTableAsync(SqliteConnection connection, string name, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = Command(connection,
            "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name);", ("$name", name));

        return (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        SqliteCommand command = connection.CreateCommand();

        command.CommandText = sql;

        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
