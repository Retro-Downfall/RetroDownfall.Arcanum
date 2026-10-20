using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Annals;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Intelligence.MemoryDelegation;
using RetroDownfall.Arcanum.Core.ProvingGrounds.MemoryEvaluation;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data.Covenant;
using RetroDownfall.Arcanum.Secrets.Security;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class SessionCreationBindingTests
{
    [SkippableFact]
    public async Task Generated_title_lane_keeps_its_null_guard_and_200_character_limit()
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        using var client = factory.CreateAuthenticatedClient();

        using var scope = factory.Services.CreateScope();

        var owner = scope.ServiceProvider.GetRequiredService<ISessionTurnBeginStore>();

        var created = await owner.CreateBoundSessionAsync(CanonicalCampaignContext.GlobalOnly, "  " + new string('T', 256) + "  ", default);

        Assert.True(created.IsSuccess);

        var connection = await scope.ServiceProvider.GetRequiredService<ICovenantConnectionSource>().GetOpenCoreConnectionAsync(default);

        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT Title FROM Sessions WHERE Id=$id";

        command.Parameters.AddWithValue("$id", created.Value.ToString("D").ToUpperInvariant());

        Assert.Equal(new string('T', 200), await command.ExecuteScalarAsync());

        await Assert.ThrowsAsync<ArgumentNullException>(() => owner.CreateBoundSessionAsync(CanonicalCampaignContext.GlobalOnly, null!, default).AsTask());
    }

    [Fact]
    public async Task Legacy_begin_implementation_refuses_named_creation_instead_of_falling_back()
    {
        ISessionTurnBeginStore owner = new FakeSessionTurnBeginStore();

        var result = await owner.CreateNamedSessionAsync(CanonicalCampaignContext.GlobalOnly, "named", default);

        Assert.True(result.IsFailure);

        Assert.Equal(ErrorCodes.Grimoire.WriteFailed, result.Error.Code);
    }

    [SkippableTheory]
    [InlineData(false, "null")]
    [InlineData(true, "null")]
    [InlineData(false, "whitespace")]
    [InlineData(true, "whitespace")]
    [InlineData(false, "short")]
    [InlineData(true, "short")]
    [InlineData(false, "long")]
    [InlineData(true, "long")]
    public async Task Public_creation_preserves_operator_title_and_immediately_supports_canonical_turns(bool campaignBound, string titleKind)
    {
        await using var factory = MemoryErasureRouteDriver.Host(new InMemoryOsCredentialStore(), covenant: true,
            configure: settings => settings.Features.Annals = true);

        using var client = factory.CreateAuthenticatedClient();

        Guid? campaignId = null;

        if (campaignBound)
        {
            string path = Path.Combine(factory.TempHome, "bound-campaign");

            Directory.CreateDirectory(path);

            using var registration = await client.PostAsync("/api/campaigns", JsonContent.Create(
                new RegisterCampaignRequest("campaign", path, WorkspaceType.Campaign, null), ArcanumJsonContext.Default.RegisterCampaignRequest));

            Assert.Equal(HttpStatusCode.Created, registration.StatusCode);

            campaignId = JsonSerializer.Deserialize(await registration.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseCampaignDto)!.Data!.Id;
        }

        string? supplied = titleKind switch { "null" => null, "whitespace" => "  ", "short" => "  named  ", _ => "  " + new string('T', 256) + "  " };

        string? expected = titleKind switch { "null" or "whitespace" => null, "short" => "named", _ => new string('T', 256) };

        using var creation = await client.PostAsync("/api/sessions", JsonContent.Create(new CreateSessionRequest(campaignId, supplied), ArcanumJsonContext.Default.CreateSessionRequest));

        Assert.Equal(HttpStatusCode.Created, creation.StatusCode);

        var session = JsonSerializer.Deserialize(await creation.Content.ReadAsStringAsync(), ArcanumJsonContext.Default.ApiResponseSessionDetailDto)!.Data!;

        Assert.Equal(expected, session.Title);

        Assert.Equal(campaignId, session.CampaignId);

        Assert.Equal($"/api/sessions/{session.Id:D}", creation.Headers.Location?.ToString());

        using var scope = factory.Services.CreateScope();

        var canonical = await scope.ServiceProvider.GetRequiredService<ICanonicalCampaignContextResolver>()
            .ResolveAsync(new(session.Id, null, null), default);

        Assert.True(canonical.IsSuccess, canonical.IsFailure ? canonical.Error.ToString() : "");

        Assert.Equal(campaignId, canonical.Value.CampaignId);

        using var ping = await client.PostAsync("/api/intelligence/ping", JsonContent.Create(new PingRequest("parent", SessionId: session.Id, SkipSpellRouting: true), ArcanumJsonContext.Default.PingRequest));

        Assert.True(ping.StatusCode == HttpStatusCode.OK, await ping.Content.ReadAsStringAsync());

        string memory = await MemoryErasureRouteDriver.InsertSagaAsync(factory, "selected fact");

        var connection = await scope.ServiceProvider.GetRequiredService<ICovenantConnectionSource>().GetOpenCoreConnectionAsync(default);

        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT v.VersionId,v.ContentHash FROM annal_claims c JOIN annal_heads h ON h.ClaimId=c.ClaimId JOIN annal_versions v ON v.VersionId=h.CurrentVersionId WHERE c.SubjectStoreCode=1 AND c.SubjectId=$id";

        command.Parameters.AddWithValue("$id", memory);

        await using var row = await command.ExecuteReaderAsync();

        Assert.True(await row.ReadAsync());

        MemoryDelegationGrant grant = new(new(campaignBound ? MemoryEvaluationScopeKind.Campaign : MemoryEvaluationScopeKind.Global, campaignId),
            new([], [new(memory, row.GetString(0), AnnalContentHashFormat.LegacyStoreDigest, new((byte[])row[1]))], []));

        await row.DisposeAsync();

        using var delegated = await client.PostAsync("/api/intelligence/memory-delegation", JsonContent.Create(
            new MemoryDelegationRequest(new("parent", SessionId: session.Id, SkipSpellRouting: true, DisableMcpTools: true), grant), ArcanumJsonContext.Default.MemoryDelegationRequest));

        Assert.True(delegated.StatusCode == HttpStatusCode.OK, await delegated.Content.ReadAsStringAsync());
    }

    [SkippableFact]
    public async Task Missing_campaign_creates_neither_session_nor_immutable_binding()
    {
        await using var factory = MemoryErasureRouteDriver.CreateFactory(new InMemoryOsCredentialStore());

        using var client = factory.CreateAuthenticatedClient();

        using var scope = factory.Services.CreateScope();

        var connection = await scope.ServiceProvider.GetRequiredService<ICovenantConnectionSource>().GetOpenCoreConnectionAsync(default);

        await using var command = connection.CreateCommand();

        command.CommandText = "SELECT (SELECT count(*) FROM Sessions) + (SELECT count(*) FROM session_campaign_bindings)";

        long before = (long)(await command.ExecuteScalarAsync())!;

        using var response = await client.PostAsync("/api/sessions", JsonContent.Create(new CreateSessionRequest(Guid.NewGuid(), "title"), ArcanumJsonContext.Default.CreateSessionRequest));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        Assert.Equal(before, (long)(await command.ExecuteScalarAsync())!);
    }
}
