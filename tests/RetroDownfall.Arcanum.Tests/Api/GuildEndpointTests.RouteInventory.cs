using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api;

public sealed partial class GuildEndpointTests
{
    [Fact]
    public async Task Guild_operator_route_contract_is_registered_with_unique_names()
    {
        await using ArcanumWebApplicationFactory factory = new();

        using HttpClient client = factory.CreateAuthenticatedClient();

        _ = await client.GetAsync("/api/guild/status");

        RouteEndpoint[] routes = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();

        (string Method, string Path, string Name)[] required =
        [
            ("GET", "/api/guild/status", "GuildGetStatus"),
            ("GET", "/api/guild/agents", "GuildListAgents"),
            ("GET", "/api/guild/agents/{agentId:guid}", "GuildGetAgent"),
            ("POST", "/api/guild/agents/refresh", "GuildRefreshAgents"),
            ("POST", "/api/guild/agents/remote/resolve", "GuildResolveRemoteAgent"),
            ("POST", "/api/guild/quests", "GuildIssueRoot"),
            ("POST", "/api/guild/quests/{questId:guid}/children", "GuildIssueChild"),
            ("GET", "/api/guild/quests", "GuildListQuests"),
            ("GET", "/api/guild/quests/{questId:guid}", "GuildGetQuest"),
            ("GET", "/api/guild/quests/{questId:guid}/tree", "GuildGetQuestTree"),
            ("POST", "/api/guild/quests/{questId:guid}/messages", "GuildSendOperatorMessage"),
            ("POST", "/api/guild/quests/{questId:guid}/cancel", "GuildCancelQuest"),
            ("GET", "/api/guild/quests/{questId:guid}/spend", "GuildGetTreeSpend"),
            ("POST", "/api/guild/content/resolve", "GuildResolveContent"),
            ("GET", "/api/guild/content/{contentId:guid}", "GuildGetContent"),
            ("POST", "/api/guild/signets/{signetId:guid}/revoke", "GuildRevokeSignet"),
            ("GET", "/api/guild/quests/{questId:guid}/events", "GuildWatchTree"),
            ("GET", "/api/guild/cohorts/{cohortId:guid}/events", "GuildWatchCohort"),
            ("POST", "/api/guild/cohorts", "GuildCreateCohort"),
            ("GET", "/api/guild/cohorts", "GuildListCohorts"),
            ("GET", "/api/guild/cohorts/{cohortId:guid}", "GuildGetCohort"),
            ("POST", "/api/guild/cohorts/{cohortId:guid}/members", "GuildAdmitCohortMember"),
            ("GET", "/api/guild/cohorts/{cohortId:guid}/members", "GuildListCohortMembers"),
            ("POST", "/api/guild/cohorts/{cohortId:guid}/members/{memberId:guid}/remove", "GuildRemoveCohortMember"),
            ("POST", "/api/guild/cohorts/{cohortId:guid}/close", "GuildCloseCohort"),
            ("POST", "/api/guild/cohorts/{cohortId:guid}/cancel", "GuildCancelCohort"),
            ("GET", "/api/guild/cohorts/{cohortId:guid}/contributions", "GuildListCohortContributions"),
            ("GET", "/api/guild/cohorts/{cohortId:guid}/convergence", "GuildGetCohortConvergence"),
            ("POST", "/api/guild/cohorts/{cohortId:guid}/offers", "GuildPostWorkOffer"),
            ("GET", "/api/guild/cohorts/{cohortId:guid}/offers", "GuildListWorkOffers"),
            ("GET", "/api/guild/offers/{offerId:guid}", "GuildGetWorkOffer"),
            ("POST", "/api/guild/offers/{offerId:guid}/withdraw", "GuildWithdrawWorkOffer"),
            ("GET", "/api/guild/offers/{offerId:guid}/bids", "GuildListWorkBids"),
            ("POST", "/api/guild/offers/{offerId:guid}/award", "GuildAwardWork"),
            ("POST", "/api/guild/cohorts/{cohortId:guid}/notes", "GuildAppendSharedNote"),
            ("GET", "/api/guild/cohorts/{cohortId:guid}/notes", "GuildReadSharedNotes"),
            ("POST", "/api/guild/workspace-leases", "GuildAcquireWorkspaceLease"),
            ("GET", "/api/guild/workspace-leases", "GuildListWorkspaceLeases"),
            ("POST", "/api/guild/workspace-leases/{leaseId:guid}/renew", "GuildRenewWorkspaceLease"),
            ("POST", "/api/guild/workspace-leases/{leaseId:guid}/release", "GuildReleaseWorkspaceLease"),
            ("GET", "/api/guild/cohorts/{cohortId:guid}/members/{memberId:guid}/quests", "GuildListCohortMemberQuests"),
        ];

        string[] missing = required.Where(expected => !routes.Any(route => route.RoutePattern.RawText == expected.Path
            && route.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == expected.Name
            && route.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods.Contains(expected.Method) == true))
            .Select(expected => $"{expected.Method} {expected.Path} ({expected.Name})").ToArray();

        Assert.True(missing.Length == 0, "Missing Guild routes: " + string.Join(", ", missing));
    }
}
