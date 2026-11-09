using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    private sealed record TreeCheckpoint(Guid Generation, Guid Root, string OwnerKey, long Snapshot, long After, Guid Last);

    private static string TreeScope(Guid subtree, int size, QuestState? state) => "tree:" + Id(subtree) + ":"
        + (state is null ? "*" : ((int)state.Value).ToString(CultureInfo.InvariantCulture)) + ":" + size.ToString(CultureInfo.InvariantCulture) + ":";

    private static string TreeCallerKey(GuildCallerContext caller)
    {
        GuildExecutionHandle? run = caller.Execution;

        string binding = ((int)caller.Kind).ToString(CultureInfo.InvariantCulture) + ":" + Id(caller.PrincipalId)
            + (run is null ? "" : ":" + Id(run.SignetId) + ":" + Id(run.AgentId) + ":" + Id(run.RunId) + ":" + Id(run.QuestId)
                + ":" + run.CredentialGeneration.ToString(CultureInfo.InvariantCulture) + ":" + run.RevocationGeneration.ToString(CultureInfo.InvariantCulture));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding)));
    }

    private static string TreeOwnerKey(GuildQuestDto quest) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Id(quest.Id) + ":" + Id(quest.SessionId) + ":" + Id(quest.CampaignId) + ":" + Id(quest.WorkspaceId))));

    private async Task<Result<TreeCheckpoint>> ReadTreeCursorWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, string callerKey, string scope, string cursor, Guid currentEpoch, CancellationToken ct)
    {
        // A random registry identity is the capability. Generation/epoch/root text alone never
        // authenticates a cursor; compare it with the immutable retained row before trusting it.
        if (cursor.Length != 131) return TreeCursorInvalid;

        string[] parts = cursor.Split('.');

        if (parts.Length != 4) return TreeCursorInvalid;

        Guid[] ids = new Guid[4];

        for (int index = 0; index < ids.Length; index++)
        {
            if (!Guid.TryParseExact(parts[index], "N", out ids[index]) || ids[index] == Guid.Empty
                || parts[index] != ids[index].ToString("N")) return TreeCursorInvalid;
        }

        await using SqliteCommand command = Command(connection, transaction, """
            SELECT CASE WHEN typeof(InstallationGeneration)='text' AND length(CAST(InstallationGeneration AS BLOB))=36
                AND typeof(AuthorityEpoch)='text' AND length(CAST(AuthorityEpoch AS BLOB))=36
                AND typeof(RootQuestId)='text' AND length(CAST(RootQuestId AS BLOB))=36
                AND typeof(CallerKey)='text' AND length(CAST(CallerKey AS BLOB))=64
                AND typeof(ScopeKey)='text' AND length(CAST(ScopeKey AS BLOB))<=512
                AND typeof(VisibilityKey)='text' AND length(CAST(VisibilityKey AS BLOB))=64
                AND typeof(AfterSequence)='integer' AND AfterSequence>0
                AND typeof(SnapshotSequence)='integer' AND SnapshotSequence>=AfterSequence
                THEN 1 ELSE 0 END,
                InstallationGeneration,AuthorityEpoch,RootQuestId,CallerKey,ScopeKey,VisibilityKey,AfterSequence,SnapshotSequence
            FROM guild_portal_cursors WHERE Id=$id
            """);

        Add(command, "$id", Id(ids[3]));

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return TreeCursorInvalid;

        if (reader.GetInt64(0) != 1) return Unavailable;

        if (reader.GetString(1) != Id(ids[0]) || reader.GetString(2) != Id(ids[1]) || reader.GetString(3) != Id(ids[2])
            || reader.GetString(4) != callerKey) return TreeCursorInvalid;

        string savedScope = reader.GetString(5);

        if (!savedScope.StartsWith(scope, StringComparison.Ordinal)
            || !Guid.TryParseExact(savedScope.AsSpan(scope.Length), "D", out Guid last) || last == Guid.Empty
            || savedScope != scope + Id(last)) return TreeCursorInvalid;

        if (ids[0] != policy.InstallationGeneration || ids[1] != currentEpoch) return TreeCursorGone;

        return new TreeCheckpoint(ids[0], ids[2], reader.GetString(6), reader.GetInt64(8), reader.GetInt64(7), last);
    }

    private async Task<string> SaveTreeCursorWithinAsync(SqliteConnection connection, SqliteTransaction transaction,
        string callerKey, string ownerKey, string scope, Guid root, Guid epoch, long snapshot, long after, Guid last, CancellationToken ct)
    {
        Guid id = Guid.NewGuid();

        await using SqliteCommand command = Command(connection, transaction, """
            INSERT INTO guild_portal_cursors(Id,InstallationGeneration,CallerKey,RootQuestId,ScopeKey,
                AfterSequence,SnapshotSequence,CreatedAtUtc,AuthorityEpoch,VisibilityKey)
            VALUES($id,$generation,$caller,$root,$scope,$after,$snapshot,$now,$epoch,$owner)
            """);

        Add(command, "$id", Id(id));

        Add(command, "$generation", Id(policy.InstallationGeneration));

        Add(command, "$caller", callerKey);

        Add(command, "$root", Id(root));

        Add(command, "$scope", scope + Id(last));

        Add(command, "$after", after);

        Add(command, "$snapshot", snapshot);

        Add(command, "$now", UtcInstantText.Format(time.GetUtcNow()));

        Add(command, "$epoch", Id(epoch));

        Add(command, "$owner", ownerKey);

        _ = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return policy.InstallationGeneration.ToString("N") + "." + epoch.ToString("N") + "." + root.ToString("N") + "." + id.ToString("N");
    }

    private static Error TreeCursorInvalid => new(ErrorCodes.Guild.CursorInvalid, "The tree cursor does not match this caller and query.");

    private static Error TreeCursorGone => new(ErrorCodes.Guild.CursorGone, "The tree owner or installation authority is no longer available.");
}
