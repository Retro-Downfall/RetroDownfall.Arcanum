using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Security;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildDeclaredRootScan(GuildAgentSourceKind Kind, Guid? WorkspaceId, string Directory, Guid ScanId);

internal sealed partial class GuildStore
{
    internal Task<Result<Guid>> BeginDeclaredRootScanAsync(Guid generation, GuildAgentSourceKind kind,
        Guid? workspaceId, string directory, CancellationToken token) => ExecuteAsync<Guid>(true, async (connection, transaction, ct) =>
        {
            if (generation == Guid.Empty || kind is not (GuildAgentSourceKind.DeclaredGlobal or GuildAgentSourceKind.DeclaredWorkspace)
                || (kind == GuildAgentSourceKind.DeclaredWorkspace) != workspaceId.HasValue)
            {
                return Invalid();
            }

            Result owner = await RequireOwnerWithinAsync(connection, transaction, null, workspaceId, ct).ConfigureAwait(false);

            if (owner.IsFailure)
            {
                return owner.Error;
            }

            Guid scanId = Guid.NewGuid();

            await using SqliteCommand command = Command(connection, transaction, """
                INSERT INTO guild_declared_roots(RootKey,SourceKindCode,WorkspaceId,InstallationGeneration,PathDigest,ScanId,StateCode)
                VALUES($root,$kind,$workspace,$generation,$path,$scan,0)
                ON CONFLICT(RootKey) DO UPDATE SET InstallationGeneration=excluded.InstallationGeneration,
                    PathDigest=excluded.PathDigest,ScanId=excluded.ScanId,StateCode=0;
                """);

            Add(command, "$root", RootKey(kind, workspaceId));

            Add(command, "$kind", (int)kind);

            Add(command, "$workspace", Format(workspaceId));

            Add(command, "$generation", Format(generation));

            Add(command, "$path", RootDigest(directory));

            Add(command, "$scan", Format(scanId));

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            return scanId;
        }, token);

    internal async Task<Result> ValidateDeclaredRootScansAsync(Guid generation,
        ImmutableArray<GuildDeclaredRootScan> scans, CancellationToken token)
    {
        Result<bool> checkedScans = await ExecuteAsync<bool>(false, async (connection, transaction, ct) =>
        {
            foreach (GuildDeclaredRootScan scan in scans)
            {
                Result current = await RequireRootScanWithinAsync(connection, transaction, generation, scan, ct).ConfigureAwait(false);

                if (current.IsFailure)
                {
                    return current.Error;
                }
            }

            return true;
        }, token).ConfigureAwait(false);

        return checkedScans.IsFailure ? Result.Failure(checkedScans.Error) : Result.Success();
    }

    internal Task<Result> CompleteDeclaredRootScansAsync(Guid generation,
        ImmutableArray<GuildDeclaredRootScan> scans, CancellationToken token) => SetRootScansAsync(generation, scans, true, token);

    internal Task<Result> InvalidateDeclaredRootScansAsync(Guid generation,
        ImmutableArray<GuildDeclaredRootScan> scans, CancellationToken token) => SetRootScansAsync(generation, scans, false, token);

