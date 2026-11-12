using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization.Metadata;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Cli.UX;
using RetroDownfall.Arcanum.Core.Guild;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Serialization;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;

namespace RetroDownfall.Arcanum.Cli.Services;

internal sealed record GuildResourceChoice(Guid Id, string Name, string Summary, GuildAgentCardDto? Card = null, string? Path = null);

/// <summary>Visible HTTP catalogs only. Selection never grants authority or substitutes resource kinds.</summary>
internal sealed class GuildCliResourceCatalog(
    ArcanumApiClient apiClient,
    ICliEnvironment environment,
    IResourcePicker picker,
    IRecentResourceStore recentStore)
{
    public async Task<ResourceSelectionResult<GuildResourceChoice>> SelectAsync(
        string kind, string? identifier, Guid? scope, CancellationToken cancellationToken)
    {
        if (kind == "content")
        {
            identifier = identifier?.Trim();

            if (string.IsNullOrEmpty(identifier))
                return ResourceSelectionResult<GuildResourceChoice>.Failure("A content UUID or UUID prefix is required; protected content has no browsing picker.");
        }

        // Exact Card and Workspace identities still resolve through their visible host catalogs.
        // Other exact identities use their owning endpoint to prove current visibility.
        if (kind is not ("agent" or "workspace") && Guid.TryParseExact(identifier, "D", out Guid exact) && exact != Guid.Empty)
        {
            return ResourceSelectionResult<GuildResourceChoice>.Selected(new(exact, exact.ToString("D"), kind));
        }

        string? source = null;

        if (kind == "agent" && identifier is not null)
        {
            if (identifier.StartsWith("workspace:", StringComparison.Ordinal))
            {
                source = "declaredWorkspace";

                identifier = identifier[10..];
            }
            else if (identifier.StartsWith("global:", StringComparison.Ordinal))
            {
                source = "declaredGlobal";

                identifier = identifier[7..];
            }
        }

        ResourceDescriptor<GuildResourceChoice> descriptor = new(kind, ["Name", "State"],
            static item => item.Id.ToString("D"),
            item => kind == "workspace" && string.Equals(item.Path, identifier, StringComparison.OrdinalIgnoreCase) ? item.Path! : item.Name,
            static item => item.Summary,
            static item => [item.Name, item.Summary]);

        IAsyncEnumerator<Result<ResourcePage<GuildResourceChoice>>>? pages = null;

        long nextPage = 0;

        ResourceSelectionRequest<GuildResourceChoice> request = new("guild-" + kind, identifier,
            environment.IsInteractive, descriptor,
            async (token, ct) =>
            {
                if (token is null)
                {
                    if (pages is not null) await pages.DisposeAsync().ConfigureAwait(false);

                    pages = (kind == "content" ? ContentPagesAsync(identifier!, ct) : PagesAsync(kind, scope, source, ct)).GetAsyncEnumerator(ct);

                    nextPage = 0;
                }
                else if (!long.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out long pageNumber) || pageNumber != nextPage)
                {
                    return new Error("Api.PaginationNoProgress", "The Guild selector cursor did not advance.");
                }

                if (pages is null || !await pages.MoveNextAsync().ConfigureAwait(false))
                    return new ResourcePage<GuildResourceChoice>([], null);

                Result<ResourcePage<GuildResourceChoice>> page = pages.Current;

                if (page.IsFailure) return page;

                nextPage++;

                return new ResourcePage<GuildResourceChoice>(page.Value.Items,
                    page.Value.NextToken is null ? null : nextPage.ToString(CultureInfo.InvariantCulture));
            }, MatchIdPrefixes: true);

        try
        {
            return await new ResourceSelector<GuildResourceChoice>(picker, recentStore).SelectAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (pages is not null) await pages.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async IAsyncEnumerable<Result<ResourcePage<GuildResourceChoice>>> PagesAsync(
        string kind, Guid? scope, string? source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (kind is "offer" && scope is null)
        {
            // Offers have an owner-scoped list. Walk visible Cohorts in bounded pages rather than
            // inventing a global endpoint or collecting all offer bodies in memory.
            await foreach (Result<ResourcePage<GuildResourceChoice>> cohorts in PagesAsync("cohort", null, null, cancellationToken).ConfigureAwait(false))
            {
                if (cohorts.IsFailure) { yield return cohorts; yield break; }

                foreach (GuildResourceChoice cohort in cohorts.Value.Items)
                {
                    await foreach (Result<ResourcePage<GuildResourceChoice>> offers in PagesAsync("offer", cohort.Id, null, cancellationToken).ConfigureAwait(false))
                    {
                        if (offers.IsFailure) { yield return offers; yield break; }

                        yield return new ResourcePage<GuildResourceChoice>(offers.Value.Items, "more");
                    }
                }
            }

            yield return new ResourcePage<GuildResourceChoice>([], null);

            yield break;
        }

        string? cursor = null;

        HashSet<string> seen = new(StringComparer.Ordinal);

        do
        {
            Result<ResourcePage<GuildResourceChoice>> page;

            switch (kind)
            {
                case "workspace":
                {
                    Result<WorkspaceInfo[]> result = await apiClient.GetWorkspacesAsync(cancellationToken).ConfigureAwait(false);

                    page = result.IsFailure ? result.Error : new ResourcePage<GuildResourceChoice>(
                        [.. result.Value.Where(static item => Guid.TryParseExact(item.Id, "D", out Guid id) && id != Guid.Empty)
                            .Select(static item => new GuildResourceChoice(Guid.Parse(item.Id), item.Name, item.Path, Path: item.Path))], null);

                    break;
                }

                case "campaign":
                {
                    int offset = cursor is null ? 0 : int.Parse(cursor, NumberStyles.None, CultureInfo.InvariantCulture);

                    Result<ListPageResult<CampaignDto>> result = await apiClient.GetCampaignsPageAsync(null, 100, offset, cancellationToken).ConfigureAwait(false);

                    page = result.IsFailure ? result.Error
                        : result.Value.HasMore && result.Value.NextOffset is null ? new Error("Api.PaginationNoProgress", "The Campaign catalog omitted its continuation.")
                        : new ResourcePage<GuildResourceChoice>([.. result.Value.Items.Select(static item => new GuildResourceChoice(item.Id, item.Name, item.Path))],
                            result.Value.HasMore ? result.Value.NextOffset!.Value.ToString(CultureInfo.InvariantCulture) : null);

                    break;
                }

                case "session":
                {
                    DateTimeOffset? before = cursor is null ? null : DateTimeOffset.Parse(cursor, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

                    Result<SessionQueryResult> result = await apiClient.QuerySessionsAsync(new SessionQueryRequest(Status: "all", Limit: 100, BeforeUpdatedAt: before), cancellationToken).ConfigureAwait(false);

                    page = result.IsFailure ? result.Error
                        : result.Value.HasMore && result.Value.NextBeforeUpdatedAt is null ? new Error("Api.PaginationNoProgress", "The Session catalog omitted its continuation.")
                        : new ResourcePage<GuildResourceChoice>([.. result.Value.Summaries.Select(static item => new GuildResourceChoice(item.Id,
                            string.IsNullOrWhiteSpace(item.Title) ? "(untitled)" : item.Title, item.Status))],
                            result.Value.HasMore ? result.Value.NextBeforeUpdatedAt!.Value.ToString("O", CultureInfo.InvariantCulture) : null);

                    break;
                }

                case "agent":
                    page = await Fetch<GuildAgentCardPageDto>(Query("api/guild/agents", cursor, ("source", source)),
                        static value => new([.. value.Items.Select(item => new GuildResourceChoice(item.AgentId, item.Name,
                            $"Card {item.CardId:D} revision {item.Revision}; {item.Source.Kind}", item))], value.NextCursor), cancellationToken).ConfigureAwait(false);

                    break;

                case "task":
                case "signet":
                    page = await Fetch<GuildQuestPageDto>(Query("api/guild/quests", cursor),
                        value => new([.. value.Items.SelectMany(item => kind switch
                        {
                            "signet" => new[] { new GuildResourceChoice(item.SignetId, item.SignetId.ToString("D"), "Signet for Quest " + item.Id.ToString("D")) },
                            _ => new[] { new GuildResourceChoice(item.Id, item.Name ?? item.Id.ToString("D"), item.State.ToString()) },
                        }).DistinctBy(item => item.Id)], value.NextCursor), cancellationToken).ConfigureAwait(false);

                    break;

                case "cohort":
                    page = await Fetch<CohortPageDto>(Query("api/guild/cohorts", cursor),
                        static value => new([.. value.Items.Select(item => new GuildResourceChoice(item.Id, item.Name, item.State.ToString()))], value.NextCursor), cancellationToken).ConfigureAwait(false);

                    break;

                case "member":
                    page = await Fetch<CohortMemberPageDto>(Query($"api/guild/cohorts/{scope:D}/members", cursor),
                        static value => new([.. value.Items.Select(item => new GuildResourceChoice(item.Id, item.Id.ToString("D"), item.State.ToString()))], value.NextCursor), cancellationToken).ConfigureAwait(false);

                    break;

                case "offer":
                    page = await Fetch<WorkOfferPageDto>(Query($"api/guild/cohorts/{scope:D}/offers", cursor),
                        static value => new([.. value.Items.Select(item => new GuildResourceChoice(item.Id, item.Id.ToString("D"), item.State.ToString()))], value.NextCursor), cancellationToken).ConfigureAwait(false);

                    break;

                case "bid":
                    page = await Fetch<WorkBidPageDto>(Query($"api/guild/offers/{scope:D}/bids", cursor),
                        static value => new([.. value.Items.Select(item => new GuildResourceChoice(item.Id, item.Id.ToString("D"), item.State.ToString()))], value.NextCursor), cancellationToken).ConfigureAwait(false);

                    break;

                case "note":
                    page = await Fetch<SharedNotePageDto>(Query($"api/guild/cohorts/{scope:D}/notes", cursor),
                        static value => new([.. value.Items.Select(item => new GuildResourceChoice(item.Id, item.Id.ToString("D"), item.Kind.ToString()))], value.NextCursor), cancellationToken).ConfigureAwait(false);

                    break;

                case "lease":
                    page = await Fetch<WorkspaceLeasePageDto>(Query("api/guild/workspace-leases", cursor),
                        static value => new([.. value.Items.Select(item => new GuildResourceChoice(item.Id, item.Id.ToString("D"), item.State.ToString()))], value.NextCursor), cancellationToken).ConfigureAwait(false);

                    break;

                default:
                    page = new ResourcePage<GuildResourceChoice>([], null);

                    break;
            }

            yield return page;

            if (page.IsFailure) yield break;

            cursor = page.Value.NextToken;

            if (cursor is not null && !seen.Add(cursor))
            {
                yield return new Error("Api.PaginationNoProgress", "The Guild catalog cursor did not advance.");

                yield break;
            }
        }

        while (cursor is not null);
    }

    private async IAsyncEnumerable<Result<ResourcePage<GuildResourceChoice>>> ContentPagesAsync(
        string prefix, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Result<GuildContentResolutionDto> result = await apiClient.PostGuildAsync("api/guild/content/resolve",
            new GuildResolveContentRequest(prefix), GuildJsonContext.Default.GuildResolveContentRequest,
            ArcanumJsonContext.Default.ApiResponseGuildContentResolutionDto, cancellationToken).ConfigureAwait(false);

        if (result.IsFailure)
        {
            yield return result.Error;

            yield break;
        }

        if (result.Value.Candidates.IsDefault || result.Value.Candidates.Length > 2)
        {
            yield return new Error("Api.InvalidResponse", "The content resolver returned an invalid metadata candidate set.");

            yield break;
        }

        yield return new ResourcePage<GuildResourceChoice>([.. result.Value.Candidates.Select(static reference =>
            new GuildResourceChoice(reference.ContentId, reference.ContentId.ToString("D"), $"{reference.Kind}; revision {reference.Revision}"))], null);
    }

    private async Task<Result<ResourcePage<GuildResourceChoice>>> Fetch<T>(string path,
        Func<T, ResourcePage<GuildResourceChoice>> project, CancellationToken cancellationToken)
    {
        JsonTypeInfo<ApiResponse<T>> metadata = (JsonTypeInfo<ApiResponse<T>>)ArcanumJsonContext.Default.GetTypeInfo(typeof(ApiResponse<T>))!;

        Result<T> result = await apiClient.GetGuildAsync(path, metadata, cancellationToken).ConfigureAwait(false);

        return result.IsFailure ? result.Error : project(result.Value);
    }

    private static string Query(string path, string? cursor, params (string Name, string? Value)[] filters) =>
        ArcanumApiClient.GuildQuery(path, [("cursor", cursor), ("pageSize", "100"), .. filters]);
}
