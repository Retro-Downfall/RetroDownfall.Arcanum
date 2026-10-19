using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.Intelligence;

namespace RetroDownfall.Arcanum.Infrastructure.Data.MemoryEvaluation;

internal sealed partial class MemoryEvaluationSourceReader
{
    private sealed record PreparedCovenant(CovenantDigest MetadataDigest,
        List<MemoryEvaluationSourceCoordinate> Coordinates, List<MemoryEvaluationSourceFragment> Fragments);

    private async Task<PreparedCovenant?> PrepareCovenantAsync(MemoryEvaluationValidatedRequest request, CanonicalCampaignContext campaign,
        CovenantTurnLease lease, CancellationToken ct)
    {
        if (request.Request.Memory.Covenant.IsEmpty)
        {
            return null;
        }

        Require(covenantStore is not null);

        SqliteConnection connection = await connections.GetOpenConnectionAsync(ct).ConfigureAwait(false);

        CovenantDigest before;

        await using (SqliteTransaction transaction = connection.BeginTransaction(deferred: true))
        {
            before = await CovenantMetadataAsync(connection, transaction, campaign.CampaignId, lease, ct).ConfigureAwait(false);
        }

        var read = await covenantStore!.ReadTurnSnapshotAsync(campaign, lease, ct).ConfigureAwait(false);

        Require(read.IsSuccess && read.Value.DatasetGeneration.Value == lease.Snapshot.DatasetGeneration);

        var linked = new CovenantLinker().Link(read.Value);

        Require(linked.IsSuccess);

        List<MemoryEvaluationSourceCoordinate> coordinates = [];

        List<MemoryEvaluationSourceFragment> fragments = [];

        // Link every lawful candidate before selecting. An omitted shadow or mask still decides eligibility.
        foreach (CovenantPlanCandidateDecision decision in linked.Value.EligibleDecisions)
        {
            CovenantSnapshotCandidate candidate = decision.Candidate;

            MemoryEvaluationCovenantTarget? target = request.Request.Memory.Covenant.FirstOrDefault(target => target.EntryId == candidate.EntryId && target.VersionId == candidate.VersionId);

            if (target is null)
            {
                continue;
            }

            Require(target.FragmentDigest == candidate.FragmentDigest);

            fragments.Add(new(coordinates.Count, SystemPromptBuilder.RenderEvaluationCovenant(decision.Placement,
                Utf8.GetString(decision.RenderSelectedSection())), MemoryEvaluationPromptPart.System));

            coordinates.Add(new(MemoryEvaluationCoordinateRole.Intervention, MemoryEvaluationSource.Covenant,
                candidate.EntryId.ToString("D"), null, candidate.VersionId.ToString("D"), (int)candidate.RendererPolicy,
                candidate.FragmentDigest, null, null, (MemoryEvaluationScopeKind)candidate.Scope, candidate.CampaignId,
                null, null, null, null, MemoryEvaluationSourceLifecycle.Active, ContentSensitivity.CovenantDerived,
                null, null, lease.Snapshot.DatasetGeneration, checked((int)candidate.Revision), candidate.Lane, decision.Placement, candidate.Origin));
        }

        Require(coordinates.Count == request.Request.Memory.Covenant.Length);

        return new(before, coordinates, fragments);
    }

    internal static string CovenantMetadataProjection(string sql)
    {
        string[] scopes = sql.Split("v.CompiledContent AS CompiledContent", StringSplitOptions.None);

        // The owning query has exactly two scope branches. Any changed or additional content
        // reference requires an explicit projection review before metadata validation may run it.
        if (scopes.Length != 3 || scopes.Any(scope => scope.Contains("CompiledContent", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("The Covenant snapshot content projection is unrecognized.");
        }

        return string.Join("length(CAST(v.CompiledContent AS BLOB)) AS CompiledContent", scopes);
    }

    private static async Task<CovenantDigest> CovenantMetadataAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid? campaign, CovenantTurnLease lease, CancellationToken ct)
    {
        using CovenantCanonicalHashWriter writer = new();

        writer.WriteUtf8("arcanum.memory-evaluation.covenant-state.v1");

        // The owning port's same bounded scoped projection, with only the content column replaced by its byte length.
        string heads = CovenantMetadataProjection(CovenantStoreSql.TurnSnapshot())
            .Replace("LIMIT ", "ORDER BY candidates.SearchRowId LIMIT ", StringComparison.Ordinal);

        await using (SqliteCommand command = Command(connection, transaction, heads, ("$campaign", campaign?.ToString("D"))))
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            int count = 0;

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                Require(++count <= CovenantLimits.MaxActiveSnapshotRows && new Guid((byte[])reader[0]) == lease.Snapshot.DatasetGeneration);

                HashRow(writer, reader);
            }

            Require(count > 0);

            writer.WriteInt32(count);
        }

        await using (SqliteCommand command = Command(connection, transaction, $"""
            SELECT h.ScopeCode, h.CampaignId, h.NormalizedKey, h.LaneCode, h.KeyEpoch, h.IsPinned, h.IsMasked,
                h.CurrentVersionId, h.CurrentRevision, h.UpdatedAtUtc, {CovenantStoreSql.BindingEpoch("h.NormalizedKey")}
            FROM covenant_curation_heads h WHERE h.CampaignId IS NULL OR h.CampaignId = $campaign
            ORDER BY h.ScopeCode, h.NormalizedKey, h.LaneCode, h.KeyEpoch LIMIT 161
            """, ("$campaign", campaign?.ToString("D"))))
        {
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            int count = 0;

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                Require(++count <= CovenantLimits.MaxActiveSnapshotRows);

                HashRow(writer, reader);
            }

            writer.WriteInt32(count);
        }

        return writer.FinalizeDigest();
    }
}
