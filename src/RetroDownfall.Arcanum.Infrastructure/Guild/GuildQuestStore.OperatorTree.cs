using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

internal sealed partial class GuildQuestStore
{
    internal Task<Result<GuildQuestPageDto>> GetOperatorTreePageAsync(GuildCallerContext caller, Guid subtree,
        GuildPageQuery query, CancellationToken ct) =>
        authority.InQuestTransactionAsync<GuildQuestPageDto>(subtree,
            (connection, transaction, _, token) => GetOperatorTreePageWithinAsync(connection, transaction, caller, subtree, query, token), ct);

    internal async Task<Result<GuildQuestPageDto>> GetOperatorTreePageWithinAsync(SqliteConnection connection,
        SqliteTransaction transaction, GuildCallerContext caller, Guid subtree, GuildPageQuery query, CancellationToken ct)
    {
        if (caller is null || caller.Kind != GuildCallerKind.Operator || subtree == Guid.Empty || query is null
            || query.PageSize is < 1 or > 100)
            return new Error(ErrorCodes.Guild.InvalidRequest, "The tree query is invalid.");

        string callerKey = TreeCallerKey(caller);

        string scope = "operator-" + TreeScope(subtree, query.PageSize, null);

        Result<Guid> epoch = await GuildSignetStore.EpochAsync(connection, transaction, ct).ConfigureAwait(false);

        if (epoch.IsFailure) return epoch.Error;

        TreeCheckpoint? checkpoint = null;

        if (query.Cursor is { } cursor)
        {
            Result<TreeCheckpoint> saved = await ReadTreeCursorWithinAsync(connection, transaction, callerKey, scope, cursor, epoch.Value, ct).ConfigureAwait(false);

            if (saved.IsFailure) return saved.Error;

            checkpoint = saved.Value;
        }

        if (caller.InstallationGeneration != policy.InstallationGeneration)
            return checkpoint is null ? OwnerUnavailable : TreeCursorGone;

        Result located = await policy.AuthorizeLocateAsync(caller, connection, transaction, subtree, null, null, ct).ConfigureAwait(false);

        if (located.IsFailure) return located.Error.Code == ErrorCodes.Guild.Forbidden ? OwnerUnavailable : located.Error;

        Result<GuildQuestStored> found = await ReadWithinAsync(connection, transaction, subtree, ct).ConfigureAwait(false);

        if (found.IsFailure)
            return checkpoint is not null && found.Error.Code is ErrorCodes.Guild.NotFound or ErrorCodes.Guild.OwnerUnavailable
                ? TreeCursorGone : found.Error;

        Result visible = await RequireVisibleAsync(connection, transaction, caller, found.Value, ct).ConfigureAwait(false);

        if (visible.IsFailure)
            return checkpoint is not null && visible.Error.Code == ErrorCodes.Guild.OwnerUnavailable ? TreeCursorGone
                : visible.Error.Code == ErrorCodes.Guild.Forbidden ? OwnerUnavailable : visible.Error;

        GuildQuestDto owner = found.Value.Quest;

        Result<string> visibility = await GuildOperatorVisibility.ReadAsync(connection, transaction, policy, ct).ConfigureAwait(false);

        if (visibility.IsFailure) return visibility.Error;

        string ownerKey = GuildOperatorVisibility.Bind(TreeOwnerKey(owner), visibility.Value);

        if (checkpoint is not null && (checkpoint.Root != owner.RootQuestId || checkpoint.OwnerKey != ownerKey)) return TreeCursorGone;

        Result<GuildQuestTreePage> structural = await ReadTreePageWithinAsync(connection, transaction, owner, checkpoint, query.PageSize, null, ct).ConfigureAwait(false);

        if (structural.IsFailure) return structural.Error;

        ImmutableArray<GuildQuestDto>.Builder items = ImmutableArray.CreateBuilder<GuildQuestDto>();

        bool more = structural.Value.NextCursor is not null;

        foreach (GuildQuestTreeNode node in structural.Value.Items)
        {
            Result<GuildQuestStored> member = await ReadWithinAsync(connection, transaction, node.Id, ct).ConfigureAwait(false);

            if (member.IsFailure) return member.Error;

            Result memberVisible = await RequireVisibleAsync(connection, transaction, caller, member.Value, ct).ConfigureAwait(false);

            if (memberVisible.IsFailure) return memberVisible.Error;

            items.Add(member.Value.Quest with { Receipt = null });

            GuildQuestPageDto candidate = new(items.ToImmutable(), new string('x', 131), structural.Value.SnapshotSequence);

            if (JsonSerializer.SerializeToUtf8Bytes(candidate, GuildJsonContext.Default.GuildQuestPageDto).Length > TreePageMaximumBytes)
            {
                items.RemoveAt(items.Count - 1);

                if (items.Count == 0) return new Error(ErrorCodes.Guild.PayloadTooLarge, "The Quest exceeds the page boundary.");

                more = true;

                break;
            }
        }

        string? next = null;

        if (more && items.Count > 0)
        {
            GuildQuestDto last = items[^1];

            next = await SaveTreeCursorWithinAsync(connection, transaction, callerKey, ownerKey, scope, owner.RootQuestId,
                epoch.Value, structural.Value.SnapshotSequence, last.IssuedSequence, last.Id, ct).ConfigureAwait(false);
        }

        return new GuildQuestPageDto(items.ToImmutable(), next, structural.Value.SnapshotSequence);
    }
}
