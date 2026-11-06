using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Infrastructure.Security;

namespace RetroDownfall.Arcanum.Infrastructure.Guild;

/// <summary>Immutable host composition facts; callers and authored Cards cannot supply this scope.</summary>
internal sealed record GuildRosterContext(Guid InstallationGeneration, string GlobalAgentsDirectory,
    Guid? WorkspaceId = null, string? WorkspaceRoot = null, Guid? SessionId = null, Guid? CampaignId = null,
    ImmutableArray<GuildConfiguredRemoteSource> RemoteSources = default);

/// <summary>Discovery stores immutable observations. It does not issue work, authenticate model claims or grant effects.</summary>
internal sealed class GuildRoster(GuildStore store, GuildRosterRefreshStore refreshes, GuildRosterContext context,
    GuildDeclaredAgentSourceReader declared, GuildRemoteCardReader remote, TimeProvider time) : IGuildRoster
{
    private GuildCardReadScope Scope => new(context.WorkspaceId, context.SessionId, context.CampaignId, WorkspaceRoot: context.WorkspaceRoot);

    public async Task<Result<GuildAgentCard>> ResolveAsync(GuildCallerContext caller, Guid agentId, Guid? cardId, long? revision,
        CancellationToken cancellationToken)
    {
        Result admitted = await AdmitAsync(caller, cancellationToken).ConfigureAwait(false);

        if (admitted.IsFailure)
        {
            return admitted.Error;
        }

        if (agentId == Guid.Empty || cardId == Guid.Empty || revision is < 1)
        {
            return Invalid();
        }

        Result<GuildStoredCard> found = await store.ResolveAsync(Scope, agentId, cardId, revision, cancellationToken).ConfigureAwait(false);

        if (found.IsFailure)
        {
            return found.Error;
        }

        Result<ImmutableHashSet<string>> eligible = await EligibilityAsync([found.Value], cancellationToken).ConfigureAwait(false);

        if (eligible.IsFailure)
        {
            return eligible.Error;
        }

        GuildAgentCard card = ApplyEligibility(found.Value, eligible.Value);

        return revision is null && !card.Source.Available
            ? new Error(ErrorCodes.Guild.Unavailable, "The agent destination is unavailable for new work.")
            : Result<GuildAgentCard>.Success(card);
    }

    public async Task<Result<GuildAgentCardPageDto>> ListAsync(GuildCallerContext caller, GuildListAgentsQuery query,
        CancellationToken cancellationToken)
    {
        Result admitted = await AdmitAsync(caller, cancellationToken).ConfigureAwait(false);

        if (admitted.IsFailure)
        {
            return admitted.Error;
        }

        if (query.PageSize is < 1 or > 100 || query.Source is { } source && !Enum.IsDefined(source))
        {
            return Invalid();
        }

        string binding = Binding("list", query.Source, query.Skill);

        GuildRosterCursorState state = new(0, null, null);

        if (query.Cursor is not null)
        {
            Result<GuildRosterCursorState> decoded = await refreshes.ReadCursorAsync(context.InstallationGeneration,
                caller.PrincipalId!.Value, binding, "list", query.Cursor, cancellationToken).ConfigureAwait(false);

            if (decoded.IsFailure)
            {
                return decoded.Error;
            }

            state = decoded.Value;
        }

        Result<GuildStoredCardPage> page = await store.ListAsync(Scope, query.Source, query.Skill, state.After,
            query.PageSize, cancellationToken, state.SnapshotSequence).ConfigureAwait(false);

        if (page.IsFailure)
        {
            return page.Error;
        }

        string? next = null;

        if (page.Value.NextCardId is not null)
        {
            Result<string> saved = await refreshes.SaveCursorAsync(context.InstallationGeneration, caller.PrincipalId!.Value,
                binding, "list", new(0, page.Value.NextCardId, page.Value.SnapshotSequence), cancellationToken).ConfigureAwait(false);

            if (saved.IsFailure)
            {
                return saved.Error;
            }

            next = saved.Value;
        }

        Result<ImmutableHashSet<string>> eligible = await EligibilityAsync(page.Value.Items, cancellationToken).ConfigureAwait(false);

        return eligible.IsFailure ? eligible.Error : new GuildAgentCardPageDto(
            [.. page.Value.Items.Select(item => Project(ApplyEligibility(item, eligible.Value)))], next, page.Value.SnapshotSequence);
    }

    public async Task<Result<GuildRefreshReceipt>> RefreshAsync(GuildCallerContext caller, GuildMutationIdentity mutation,
        GuildRefreshAgentsRequest request, CancellationToken cancellationToken)
    {
        Result admitted = await AdmitAsync(caller, cancellationToken).ConfigureAwait(false);

        if (admitted.IsFailure)
        {
            return admitted.Error;
        }

        if (request.IdempotencyKey == Guid.Empty || request.AgentId == Guid.Empty
            || request.AgentId is not null && request.Cursor is not null
            || mutation != CreateRefreshMutation(request))
        {
            return Invalid();
        }

        await refreshes.RefreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            Result<GuildRefreshReceipt?> prior = await refreshes.FindAsync(context.InstallationGeneration,
                caller.PrincipalId!.Value, mutation, cancellationToken).ConfigureAwait(false);

            if (prior.IsFailure)
            {
                return prior.Error;
            }

            if (prior.Value is { } receipt)
            {
                return receipt;
            }

            Result<GuildRefreshDiagnosticPageDto> refreshed = request.AgentId is { } agentId
                ? await RefreshOneAsync(agentId, cancellationToken).ConfigureAwait(false)
                : await RefreshPageAsync(caller, request.Cursor, cancellationToken).ConfigureAwait(false);

            if (refreshed.IsFailure)
            {
                return refreshed.Error;
            }

            GuildRefreshReceipt accepted = new(new(Guid.NewGuid(), mutation.IdempotencyKey, false, time.GetUtcNow(), 1),
                refreshed.Value, refreshed.Value.NextCursor is null);

            return await refreshes.AcceptAsync(context.InstallationGeneration, caller.PrincipalId!.Value,
                mutation, accepted, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            refreshes.RefreshGate.Release();
        }
    }

    internal GuildMutationIdentity CreateRefreshMutation(GuildRefreshAgentsRequest request)
    {
        byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, GuildJsonContext.Default.GuildRefreshAgentsRequest);

        return new(request.IdempotencyKey, Convert.ToHexString(SHA256.HashData(
            [.. Encoding.UTF8.GetBytes(Binding("refresh", null, null)), .. requestBytes])));
    }

    internal async Task<Result<GuildAgentCard>> ResolveSelectorAsync(GuildCallerContext caller, string selector,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return Invalid();
        }

        GuildAgentSourceKind? kind = null;

        string name = selector;

        if (selector.StartsWith("global:", StringComparison.Ordinal))
        {
            kind = GuildAgentSourceKind.DeclaredGlobal;

            name = selector[7..];
        }
        else if (selector.StartsWith("workspace:", StringComparison.Ordinal))
        {
            kind = GuildAgentSourceKind.DeclaredWorkspace;

            name = selector[10..];
        }

        List<GuildAgentCardDto> matches = [];

        string? cursor = null;

        do
        {
            Result<GuildAgentCardPageDto> page = await ListAsync(caller, new(kind, null, cursor), cancellationToken).ConfigureAwait(false);

            if (page.IsFailure)
            {
                return page.Error;
            }

            matches.AddRange(page.Value.Items.Where(item => string.Equals(item.Name, name, StringComparison.Ordinal))
                .Take(2 - matches.Count));

            if (matches.Count == 2)
            {
                break;
            }

            cursor = page.Value.NextCursor;
        } while (cursor is not null);

        if (matches.Count == 0)
        {
            return new Error(ErrorCodes.Guild.NotFound, "The visible agent was not found.");
        }

        if (matches.Count > 1)
        {
            string candidates = string.Join(", ", matches.Select(item => $"{item.AgentId:D} ({item.Source.Kind})"));

            return new Error(ErrorCodes.Guild.SourceConflict, $"The agent name is ambiguous. Visible candidates include: {candidates}.");
        }

        return await ResolveAsync(caller, matches[0].AgentId, matches[0].CardId, null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<Result<GuildAgentCard>> AdmitDerivedAsync(GuildCallerContext caller, GuildIssuanceIdentity issuance,
        string name, string description, ArtifactSensitivityLabel? sensitivity, CancellationToken cancellationToken)
    {
        Result admitted = await AdmitAsync(caller, cancellationToken).ConfigureAwait(false);

        if (admitted.IsFailure)
        {
            return admitted.Error;
        }

        if (context.SessionId is null || issuance.InstallationGeneration != context.InstallationGeneration
            || issuance.LogicalCallId == Guid.Empty || issuance.IssuingStepId == Guid.Empty
            || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description))
        {
            return Invalid();
        }

        string source = $"derived:{issuance.InstallationGeneration:D}:{issuance.IssuerQuestId}:{issuance.RootInvocationId}:{issuance.IssuingStepId:D}:{issuance.LogicalCallId:D}";

        GuildParsedAgent parsed = new(null, name, description, "1", ["apprentice-goal-execution"], ["text/plain"], ["text/plain"], [], string.Empty);

        GuildStoredAgentCardPayload payload = Payload(parsed, GuildAgentSourceKind.Derived, null);

        string digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(payload,
            GuildStorageJsonContext.Default.GuildStoredAgentCardPayload)));

        Result<GuildStoredCard> written = await store.UpsertCardAsync(new(source, null, context.SessionId,
            context.CampaignId, payload, digest, Label: sensitivity), cancellationToken).ConfigureAwait(false);

        return written.IsFailure ? written.Error : Result<GuildAgentCard>.Success(written.Value.Payload.Card);
    }

    internal async Task<Result> RebindExplicitAsync(GuildCallerContext caller, Guid alias,
        GuildDeclaredAgentObservation replacement, long expectedRevision, CancellationToken cancellationToken)
    {
        Result admitted = await AdmitAsync(caller, cancellationToken).ConfigureAwait(false);

        if (admitted.IsFailure)
        {
            return admitted;
        }

        if (alias == Guid.Empty || replacement.Agent?.ExplicitId != alias || replacement.ReasonCode is not null)
        {
            return Result.Failure(Invalid());
        }

        Result<GuildStoredCard> existing = await store.GetByExplicitIdAsync(alias, cancellationToken).ConfigureAwait(false);

        if (existing.IsFailure)
        {
            return Result.Failure(existing.Error);
        }

        if (existing.Value.Payload.Card.Source.Kind != replacement.Kind
            || existing.Value.Payload.Card.Source.WorkspaceId != replacement.WorkspaceId)
        {
            return Result.Failure(new Error(ErrorCodes.Guild.SourceConflict,
                "Explicit identity rebinding requires the same declared source scope."));
        }

        if (!Visible(existing.Value) || !GuildDeclaredAgentSourceReader.TrySourcePath(existing.Value.SourceIdentity, out string oldPath))
        {
            return Result.Failure(new Error(ErrorCodes.Guild.NotFound, "The visible agent was not found."));
        }

        Result<NoFollowPathTopologyKind> topology = NoFollowPathTopology.Classify(oldPath);

        if (topology.IsFailure || topology.Value != NoFollowPathTopologyKind.Absent)
        {
            return Result.Failure(new Error(ErrorCodes.Guild.SourceConflict, "The prior declared source must be removed before rebinding."));
        }

        return await store.RebindDeclaredSourceAsync(alias, existing.Value.SourceIdentity, replacement,
            expectedRevision, context.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<GuildRefreshDiagnosticPageDto>> RefreshOneAsync(Guid agentId, CancellationToken token)
    {
        Result<GuildStoredCard> found = await store.ResolveAsync(Scope, agentId, null, null, token).ConfigureAwait(false);

        if (found.IsFailure)
        {
            return found.Error;
        }

        GuildStoredCard existing = found.Value;

        GuildAgentCard card = existing.Payload.Card;

        Result<GuildRefreshDiagnosticDto> diagnostic;

        if (card.Source.Kind == GuildAgentSourceKind.Remote)
        {
            GuildConfiguredRemoteSource? configured = Remotes.FirstOrDefault(item => GuildRemoteCardReader.SourceIdentity(item) == existing.SourceIdentity);

            diagnostic = configured is null
                ? await UnavailableAsync(existing.SourceIdentity, card.Source.Kind, ErrorCodes.Guild.DestinationChanged, token).ConfigureAwait(false)
                : await RefreshRemoteAsync(configured, token).ConfigureAwait(false);
        }
        else if (card.Source.Kind is GuildAgentSourceKind.DeclaredGlobal or GuildAgentSourceKind.DeclaredWorkspace
            && GuildDeclaredAgentSourceReader.TrySourcePath(existing.SourceIdentity, out string path))
        {
            (string owner, string directory) = DeclaredDirectory(card.Source.Kind);

            if (!WorkspacePathPolicy.TryCanonicalize(directory, out string? canonicalDirectory, out _)
                || !string.Equals(Path.GetFullPath(Path.GetDirectoryName(path)!), canonicalDirectory,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                return new Error(ErrorCodes.Guild.WorkspaceIdentityUnavailable, "The current source does not match its registered directory.");
            }

            Result<GuildDeclaredAgentPage> observed = await declared.ReadFileAsync(owner, directory, card.Source.Kind,
                card.Source.WorkspaceId, Path.GetFileName(path), token).ConfigureAwait(false);

            diagnostic = observed.IsSuccess && observed.Value.Items.Length == 1
                ? await ApplyAsync(observed.Value.Items[0], token).ConfigureAwait(false)
                : await UnavailableAsync(existing.SourceIdentity, card.Source.Kind,
                    observed.IsFailure ? observed.Error.Code : ErrorCodes.Guild.Unavailable, token).ConfigureAwait(false);
        }
        else
        {
            return Invalid();
        }

        if (diagnostic.IsFailure)
        {
            return diagnostic.Error;
        }

        Result<ImmutableHashSet<string>> eligible = await store.ReadDeclaredEligibilityAsync(context.InstallationGeneration,
            Scope, context.GlobalAgentsDirectory, [existing.SourceIdentity], token).ConfigureAwait(false);

        if (eligible.IsFailure)
        {
            return eligible.Error;
        }

        GuildRefreshDiagnosticDto observedDiagnostic = diagnostic.Value;

        if (card.Source.Kind is GuildAgentSourceKind.DeclaredGlobal or GuildAgentSourceKind.DeclaredWorkspace)
        {
            observedDiagnostic = observedDiagnostic with { Available = observedDiagnostic.Available && eligible.Value.Contains(existing.SourceIdentity) };
        }

        return new GuildRefreshDiagnosticPageDto([observedDiagnostic], null, 0);
    }

    private async Task<Result<GuildRefreshDiagnosticPageDto>> RefreshPageAsync(GuildCallerContext caller, string? cursor, CancellationToken token)
    {
        string binding = Binding("refresh", null, null);

        GuildRosterCursorState state = new(0, null, null);

        if (cursor is not null)
        {
            Result<GuildRosterCursorState> decoded = await refreshes.ReadCursorAsync(context.InstallationGeneration,
                caller.PrincipalId!.Value, binding, "refresh", cursor, token).ConfigureAwait(false);

            if (decoded.IsFailure)
            {
                return decoded.Error;
            }

            state = decoded.Value;

            if (state.GlobalDiscoveryId is null || context.WorkspaceId is not null && state.WorkspaceDiscoveryId is null)
            {
                return new Error(ErrorCodes.Guild.CursorGone, "The declared-source discovery is unavailable.");
            }

            Result current = await store.ValidateDeclaredRootScansAsync(context.InstallationGeneration, Scans(state), token).ConfigureAwait(false);

            if (current.IsFailure)
            {
                return current.Error;
            }
        }
        else
        {
            Result<Guid> global = await store.BeginDeclaredRootScanAsync(context.InstallationGeneration,
                GuildAgentSourceKind.DeclaredGlobal, null, context.GlobalAgentsDirectory, token).ConfigureAwait(false);

            if (global.IsFailure)
            {
                return global.Error;
            }

            state = state with { GlobalDiscoveryId = global.Value };

            if (context.WorkspaceId is { } workspace)
            {
                Result<Guid> local = await store.BeginDeclaredRootScanAsync(context.InstallationGeneration,
                    GuildAgentSourceKind.DeclaredWorkspace, workspace, DeclaredDirectory(GuildAgentSourceKind.DeclaredWorkspace).Directory, token).ConfigureAwait(false);

                if (local.IsFailure)
                {
                    return local.Error;
                }

                state = state with { WorkspaceDiscoveryId = local.Value };
            }
        }

        ImmutableArray<GuildRefreshDiagnosticDto>.Builder diagnostics = ImmutableArray.CreateBuilder<GuildRefreshDiagnosticDto>();

        Dictionary<Guid, string> declaredSources = [];

        int examined = 0;

        while (examined < 100 && state.Stage < 4)
        {
            if (state.Stage is 0 or 1)
            {
                if (state.Stage == 1 && context.WorkspaceId is null)
                {
                    state = state with { Stage = 2, After = null, SnapshotSequence = null };

                    continue;
                }

                GuildAgentSourceKind kind = state.Stage == 0 ? GuildAgentSourceKind.DeclaredGlobal : GuildAgentSourceKind.DeclaredWorkspace;

                (string owner, string directory) = DeclaredDirectory(kind);

                Result<GuildDeclaredAgentPage> observed = await declared.ReadPageAsync(owner, directory, kind,
                    kind == GuildAgentSourceKind.DeclaredWorkspace ? context.WorkspaceId : null, state.After, 100 - examined, token).ConfigureAwait(false);

                if (observed.IsFailure)
                {
                    Result invalidated = await store.InvalidateDeclaredRootScansAsync(context.InstallationGeneration, Scans(state), token).ConfigureAwait(false);

                    return invalidated.IsFailure ? invalidated.Error : observed.Error;
                }

                GuildDeclaredRootScan scan = Scans(state).Single(item => item.Kind == kind);

                foreach (GuildDeclaredAgentObservation item in observed.Value.Items)
                {
                    Result<GuildRefreshDiagnosticDto> applied = await ApplyAsync(item, token, scan).ConfigureAwait(false);

                    if (applied.IsFailure)
                    {
                        return applied.Error;
                    }

                    diagnostics.Add(applied.Value);

                    if (applied.Value.AgentId is { } agentId)
                    {
                        declaredSources[agentId] = item.SourceIdentity;
                    }

                    examined++;
                }

                state = observed.Value.NextName is { } after
                    ? state with { After = after }
                    : state with { Stage = state.Stage + 1, After = null, SnapshotSequence = null };
            }
            else if (state.Stage == 2)
            {
                Result completed = await store.CompleteDeclaredRootScansAsync(context.InstallationGeneration, Scans(state), token).ConfigureAwait(false);

                if (completed.IsFailure)
                {
                    return completed.Error;
                }

                int index = 0;

                if (state.After is not null && (!int.TryParse(state.After, NumberStyles.None, CultureInfo.InvariantCulture, out index)
                        || index < 0 || index > Remotes.Length))
                {
                    return new Error(ErrorCodes.Guild.CursorInvalid, "The refresh continuation is invalid.");
                }

                while (index < Remotes.Length && examined < 100)
                {
                    Result<GuildRefreshDiagnosticDto> refreshed = await RefreshRemoteAsync(Remotes[index++], token).ConfigureAwait(false);

                    if (refreshed.IsFailure)
                    {
                        return refreshed.Error;
                    }

                    diagnostics.Add(refreshed.Value);

                    examined++;
                }

                state = index == Remotes.Length
                    ? state with { Stage = 3, After = null, SnapshotSequence = null }
                    : state with { After = index.ToString(CultureInfo.InvariantCulture) };
            }
            else
            {
                Result<GuildStoredCardPage> cached = await store.ListAsync(Scope, null, null, state.After,
                    100 - examined, token, state.SnapshotSequence).ConfigureAwait(false);

                if (cached.IsFailure)
                {
                    return cached.Error;
                }

                foreach (GuildStoredCard item in cached.Value.Items)
                {
                    examined++;

                    GuildAgentSourceKind kind = item.Payload.Card.Source.Kind;

                    bool missing = kind == GuildAgentSourceKind.Remote
                        ? !Remotes.Any(source => GuildRemoteCardReader.SourceIdentity(source) == item.SourceIdentity)
                        : kind is GuildAgentSourceKind.DeclaredGlobal or GuildAgentSourceKind.DeclaredWorkspace
                            && (!GuildDeclaredAgentSourceReader.TrySourcePath(item.SourceIdentity, out string path)
                                || !FileHandleIdentityInterop.TryGetPathMetadataNoFollow(path, out FileHandleMetadata metadata)
                                || metadata.Kind != FileSystemObjectKind.RegularFile);

                    if (missing)
                    {
                        Result<GuildRefreshDiagnosticDto> unavailable = await UnavailableAsync(item.SourceIdentity, kind,
                            ErrorCodes.Guild.Unavailable, token).ConfigureAwait(false);

                        if (unavailable.IsFailure)
                        {
                            return unavailable.Error;
                        }

                        diagnostics.Add(unavailable.Value);
                    }
                }

                state = cached.Value.NextCardId is { } after
                    ? state with { After = after, SnapshotSequence = cached.Value.SnapshotSequence }
                    : state with { Stage = 4, After = null, SnapshotSequence = cached.Value.SnapshotSequence };
            }
        }

        string? next = null;

        if (state.Stage < 4)
        {
            Result<string> saved = await refreshes.SaveCursorAsync(context.InstallationGeneration, caller.PrincipalId!.Value,
                binding, "refresh", state, token).ConfigureAwait(false);

            if (saved.IsFailure)
            {
                return saved.Error;
            }

            next = saved.Value;
        }

        Result<ImmutableHashSet<string>> eligible = await store.ReadDeclaredEligibilityAsync(context.InstallationGeneration,
            Scope, context.GlobalAgentsDirectory, [.. declaredSources.Values.Distinct(StringComparer.Ordinal)], token).ConfigureAwait(false);

        if (eligible.IsFailure)
        {
            return eligible.Error;
        }

        return new GuildRefreshDiagnosticPageDto([.. diagnostics.Select(item => item.AgentId is { } id
                && declaredSources.TryGetValue(id, out string? source)
                    ? item with { Available = item.Available && eligible.Value.Contains(source) }
                    : item)], next, state.SnapshotSequence ?? 0);
    }

    private async Task<Result<GuildRefreshDiagnosticDto>> ApplyAsync(GuildDeclaredAgentObservation observation, CancellationToken token,
        GuildDeclaredRootScan? scan = null)
    {
        scan ??= new(observation.Kind, observation.WorkspaceId, DeclaredDirectory(observation.Kind).Directory, Guid.Empty);

        Result<bool> claim = await store.ObserveDeclaredSourceAsync(context.InstallationGeneration, scan, observation, token).ConfigureAwait(false);

        if (claim.IsFailure)
        {
            return claim.Error;
        }

        if (observation.Agent is not { } parsed)
        {
            return await UnavailableAsync(observation.SourceIdentity, observation.Kind,
                observation.ReasonCode ?? ErrorCodes.Guild.Unavailable, token).ConfigureAwait(false);
        }

        GuildStoredAgentCardPayload payload = Payload(parsed, observation.Kind, observation.WorkspaceId);

        if (claim.Value)
        {
            payload = payload with { Card = payload.Card with { Source = payload.Card.Source with { Available = false, ReasonCode = ErrorCodes.Guild.SourceConflict } } };
        }

        Result<GuildStoredCard> written = await store.UpsertCardAsync(new(observation.SourceIdentity, parsed.ExplicitId,
            null, null, payload, observation.Digest!,
            WorkspaceRoot: observation.Kind == GuildAgentSourceKind.DeclaredWorkspace ? context.WorkspaceRoot : null), token).ConfigureAwait(false);

        if (written.IsFailure)
        {
            if (written.Error.Code == ErrorCodes.Guild.SourceConflict && parsed.ExplicitId is { } alias)
            {
                Result<GuildStoredCard> existing = await store.GetByExplicitIdAsync(alias, token).ConfigureAwait(false);

                if (existing.IsSuccess)
                {
                    Result marked = await store.SetUnavailableAsync(existing.Value.SourceIdentity, ErrorCodes.Guild.SourceConflict, token).ConfigureAwait(false);

                    if (marked.IsFailure)
                    {
                        return marked.Error;
                    }
                }
            }

            return await UnavailableAsync(observation.SourceIdentity, observation.Kind, written.Error.Code, token).ConfigureAwait(false);
        }

        GuildAgentCard card = written.Value.Payload.Card;

        return new GuildRefreshDiagnosticDto(card.AgentId, observation.Kind, claim.Value ? ErrorCodes.Guild.SourceConflict : null,
            card.Source.Available, card.Revision);
    }

    private async Task<Result<GuildRefreshDiagnosticDto>> RefreshRemoteAsync(GuildConfiguredRemoteSource configured, CancellationToken token)
    {
        Result<GuildRemoteCardObservation> observed = await remote.ReadAsync(configured, token).ConfigureAwait(false);

        string source = GuildRemoteCardReader.SourceIdentity(configured);

        if (observed.IsFailure)
        {
            return await UnavailableAsync(source, GuildAgentSourceKind.Remote, observed.Error.Code, token).ConfigureAwait(false);
        }

        Result<GuildStoredCard> written = await store.UpsertCardAsync(new(source, null, null, null,
            new(observed.Value.Card, string.Empty, []), observed.Value.Digest), token).ConfigureAwait(false);

        return written.IsFailure
            ? await UnavailableAsync(source, GuildAgentSourceKind.Remote, written.Error.Code, token).ConfigureAwait(false)
            : new GuildRefreshDiagnosticDto(written.Value.Payload.Card.AgentId, GuildAgentSourceKind.Remote, null, true, written.Value.Payload.Card.Revision);
    }

    private async Task<Result<GuildRefreshDiagnosticDto>> UnavailableAsync(string source, GuildAgentSourceKind kind, string reason, CancellationToken token)
    {
        Result changed = await store.SetUnavailableAsync(source, reason, token).ConfigureAwait(false);

        if (changed.IsFailure)
        {
            return changed.Error;
        }

        Result<GuildStoredCard> existing = await store.GetBySourceAsync(source, token).ConfigureAwait(false);

        if (existing.IsFailure && existing.Error.Code != ErrorCodes.Guild.NotFound)
        {
            return existing.Error;
        }

        return new GuildRefreshDiagnosticDto(existing.IsSuccess ? existing.Value.Payload.Card.AgentId : null, kind,
            reason, false, existing.IsSuccess ? existing.Value.Payload.Card.Revision : null);
    }

    private async Task<Result> AdmitAsync(GuildCallerContext caller, CancellationToken token)
    {
        if (caller.Kind != GuildCallerKind.Operator)
        {
            return Result.Failure(new Error(ErrorCodes.Guild.Forbidden, "Roster operator admission is required."));
        }

        if (caller.InstallationGeneration != context.InstallationGeneration || context.InstallationGeneration == Guid.Empty)
        {
            return Result.Failure(new Error(ErrorCodes.Guild.AuthorityUnavailable, "The installation context is no longer current."));
        }

        if (context.WorkspaceId is { } workspace && context.CampaignId is { } campaign && workspace != campaign)
        {
            return Result.Failure(new Error(ErrorCodes.Guild.OwnerUnavailable, "The selected workspace and Campaign do not agree."));
        }

        if (context.WorkspaceId.HasValue != !string.IsNullOrWhiteSpace(context.WorkspaceRoot))
        {
            return Result.Failure(new Error(ErrorCodes.Guild.WorkspaceIdentityUnavailable, "The selected workspace binding is incomplete."));
        }

        return await store.RequireScopeAsync(Scope, token).ConfigureAwait(false);
    }

    private ImmutableArray<GuildDeclaredRootScan> Scans(GuildRosterCursorState state)
    {
        ImmutableArray<GuildDeclaredRootScan>.Builder scans = ImmutableArray.CreateBuilder<GuildDeclaredRootScan>();

        scans.Add(new(GuildAgentSourceKind.DeclaredGlobal, null, context.GlobalAgentsDirectory, state.GlobalDiscoveryId!.Value));

        if (context.WorkspaceId is { } workspace)
        {
            scans.Add(new(GuildAgentSourceKind.DeclaredWorkspace, workspace,
                DeclaredDirectory(GuildAgentSourceKind.DeclaredWorkspace).Directory, state.WorkspaceDiscoveryId!.Value));
        }

        return scans.ToImmutable();
    }

    private Task<Result<ImmutableHashSet<string>>> EligibilityAsync(ImmutableArray<GuildStoredCard> items, CancellationToken token) =>
        store.ReadDeclaredEligibilityAsync(context.InstallationGeneration, Scope, context.GlobalAgentsDirectory,
            [.. items.Where(item => item.Payload.Card.Source.Kind is GuildAgentSourceKind.DeclaredGlobal or GuildAgentSourceKind.DeclaredWorkspace)
                .Select(item => item.SourceIdentity).Distinct(StringComparer.Ordinal)], token);

    private static GuildAgentCard ApplyEligibility(GuildStoredCard item, ImmutableHashSet<string> eligible)
    {
        GuildAgentCard card = item.Payload.Card;

        return card.Source.Kind is GuildAgentSourceKind.DeclaredGlobal or GuildAgentSourceKind.DeclaredWorkspace
            ? card with { Source = card.Source with { Available = card.Source.Available && eligible.Contains(item.SourceIdentity) } }
            : card;
    }

    private bool Visible(GuildStoredCard item) => item.Payload.Card.Source.Kind switch
    {
        GuildAgentSourceKind.DeclaredWorkspace => item.Payload.Card.Source.WorkspaceId == context.WorkspaceId,
        GuildAgentSourceKind.Derived => item.SessionId == context.SessionId && item.CampaignId == context.CampaignId,
        _ => true,
    };

    private ImmutableArray<GuildConfiguredRemoteSource> Remotes => context.RemoteSources.IsDefault ? [] : context.RemoteSources;

    private (string Owner, string Directory) DeclaredDirectory(GuildAgentSourceKind kind) => kind == GuildAgentSourceKind.DeclaredGlobal
        ? (Path.GetDirectoryName(Path.GetFullPath(context.GlobalAgentsDirectory))!, Path.GetFullPath(context.GlobalAgentsDirectory))
        : (context.WorkspaceRoot!, Path.Combine(context.WorkspaceRoot!, ".arcanum", "agents"));

    private string Binding(string kind, GuildAgentSourceKind? source, string? skill)
    {
        using MemoryStream bytes = new();

        using (BinaryWriter writer = new(bytes, Encoding.UTF8, leaveOpen: true))
        {
            foreach (string value in new[] { kind, context.InstallationGeneration.ToString("D"), context.WorkspaceId?.ToString("D") ?? "",
                         context.SessionId?.ToString("D") ?? "", context.CampaignId?.ToString("D") ?? "", context.GlobalAgentsDirectory,
                         context.WorkspaceRoot ?? "", source?.ToString() ?? "", skill ?? "" })
            {
                writer.Write(value);
            }

            writer.Write(Remotes.Length);

            foreach (GuildConfiguredRemoteSource remoteSource in Remotes)
            {
                writer.Write(GuildRemoteCardReader.SourceIdentity(remoteSource));

                writer.Write(remoteSource.DiscoveryEndpoint);

                writer.Write(remoteSource.Transport.Endpoint);

                writer.Write(remoteSource.Transport.ProtocolVersion);

                writer.Write(remoteSource.Transport.CredentialReference ?? string.Empty);
            }
        }

        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    private static GuildStoredAgentCardPayload Payload(GuildParsedAgent parsed, GuildAgentSourceKind kind, Guid? workspace) =>
        new(new(Guid.Empty, Guid.Empty, 0, parsed.Name, parsed.Description, parsed.Version,
            [.. parsed.Skills.Select(id => new GuildAgentSkillDto(id, id, id, [], parsed.InputModes, parsed.OutputModes))],
            parsed.InputModes, parsed.OutputModes, new(false, false, false), new(kind, workspace, null, true, null)),
            parsed.Guidance, parsed.RequestedTools);

    private static GuildAgentCardDto Project(GuildAgentCard card) => new(card.AgentId, card.CardId, card.Revision,
        card.Name, card.Description, card.Version, card.Skills, card.DefaultInputModes, card.DefaultOutputModes, card.Capabilities, card.Source);

    private static Error Invalid() => new(ErrorCodes.Guild.InvalidRequest, "The Roster request is invalid.");
}