    private async Task<Result> SetRootScansAsync(Guid generation, ImmutableArray<GuildDeclaredRootScan> scans,
        bool completed, CancellationToken token)
    {
        Result<bool> changed = await ExecuteAsync<bool>(true, async (connection, transaction, ct) =>
        {
            foreach (GuildDeclaredRootScan scan in scans)
            {
                Result current = await RequireRootScanWithinAsync(connection, transaction, generation, scan, ct, requirePath: completed).ConfigureAwait(false);

                if (current.IsFailure)
                {
                    return current.Error;
                }
            }

            foreach (GuildDeclaredRootScan scan in scans)
            {
                await using SqliteCommand command = Command(connection, transaction, completed ? """
                    DELETE FROM guild_declared_source_claims WHERE RootKey=$root AND LastSeenScanId<>$scan;
                    UPDATE guild_declared_roots SET StateCode=1 WHERE RootKey=$root;
                    """ : "UPDATE guild_declared_roots SET StateCode=2 WHERE RootKey=$root;");

                Add(command, "$root", RootKey(scan.Kind, scan.WorkspaceId));

                if (completed)
                {
                    Add(command, "$scan", Format(scan.ScanId));
                }

                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            if (completed)
            {
                foreach (GuildDeclaredRootScan scan in scans)
                {
                    await using SqliteCommand restore = Command(connection, transaction, """
                        UPDATE guild_agent_cards SET Available=1,ReasonCode=NULL
                        WHERE ReasonCode='Guild.SourceConflict' AND EXISTS(
                            SELECT 1 FROM guild_declared_source_claims c
                            JOIN guild_agent_card_revisions rev ON rev.CardId=guild_agent_cards.CardId AND rev.Revision=guild_agent_cards.CurrentRevision
                            WHERE c.SourceIdentity=guild_agent_cards.SourceIdentity AND c.RootKey=$root AND c.LastSeenScanId=$scan
                              AND c.ObservedValid=1 AND c.SourceDigest=rev.SourceDigest
                              AND (c.ExplicitAgentId IS NULL OR NOT EXISTS(SELECT 1 FROM guild_declared_source_claims other
                                WHERE other.ExplicitAgentId=c.ExplicitAgentId AND other.SourceIdentity<>c.SourceIdentity)));
                        """);

                    Add(restore, "$root", RootKey(scan.Kind, scan.WorkspaceId));

                    Add(restore, "$scan", Format(scan.ScanId));

                    await restore.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            return true;
        }, token).ConfigureAwait(false);

        return changed.IsFailure ? Result.Failure(changed.Error) : Result.Success();
    }

    internal Task<Result<bool>> ObserveDeclaredSourceAsync(Guid generation, GuildDeclaredRootScan scan,
        GuildDeclaredAgentObservation observation, CancellationToken token) => ExecuteAsync<bool>(true,
        async (connection, transaction, ct) =>
        {
            long metadataBytes = Encoding.UTF8.GetByteCount(observation.SourceIdentity)
                + Encoding.UTF8.GetByteCount(RootKey(scan.Kind, scan.WorkspaceId)) + 36
                + (observation.Agent?.ExplicitId is null ? 0 : 36) + (observation.Agent is null ? 0 : 64);

            if (metadataBytes > MaximumContentBytes)
            {
                return TooLarge();
            }

            if (observation.Agent is not null && (observation.Digest is null || observation.Digest.Length != 64))
            {
                return Invalid();
            }

            Result current = await RequireRootScanWithinAsync(connection, transaction, generation, scan, ct).ConfigureAwait(false);

            if (current.IsFailure)
            {
                return current.Error;
            }

            string scanId;

            await using (SqliteCommand root = Command(connection, transaction, "SELECT ScanId FROM guild_declared_roots WHERE RootKey=$root"))
            {
                Add(root, "$root", RootKey(scan.Kind, scan.WorkspaceId));

                scanId = (string)(await root.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
            }

            await using (SqliteCommand command = Command(connection, transaction, """
                INSERT INTO guild_declared_source_claims(SourceIdentity,RootKey,ExplicitAgentId,LastSeenScanId,ObservedValid,SourceDigest)
                VALUES($source,$root,$alias,$scan,$known,$digest)
                ON CONFLICT(SourceIdentity) DO UPDATE SET RootKey=excluded.RootKey,
                    ExplicitAgentId=CASE WHEN $known=1 THEN excluded.ExplicitAgentId ELSE guild_declared_source_claims.ExplicitAgentId END,
                    LastSeenScanId=excluded.LastSeenScanId,ObservedValid=excluded.ObservedValid,SourceDigest=excluded.SourceDigest;
                """))
            {
                Add(command, "$source", observation.SourceIdentity);

                Add(command, "$root", RootKey(scan.Kind, scan.WorkspaceId));

                Add(command, "$alias", Format(observation.Agent?.ExplicitId));

                Add(command, "$known", observation.Agent is null ? 0 : 1);

                Add(command, "$scan", scanId);

                Add(command, "$digest", observation.Agent is null ? null : observation.Digest);

                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using SqliteCommand conflicts = Command(connection, transaction, """
                SELECT EXISTS(SELECT 1 FROM guild_declared_source_claims c
                    JOIN guild_declared_source_claims other ON other.ExplicitAgentId=c.ExplicitAgentId
                    WHERE c.SourceIdentity=$source AND c.ExplicitAgentId IS NOT NULL AND other.SourceIdentity<>c.SourceIdentity);
                """);

            Add(conflicts, "$source", observation.SourceIdentity);

            return Equals(1L, await conflicts.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }, token);

    internal Task<Result<ImmutableHashSet<string>>> ReadDeclaredEligibilityAsync(Guid generation, GuildCardReadScope scope,
        string globalDirectory, ImmutableArray<string> sources, CancellationToken token) => ExecuteAsync<ImmutableHashSet<string>>(false,
        async (connection, transaction, ct) =>
        {
            if (sources.Length == 0)
            {
                return ImmutableHashSet<string>.Empty;
            }

            if (sources.Length > 100)
            {
                return Invalid();
            }

            string parameters = string.Join(',', Enumerable.Range(0, sources.Length).Select(index => "$source" + index));

            await using SqliteCommand command = Command(connection, transaction, $"""
                SELECT c.SourceIdentity FROM guild_declared_source_claims c
                JOIN guild_declared_roots r ON r.RootKey=c.RootKey
                WHERE c.SourceIdentity IN({parameters}) AND r.InstallationGeneration=$generation AND r.StateCode=1
                  AND c.LastSeenScanId=r.ScanId AND c.ObservedValid=1
                  AND ((r.SourceKindCode=0 AND r.PathDigest=$globalPath)
                    OR (r.SourceKindCode=1 AND r.WorkspaceId=$workspace AND r.PathDigest=$workspacePath))
                  AND (c.ExplicitAgentId IS NULL OR NOT EXISTS(SELECT 1 FROM guild_declared_source_claims other
                    WHERE other.ExplicitAgentId=c.ExplicitAgentId AND other.SourceIdentity<>c.SourceIdentity));
                """);

            for (int index = 0; index < sources.Length; index++)
            {
                Add(command, "$source" + index, sources[index]);
            }

            Add(command, "$generation", Format(generation));

            Add(command, "$globalPath", RootDigest(globalDirectory));

            Add(command, "$workspace", Format(scope.WorkspaceId));

            Add(command, "$workspacePath", scope.WorkspaceRoot is null ? null : RootDigest(Path.Combine(scope.WorkspaceRoot, ".arcanum", "agents")));

            ImmutableHashSet<string>.Builder eligible = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                eligible.Add(reader.GetString(0));
            }

            return eligible.ToImmutable();
        }, token);

    internal async Task<Result> RebindDeclaredSourceAsync(Guid alias, string oldSource,
        GuildDeclaredAgentObservation replacement, long expectedRevision, string? workspaceRoot, CancellationToken token)
    {
        Result<bool> rebound = await ExecuteAsync<bool>(true, async (connection, transaction, ct) =>
        {
            Result workspace = await RequireWorkspaceWithinAsync(connection, transaction,
                replacement.WorkspaceId, workspaceRoot, ct).ConfigureAwait(false);

            if (workspace.IsFailure)
            {
                return workspace.Error;
            }

            await using SqliteCommand command = Command(connection, transaction, """
                UPDATE guild_agent_cards SET SourceIdentity=$new,SourceKindCode=$kind,WorkspaceId=$workspace,
                    Available=0,ReasonCode='Guild.Unavailable',UpdatedAtUtc=$now
                WHERE ExplicitAgentId=$alias AND SourceIdentity=$old AND CurrentRevision=$revision
                    AND SourceKindCode=$kind AND WorkspaceId IS $workspace;
                """);

            Add(command, "$new", replacement.SourceIdentity);

            Add(command, "$kind", (int)replacement.Kind);

            Add(command, "$workspace", Format(replacement.WorkspaceId));

            Add(command, "$now", UtcInstantText.Format(time.GetUtcNow()));

            Add(command, "$alias", Format(alias));

            Add(command, "$old", oldSource);

            Add(command, "$revision", expectedRevision);

            if (await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
            {
                return Conflict();
            }

            command.Parameters.Clear();

            command.CommandText = "DELETE FROM guild_declared_source_claims WHERE SourceIdentity=$old";

            Add(command, "$old", oldSource);

            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            return true;
        }, token).ConfigureAwait(false);

        return rebound.IsFailure ? Result.Failure(rebound.Error) : Result.Success();
    }

    private static async Task<Result> RequireDeclaredSourceClaimsWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildCardWrite write, CancellationToken ct)
    {
        if (write.Payload.Card.Source.Kind is not (GuildAgentSourceKind.DeclaredGlobal or GuildAgentSourceKind.DeclaredWorkspace)
            || write.ExplicitAgentId is null)
        {
            return Result.Success();
        }

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM guild_declared_source_claims
                WHERE ExplicitAgentId=$alias AND SourceIdentity<>$source);
            """);

        Add(command, "$alias", Format(write.ExplicitAgentId));

        Add(command, "$source", write.SourceIdentity);

        return write.Payload.Card.Source.Available && Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false))
            ? Result.Failure(new Error(ErrorCodes.Guild.SourceConflict, "The explicit agent identity has conflicting declared sources."))
            : Result.Success();
    }

    private static async Task<Result> RequireRootScanWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid generation, GuildDeclaredRootScan scan, CancellationToken ct, bool requirePath = true)
    {
        await using SqliteCommand command = Command(connection, transaction, """
            SELECT EXISTS(SELECT 1 FROM guild_declared_roots WHERE RootKey=$root AND InstallationGeneration=$generation
                AND ($verifyPath=0 OR PathDigest=$path) AND ($scan IS NULL OR ScanId=$scan));
            """);

        Add(command, "$root", RootKey(scan.Kind, scan.WorkspaceId));

        Add(command, "$generation", Format(generation));

        Add(command, "$path", RootDigest(scan.Directory));

        Add(command, "$verifyPath", requirePath ? 1 : 0);

        Add(command, "$scan", scan.ScanId == Guid.Empty ? null : Format(scan.ScanId));

        return Equals(1L, await command.ExecuteScalarAsync(ct).ConfigureAwait(false))
            ? Result.Success() : Result.Failure(new Error(ErrorCodes.Guild.CursorGone, "The declared-source discovery was superseded or is unavailable."));
    }

    private static string RootKey(GuildAgentSourceKind kind, Guid? workspace) =>
        kind == GuildAgentSourceKind.DeclaredGlobal ? "global" : "workspace:" + Format(workspace);

    private static string RootDigest(string directory)
    {
        string path = Path.GetFullPath(directory);

        if (WorkspacePathPolicy.TryCanonicalize(path, out string? canonical, out _))
        {
            path = canonical;
        }

        path = Path.TrimEndingDirectorySeparator(path);

        if (OperatingSystem.IsWindows())
        {
            path = path.ToUpperInvariant();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
    }
}
