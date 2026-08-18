using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Infrastructure.TheForge;

namespace RetroDownfall.Arcanum.Infrastructure.Repositories;

/// <summary>
/// Resolves a supplied directory to the most specific registered Campaign root, by identity.
/// </summary>
/// <remarks>
/// One indexed <c>IN</c> query over the bounded ancestor identity list, against the unique
/// <c>PhysicalIdentityDigest</c> index. There is no path-prefix predicate anywhere in this file, and
/// that absence is the security property: a <c>LIKE 'root%'</c> scan is what makes <c>/work/app</c> and
/// <c>/work/app-legacy</c> confusable, and what lets a symlink or a rename inherit someone else's
/// registration (§10.12).
///
/// <para>The candidate list is bounded well below SQLite's parameter limit by
/// <see cref="CampaignPathIdentityPolicy.MaxAncestorCandidates"/>, so the query is one statement with no
/// batching. Most specific wins by the smallest ancestor depth, which the opener already computed;
/// resolving by depth rather than by path length means a nested registration is chosen even when its
/// display path is shorter than its parent's.</para>
///
/// <para>A row whose <c>PolicyVersion</c> differs from the current derivation policy is skipped rather
/// than matched. Digests derived under different rules are not comparable, and treating them as equal
/// would report every registered root as moved the first time the policy advances.</para>
/// </remarks>
internal sealed class CampaignPathIdentityReader(
    ICovenantConnectionSource connections,
    PhysicalCampaignRootOpener opener) : ICampaignPathIdentityReader
{

    public async ValueTask<Result<RegisteredCampaignIdentity?>> ResolveMostSpecificAsync(
        string? workingDirectory,
        CancellationToken cancellationToken)
    {

        IReadOnlyList<CampaignRootCandidate> candidates = opener.EnumerateAncestorIdentities(workingDirectory);

        if (candidates.Count == 0)
        {
            return Result<RegisteredCampaignIdentity?>.Success(null);
        }

        Dictionary<string, int> depthByDigest = new(candidates.Count, StringComparer.Ordinal);

        foreach (CampaignRootCandidate candidate in candidates)
        {

            string hex = Convert.ToHexString(candidate.PhysicalIdentityDigest.Bytes);

            // The deepest occurrence wins if a cycle-free walk somehow repeats an identity.
            if (!depthByDigest.TryGetValue(hex, out int existing) || candidate.Depth < existing)
            {
                depthByDigest[hex] = candidate.Depth;
            }

        }

        SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        StringBuilder sql = new(
            """
            SELECT CampaignId,
                   PolicyVersion,
                   Revision,
                   Depth,
                   PhysicalIdentityDigest
            FROM campaign_path_identities
            WHERE PolicyVersion = $policyVersion
              AND PhysicalIdentityDigest IN (
            """);

        int index = 0;

        foreach (CampaignRootCandidate candidate in candidates)
        {

            if (index > 0)
            {
                _ = sql.Append(", ");
            }

            string parameter = "$identity" + index.ToString(CultureInfo.InvariantCulture);

            _ = sql.Append(parameter);

            _ = command.Parameters.AddWithValue(parameter, candidate.PhysicalIdentityDigest.Bytes);

            index++;

        }

        _ = sql.Append(");");

        command.CommandText = sql.ToString();

        _ = command.Parameters.AddWithValue("$policyVersion", (long)CampaignPathIdentityPolicy.Version);

        RegisteredCampaignIdentity? best = null;

        int bestDepth = int.MaxValue;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {

            if (reader.GetValue(4) is not byte[] digestBytes || digestBytes.Length != 32)
            {
                return Result<RegisteredCampaignIdentity?>.Failure(
                    new Error(
                        ErrorCodes.Covenant.IntegrityFailure,
                        "A registered Campaign root identity is malformed."));
            }

            if (!depthByDigest.TryGetValue(Convert.ToHexString(digestBytes), out int depth) || depth >= bestDepth)
            {
                continue;
            }

            bestDepth = depth;

            best = new RegisteredCampaignIdentity(
                Guid.Parse(reader.GetString(0)),
                checked((uint)reader.GetInt64(1)),
                reader.GetInt64(2),
                reader.GetInt32(3),
                new CovenantDigest(digestBytes));

        }

        return Result<RegisteredCampaignIdentity?>.Success(best);

    }

    /// <summary>
    /// Reads the registration a named Campaign owns, if it still has one.
    /// </summary>
    /// <remarks>
    /// <c>campaign_path_identities.CampaignId</c> is <c>REFERENCES "Campaigns"("Id")</c>, so every row
    /// that can exist holds the exact text the EF-owned parent column holds, which the provider writes
    /// as an uppercase <c>D</c>-format literal. Neither column is <c>COLLATE NOCASE</c>, so the
    /// identity is bound as a <see cref="Guid"/> and not as <c>ToString()</c>: a lowercase literal
    /// matches nothing for any identity carrying a hex letter, and the miss reads as "not registered"
    /// rather than as a failure.
    /// </remarks>
    public async ValueTask<Result<RegisteredCampaignIdentity?>> FindByCampaignAsync(
        Guid campaignId,
        CancellationToken cancellationToken)
    {

        if (campaignId == Guid.Empty)
        {
            return Result<RegisteredCampaignIdentity?>.Success(null);
        }

        SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT CampaignId,
                   PolicyVersion,
                   Revision,
                   Depth,
                   PhysicalIdentityDigest
            FROM campaign_path_identities
            WHERE CampaignId = $campaignId;
            """;

        _ = command.Parameters.AddWithValue("$campaignId", campaignId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return Result<RegisteredCampaignIdentity?>.Success(null);
        }

        if (reader.GetValue(4) is not byte[] digest || digest.Length != 32)
        {
            return Result<RegisteredCampaignIdentity?>.Failure(
                new Error(
                    ErrorCodes.Covenant.IntegrityFailure,
                    "A registered Campaign root identity is malformed."));
        }

        return Result<RegisteredCampaignIdentity?>.Success(
            new RegisteredCampaignIdentity(
                Guid.Parse(reader.GetString(0)),
                checked((uint)reader.GetInt64(1)),
                reader.GetInt64(2),
                reader.GetInt32(3),
                new CovenantDigest(digest)));

    }

}

/// <summary>
/// Reports whether a Campaign still exists, and the registry generation that observation belongs to.
/// </summary>
/// <remarks>
/// Existence and generation are read in the same statement so a turn cannot capture a generation for a
/// Campaign that was already gone. The generation is the always-present core
/// <c>campaign_registry_state.RegistryEpoch</c> rather than a Covenant-owned counter, so ordinary
/// Campaign resolution keeps working with the optional Covenant tiers absent or damaged.
///
/// <para>The existence probe reaches into the EF-owned <c>"Campaigns"</c> table, whose <c>"Id"</c> the
/// provider writes as an uppercase <c>D</c>-format literal and which is not <c>COLLATE NOCASE</c>. The
/// identity is therefore bound as a <see cref="Guid"/>, exactly as
/// <c>CovenantStoreSql.DependentHeadScan</c>'s scoped predicate binds it, so the primary key index
/// still serves the probe and the comparison stays in one representation.</para>
/// </remarks>
internal sealed class CampaignAvailabilityReader(ICovenantConnectionSource connections)
    : ICampaignAvailabilityReader
{

    public async ValueTask<Result<long?>> FindAvailabilityGenerationAsync(
        Guid campaignId,
        CancellationToken cancellationToken)
    {

        if (campaignId == Guid.Empty)
        {
            return Result<long?>.Success(null);
        }

        SqliteConnection connection = await connections.GetOpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            SELECT s.RegistryEpoch
            FROM campaign_registry_state AS s
            WHERE s.StateKey = 1
              AND EXISTS (SELECT 1 FROM "Campaigns" AS c WHERE c."Id" = $campaignId);
            """;

        _ = command.Parameters.AddWithValue("$campaignId", campaignId);

        object? epoch = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return epoch is null or DBNull
            ? Result<long?>.Success(null)
            : Result<long?>.Success(Convert.ToInt64(epoch, CultureInfo.InvariantCulture));

    }

}
