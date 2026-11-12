using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Native Operator discovery over host-resolved owner/workspace scopes and configured destinations.</summary>
internal sealed class GuildNativeRoster(GuildStore store, GuildRosterRefreshStore refreshes,
    GuildLocalHostPolicy policy, GuildDeclaredAgentSourceReader declared, GuildRemoteCardReader remote,
    GuildConfiguredRemoteResolver resolver, IWorkspaceRegistry workspaces, IOptionsMonitor<ArcanumSettings> settings,
    TimeProvider time)
{
    private static string GlobalDirectory => Path.Combine(Path.GetDirectoryName(ArcanumPaths.GlobalSpellsDirectory)!, "agents");

    internal async Task<Result<GuildAgentCardPageDto>> ListAsync(GuildCallerContext caller, GuildListAgentsQuery query, CancellationToken ct)
    {
        if (!Admitted(caller)) return Forbidden;

        if (query.PageSize is < 1 or > 100 || query.Source is { } source && !Enum.IsDefined(source)) return Invalid;

        Result<GuildOperatorRosterVisibility> visibility = await store.ReadOperatorRosterVisibilityAsync(policy, caller, ct).ConfigureAwait(false);

        if (visibility.IsFailure) return visibility.Error;

        string binding = Digest("native-roster-list", JsonSerializer.SerializeToUtf8Bytes(query with { Cursor = null, PageSize = 100 }, GuildJsonContext.Default.GuildListAgentsQuery));

        GuildRosterCursorState state = new(0, null, null);

        if (query.Cursor is not null)
        {
            Result<GuildRosterCursorState> saved = await refreshes.ReadCursorAsync(caller.InstallationGeneration, caller.PrincipalId!.Value,
                binding, "list", query.Cursor, ct).ConfigureAwait(false);

            if (saved.IsFailure) return saved.Error;

            state = saved.Value;

            if (state.GlobalDiscoveryId != visibility.Value.Epoch || state.WorkspaceDiscoveryId != visibility.Value.Stamp) return Gone;
        }

        Result<GuildStoredCardPage> page = await store.ListOperatorCardsAsync(policy, caller, query.Source, query.Skill,
            state.After, query.PageSize, state.SnapshotSequence, ct).ConfigureAwait(false);

        if (page.IsFailure) return page.Error;

        ImmutableArray<GuildAgentCardDto>.Builder items = ImmutableArray.CreateBuilder<GuildAgentCardDto>();

        foreach (GuildStoredCard item in page.Value.Items)
        {
            Result<GuildAgentCard> current = await InspectAsync(caller, item, ct).ConfigureAwait(false);

            if (current.IsFailure) return current.Error;

            items.Add(Project(current.Value));
        }

        Result<GuildOperatorRosterVisibility> final = await store.ReadOperatorRosterVisibilityAsync(policy, caller, ct).ConfigureAwait(false);

        if (final.IsFailure) return final.Error;

        if (final.Value != visibility.Value) return Gone;

        string? next = null;

        if (page.Value.NextCardId is { } after)
        {
            Result<string> saved = await refreshes.SaveCursorAsync(caller.InstallationGeneration, caller.PrincipalId!.Value,
                binding, "list", new(0, after, page.Value.SnapshotSequence, visibility.Value.Epoch, visibility.Value.Stamp), ct).ConfigureAwait(false);

            if (saved.IsFailure) return saved.Error;

            next = saved.Value;
        }

        return new GuildAgentCardPageDto(items.ToImmutable(), next, page.Value.SnapshotSequence);
    }

    internal async Task<Result<GuildAgentCardDto>> GetAsync(GuildCallerContext caller, Guid agent, long? revision, CancellationToken ct)
    {
        if (!Admitted(caller)) return Forbidden;

        if (agent == Guid.Empty || revision is < 1) return Invalid;

        Result<GuildStoredCard> found = await store.ResolveAsync(agent, null, revision, ct).ConfigureAwait(false);

        if (found.IsFailure) return found.Error;

        Result<GuildAgentCard> card = await InspectAsync(caller, found.Value, ct).ConfigureAwait(false);

        return card.IsFailure ? card.Error : Project(card.Value);
    }

    internal async Task<Result<GuildRefreshReceipt>> RefreshAsync(GuildCallerContext caller, GuildRefreshAgentsRequest request, CancellationToken ct)
    {
        if (!Admitted(caller)) return Forbidden;

        if (request.IdempotencyKey == Guid.Empty || request.AgentId == Guid.Empty || request.AgentId is not null && request.Cursor is not null) return Invalid;

        GuildMutationIdentity mutation = new(request.IdempotencyKey, Digest("native-roster-refresh",
            JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.GuildRefreshAgentsRequest)));

        Result<GuildRefreshReceipt?> prior = await refreshes.FindAsync(caller.InstallationGeneration, caller.PrincipalId!.Value, mutation, ct).ConfigureAwait(false);

        if (prior.IsFailure) return prior.Error;

        if (prior.Value is { } accepted) return accepted;

        Result<GuildOperatorRosterVisibility> visibility = await store.ReadOperatorRosterVisibilityAsync(policy, caller, ct).ConfigureAwait(false);

        if (visibility.IsFailure) return visibility.Error;

        if (request.AgentId is { } agent)
        {
            Result<GuildStoredCard> stored = await store.ResolveAsync(agent, null, null, ct).ConfigureAwait(false);

            if (stored.IsFailure) return stored.Error;

            if (stored.Value.Payload.Card.Source.Kind == GuildAgentSourceKind.Remote)
            {
                if (stored.Value.Payload.DiscoveryEndpoint is not { } endpoint || !Configured(endpoint)) return Forbidden;

                GuildRefreshDiagnosticDto diagnostic = await RefreshRemoteAsync(caller, endpoint, ct).ConfigureAwait(false);

                return await AcceptAsync(caller, mutation, [diagnostic], null, 0, ct).ConfigureAwait(false);
            }

            Result<GuildRoster> selected = await RosterForAsync(caller, stored.Value, ct).ConfigureAwait(false);

            if (selected.IsFailure) return selected.Error;

            GuildRefreshAgentsRequest inner = new(InnerKey(mutation, "one"), agent);

            Result<GuildRefreshReceipt> refreshed = await selected.Value.RefreshAsync(caller,
                selected.Value.CreateRefreshMutation(inner), inner, ct).ConfigureAwait(false);

            if (refreshed.IsFailure) return refreshed.Error;

            return await AcceptAsync(caller, mutation, refreshed.Value.Diagnostics.Items, null,
                refreshed.Value.Diagnostics.SnapshotSequence, ct).ConfigureAwait(false);
        }

        NativeSources sources = await SourcesAsync(ct).ConfigureAwait(false);

        const string cursorNamespace = "native-roster-refresh-sources-v1";

        string binding = Digest(cursorNamespace, []);

        GuildRosterCursorState state = new(0, null, 0, visibility.Value.Epoch, sources.Stamp);

        if (request.Cursor is not null)
        {
            Result<GuildRosterCursorState> saved = await refreshes.ReadCursorAsync(caller.InstallationGeneration,
                caller.PrincipalId!.Value, binding, "refresh", request.Cursor, ct).ConfigureAwait(false);

            if (saved.IsFailure) return saved.Error;

            state = saved.Value;

            if (state.GlobalDiscoveryId != visibility.Value.Epoch || state.WorkspaceDiscoveryId != sources.Stamp) return Gone;
        }

        if (state.Stage is not (0 or 1) || state.SnapshotSequence is null or < 0 or > int.MaxValue) return Invalid;

        int index = (int)state.SnapshotSequence.Value;

        ImmutableArray<GuildRefreshDiagnosticDto> diagnostics;

        long watermark = 0;

        if (state.Stage == 0)
        {
            if (index >= sources.Scopes.Length) return Invalid;

            NativeScope scope = sources.Scopes[index];

            GuildRoster roster = Create(caller, scope.WorkspaceId, scope.Root, null, scope.WorkspaceId,
                includeGlobal: scope.WorkspaceId is null);

            GuildRefreshAgentsRequest inner = new(InnerKey(mutation, $"scope:{index}"), Cursor: state.After);

            Result<GuildRefreshReceipt> refreshed = await roster.RefreshAsync(caller, roster.CreateRefreshMutation(inner), inner, ct).ConfigureAwait(false);

            if (refreshed.IsFailure) return refreshed.Error;

            diagnostics = refreshed.Value.Diagnostics.Items;

            watermark = refreshed.Value.Diagnostics.SnapshotSequence;

            state = refreshed.Value.Diagnostics.NextCursor is { } continuation
                ? state with { After = continuation }
                : state with { After = null, SnapshotSequence = index + 1 };

            if (state.SnapshotSequence == sources.Scopes.Length) state = state with { Stage = 1, SnapshotSequence = 0 };
        }
        else
        {
            if (index >= sources.Remotes.Length || state.After is not null) return Invalid;

            diagnostics = [await RefreshRemoteAsync(caller, sources.Remotes[index], ct).ConfigureAwait(false)];

            state = state with { SnapshotSequence = index + 1 };
        }

        string? next = null;

        bool complete = state.Stage == 1 && state.SnapshotSequence == sources.Remotes.Length;

        if (!complete)
        {
            Result<string> saved = await refreshes.SaveCursorAsync(caller.InstallationGeneration, caller.PrincipalId!.Value,
                binding, "refresh", state, ct).ConfigureAwait(false);

            if (saved.IsFailure) return saved.Error;

            next = saved.Value;
        }

        return await AcceptAsync(caller, mutation, diagnostics, next, watermark, ct).ConfigureAwait(false);
    }

    private async Task<Result<GuildAgentCard>> InspectAsync(GuildCallerContext caller, GuildStoredCard stored, CancellationToken ct)
    {
        Result<GuildRoster> composed = await RosterForAsync(caller, stored, ct).ConfigureAwait(false);

        if (composed.IsFailure) return composed.Error;

        // Inspection explicitly pins the retained revision; an unavailable source remains inspectable
        // and cannot be confused with a successful new-work resolution.
        Result<GuildAgentCard> found = await composed.Value.ResolveAsync(caller, stored.Payload.Card.AgentId,
            stored.Payload.Card.CardId, stored.Payload.Card.Revision, ct).ConfigureAwait(false);

        if (found.IsFailure) return found.Error;

        GuildAgentCard card = found.Value;

        if (card.Source.Kind == GuildAgentSourceKind.Remote && (stored.Payload.DiscoveryEndpoint is not { } endpoint || !Configured(endpoint)))
            card = card with { Source = card.Source with { Available = false, ReasonCode = ErrorCodes.Guild.Unavailable } };

        return card;
    }

    private async Task<Result<GuildRoster>> RosterForAsync(GuildCallerContext caller, GuildStoredCard stored, CancellationToken ct)
    {
        if (!Admitted(caller)) return Forbidden;

        Guid? workspaceId = stored.Payload.Card.Source.WorkspaceId;

        string? root = null;

        if (workspaceId is { } id)
        {
            var workspace = await workspaces.GetAsync(id.ToString("D"), ct).ConfigureAwait(false);

            if (workspace is null || !Guid.TryParse(workspace.Id, out Guid actual) || actual != id) return Missing;

            root = workspace.Path;
        }

        return Create(caller, workspaceId, root, stored.SessionId, stored.CampaignId, includeGlobal: true);
    }

    private GuildRoster Create(GuildCallerContext caller, Guid? workspace, string? root, Guid? session, Guid? campaign, bool includeGlobal) =>
        new(store, refreshes, new(caller.InstallationGeneration, GlobalDirectory, workspace, root, session, campaign,
            IncludeGlobalSources: includeGlobal, ManageRemoteSources: false), declared, remote, time);

    private async Task<GuildRefreshDiagnosticDto> RefreshRemoteAsync(GuildCallerContext caller, string endpoint, CancellationToken ct)
    {
        Result<GuildAgentCard> result = await resolver.ResolveAsync(caller, endpoint, ct).ConfigureAwait(false);

        return result.IsFailure
            ? new(null, GuildAgentSourceKind.Remote, result.Error.Code, false, null)
            : new(result.Value.AgentId, GuildAgentSourceKind.Remote, result.Value.Source.ReasonCode, result.Value.Source.Available, result.Value.Revision);
    }

    private async Task<Result<GuildRefreshReceipt>> AcceptAsync(GuildCallerContext caller, GuildMutationIdentity mutation,
        ImmutableArray<GuildRefreshDiagnosticDto> items, string? next, long watermark, CancellationToken ct)
    {
        if (!Admitted(caller)) return Forbidden;

        GuildRefreshReceipt receipt = new(new(Guid.NewGuid(), mutation.IdempotencyKey, false, time.GetUtcNow(), 1),
            new(items, next, watermark), next is null);

        return await refreshes.AcceptAsync(caller.InstallationGeneration, caller.PrincipalId!.Value, mutation, receipt, ct).ConfigureAwait(false);
    }

    private async Task<NativeSources> SourcesAsync(CancellationToken ct)
    {
        ImmutableArray<NativeScope>.Builder scopes = ImmutableArray.CreateBuilder<NativeScope>();

        scopes.Add(new(null, null));

        foreach (var workspace in (await workspaces.GetAllAsync(ct).ConfigureAwait(false)).OrderBy(static item => item.Id, StringComparer.Ordinal))
        {
            if (Guid.TryParse(workspace.Id, out Guid id) && id != Guid.Empty) scopes.Add(new(id, Path.GetFullPath(workspace.Path)));
        }

        ImmutableArray<string> destinations = [.. settings.CurrentValue.ResolveA2A().AllowedRemoteAgents
            .Select(static endpoint => endpoint.Trim()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

        using MemoryStream bytes = new();

        using (BinaryWriter writer = new(bytes, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(GlobalDirectory);

            writer.Write(scopes.Count);

            foreach (NativeScope scope in scopes) { writer.Write(scope.WorkspaceId?.ToString("D") ?? ""); writer.Write(scope.Root ?? ""); }

            writer.Write(destinations.Length);

            foreach (string endpoint in destinations) writer.Write(endpoint);
        }

        byte[] digest = SHA256.HashData(bytes.ToArray());

        return new(scopes.ToImmutable(), destinations, new Guid(digest.AsSpan(0, 16)));
    }

    private bool Admitted(GuildCallerContext caller) => policy.Authenticates(caller) && caller.Kind == GuildCallerKind.Operator && caller.PrincipalId is not null;

    private bool Configured(string endpoint) => policy.IsConfiguredRemoteDestination(endpoint);

    private static GuildAgentCardDto Project(GuildAgentCard card) => new(card.AgentId, card.CardId, card.Revision, card.Name,
        card.Description, card.Version, card.Skills, card.DefaultInputModes, card.DefaultOutputModes, card.Capabilities, card.Source);

    private static string Digest(string domain, byte[] bytes) => Convert.ToHexString(SHA256.HashData([.. Encoding.UTF8.GetBytes(domain + "\0"), .. bytes]));

    private static Guid InnerKey(GuildMutationIdentity mutation, string stage) => new(SHA256.HashData(
        Encoding.UTF8.GetBytes($"native-roster-inner:{mutation.IdempotencyKey:D}:{mutation.CanonicalRequestDigest}:{stage}")).AsSpan(0, 16));

    private sealed record NativeScope(Guid? WorkspaceId, string? Root);

    private sealed record NativeSources(ImmutableArray<NativeScope> Scopes, ImmutableArray<string> Remotes, Guid Stamp);

    private static Error Forbidden => new(ErrorCodes.Guild.Forbidden, "A current authenticated Operator is required for Roster inspection.");

    private static Error Invalid => new(ErrorCodes.Guild.InvalidRequest, "The native Roster request is invalid.");

    private static Error Missing => new(ErrorCodes.Guild.NotFound, "The visible Agent Card was not found.");

    private static Error Gone => new(ErrorCodes.Guild.CursorGone, "The Roster source or visibility generation changed; restart discovery.");
}
