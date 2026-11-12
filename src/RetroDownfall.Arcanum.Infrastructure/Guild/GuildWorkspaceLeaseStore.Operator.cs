using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed record GuildOperatorLeasePage(WorkspaceLeasePageDto Page, long LastSequence, Guid VisibilityEpoch);

internal sealed partial class GuildWorkspaceLeaseStore
{
    internal Task<Result<GuildOperatorLeasePage>> ReadOperatorPageAsync(GuildCallerContext caller,
        Guid? workspaceId, Guid? questId, GuildWorkspaceLeaseState? state, int pageSize,
        GuildRosterCursorState? cursor, CancellationToken ct) =>
        ReadTransactionAsync<GuildOperatorLeasePage>(async (connection, transaction, token) =>
        {
            await using SqliteCommand epoch = Command(connection, transaction, """
                SELECT Epoch || ':' || coalesce((SELECT seq FROM sqlite_sequence WHERE name='owner_deletion_events'),0)
                FROM guild_signet_authority_state WHERE StateKey=1
                """);

            if (await epoch.ExecuteScalarAsync(token).ConfigureAwait(false) is not string epochText) return Unavailable;

            Guid visibility = new(SHA256.HashData(Encoding.UTF8.GetBytes(epochText)).AsSpan(0, 16));

            if (cursor is not null && cursor.GlobalDiscoveryId != visibility)
                return new Error(ErrorCodes.Guild.CursorGone, "The lease listing changed ownership; start a new listing.");

            long after = 0;

            if (cursor?.After is { } afterText && (!long.TryParse(afterText, NumberStyles.None, CultureInfo.InvariantCulture, out after) || after < 0))
                return new Error(ErrorCodes.Guild.CursorInvalid, "The lease continuation is invalid.");

            epoch.CommandText = "SELECT coalesce((SELECT seq FROM sqlite_sequence WHERE name='guild_operator_sequences'),0)";

            long snapshot = cursor?.SnapshotSequence ?? (long)(await epoch.ExecuteScalarAsync(token).ConfigureAwait(false))!;

            await using SqliteCommand command = Command(connection, transaction, $"""
                SELECT {BoundedColumns},s.Sequence FROM guild_workspace_leases
                JOIN guild_operator_sequences s ON s.KindCode=2 AND s.ObjectId=guild_workspace_leases.Id
                WHERE PrincipalId=$principal AND InstallationGeneration=$generation
                    AND ($workspace IS NULL OR WorkspaceId=$workspace)
                    AND ($quest IS NULL OR QuestId=$quest) AND ($state IS NULL OR StateCode=$state)
                    AND s.Sequence>$after AND s.Sequence<=$snapshot
                    AND EXISTS(SELECT 1 FROM Campaigns w WHERE w.Id=WorkspaceId)
                    AND (SessionId IS NULL OR EXISTS(SELECT 1 FROM Sessions owner WHERE owner.Id=SessionId AND owner.CampaignId IS guild_workspace_leases.CampaignId))
                    AND (CampaignId IS NULL OR EXISTS(SELECT 1 FROM Campaigns owner WHERE owner.Id=guild_workspace_leases.CampaignId))
                    AND NOT EXISTS(SELECT 1 FROM owner_deletion_events d WHERE
                        (d.OwnerKindCode=1 AND (d.OwnerId=WorkspaceId OR d.OwnerId=CampaignId))
                        OR (d.OwnerKindCode=2 AND d.OwnerId=SessionId))
                ORDER BY s.Sequence,s.ObjectId LIMIT $take
                """);

            Add(command, "$principal", Id(caller.PrincipalId));

            Add(command, "$generation", Id(caller.InstallationGeneration));

            Add(command, "$workspace", Id(workspaceId));

            Add(command, "$quest", Id(questId));

            Add(command, "$state", state is { } selected ? (int)selected : null);

            Add(command, "$after", after);

            Add(command, "$snapshot", snapshot);

            Add(command, "$take", pageSize + 1);

            ImmutableArray<WorkspaceLeaseDto>.Builder items = ImmutableArray.CreateBuilder<WorkspaceLeaseDto>();

            long bytes = 512;

            bool more = false;

            long last = after;

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);

            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                Result<GuildWorkspaceLeaseRecord> row = Decode(reader);

                if (row.IsFailure) return row.Error;

                Result<WorkspaceLeaseDto> projected = GuildWorkspaceLeaseBroker.ToDto(row.Value);

                if (projected.IsFailure) return projected.Error;

                int size = JsonSerializer.SerializeToUtf8Bytes(projected.Value, GuildJsonContext.Default.WorkspaceLeaseDto).Length;

                if (size > 262_144) return new Error(ErrorCodes.Guild.PayloadTooLarge, "The lease metadata exceeds its response allocation.");

                if (items.Count == pageSize || bytes + size + 1 > 1_048_576)
                {
                    more = true;

                    break;
                }

                items.Add(projected.Value);

                bytes += size + 1;

                last = reader.GetInt64(24);
            }

            return new GuildOperatorLeasePage(new(items.ToImmutable(), more ? "pending" : null, snapshot), last, visibility);
        }, ct);
}
