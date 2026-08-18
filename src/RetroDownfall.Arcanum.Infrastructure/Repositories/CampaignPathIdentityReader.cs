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

        BindCoreCampaignIdentity(command, "$campaignId", campaignId);

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

    /// <summary>
    /// Binds a Campaign identity that will be compared against text the EF-owned <c>"Campaigns"."Id"</c>
    /// column governs.
    /// </summary>
    /// <remarks>
    /// The value is bound as a <see cref="Guid"/> rather than as a formatted string, so the provider
    /// produces exactly the uppercase <c>D</c>-format text EF stored. A lowercase literal matched no row
    /// for any identity carrying a hex letter, which is every realistic Campaign.
    ///
    /// <para><c>campaign_path_identities.CampaignId</c> counts as that same crossing even though the table
    /// is Covenant-owned: it is declared <c>REFERENCES "Campaigns"("Id")</c>, and
    /// <c>CovenantSqliteConnectionInitializer</c> both sets and verifies <c>PRAGMA foreign_keys=ON</c>, so
    /// no row can hold a representation that differs from the EF-written parent. Covenant tables that key
    /// themselves — <c>covenant_heads.CampaignId</c>, written lowercase by the mutation kernel — are a
    /// different crossing and keep their own representation.</para>
    /// </remarks>
    private static void BindCoreCampaignIdentity(SqliteCommand command, string name, Guid value) =>
        _ = command.Parameters.AddWithValue(name, value);

}

/// <summary>
/// Reports whether a Campaign still exists, and the registry generation that observation belongs to.
/// </summary>
/// <remarks>
/// Existence and generation are read in the same statement so a turn cannot capture a generation for a
/// Campaign that was already gone. The generation is the always-present core
/// <c>campaign_registry_state.RegistryEpoch</c> rather than a Covenant-owned counter, so ordinary
/// Campaign resolution keeps working with the optional Covenant tiers absent or damaged.
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

        // Bound as a Guid, not as a formatted string: EF stores the uppercase D-format text the provider
        // produces for a Guid, and neither side is COLLATE NOCASE. A lowercase literal made the EXISTS
        // match nothing for any identity carrying a hex letter, so every live Campaign resolved as deleted.
        _ = command.Parameters.AddWithValue("$campaignId", campaignId);

        object? epoch = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return epoch is null or DBNull
            ? Result<long?>.Success(null)
            : Result<long?>.Success(Convert.ToInt64(epoch, CultureInfo.InvariantCulture));

    }

}
