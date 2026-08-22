using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Security;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Chronosync;
using RetroDownfall.Arcanum.Core.Pattern.Entities;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api;

[Collection("ApiHost")]
public sealed class ChronosyncEndpointTests(ArcanumWebApplicationFactory factory)
{

    [SkippableFact]
    public async Task PostChronosync_RejectsAnInvalidSnapshotWithoutPersistingIt()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string root = UniqueCanonicalRoot();

        PatternSnapshot snapshot = new(
            DomainType.SoftwareEngineering,
            root,
            ["Project: src/App.csproj", "Document: SRC/app.CSPROJ"]);

        HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await SendAsync(client, snapshot);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        ApiResponse<ChronosyncReport>? envelope = JsonSerializer.Deserialize(
            await response.Content.ReadAsByteArrayAsync(),
            ArcanumJsonContext.Default.ApiResponseChronosyncReport);

        Assert.False(envelope?.IsSuccess);

        Assert.Equal(ErrorCodes.Perception.InvalidSnapshot, envelope?.Error?.Code);

        Assert.Equal(0, await CountWorkspaceContextsAsync(root));

    }

    [SkippableFact]
    public async Task PostChronosync_WithTheSameKeyAndSnapshot_ReplaysWithoutPersistingTwice()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string root = UniqueCanonicalRoot();

        string key = $"chronosync-test-{Guid.NewGuid():N}";

        PatternSnapshot snapshot = new(DomainType.Research, root, ["Document: README.md"]);

        HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage firstResponse = await SendAsync(client, snapshot, key);

        byte[] firstBody = await firstResponse.Content.ReadAsByteArrayAsync();

        using HttpResponseMessage secondResponse = await SendAsync(client, snapshot, key);

        byte[] secondBody = await secondResponse.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        Assert.Equal(firstBody, secondBody);

        Assert.Equal(1, await CountWorkspaceContextsAsync(root));

    }

    [SkippableFact]
    public async Task PostChronosync_WithTheSameKeyAndDifferentSnapshot_ReturnsConflict()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        string root = UniqueCanonicalRoot();

        string key = $"chronosync-test-{Guid.NewGuid():N}";

        HttpClient client = factory.CreateAuthenticatedClient();

        using HttpResponseMessage firstResponse = await SendAsync(
            client,
            new PatternSnapshot(DomainType.Research, root, ["Document: README.md"]),
            key);

        using HttpResponseMessage secondResponse = await SendAsync(
            client,
            new PatternSnapshot(DomainType.Research, root, ["Document: DESIGN.md"]),
            key);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);

        Assert.Equal(HttpStatusCode.Conflict, secondResponse.StatusCode);

        ApiResponse<ChronosyncReport>? envelope = JsonSerializer.Deserialize(
            await secondResponse.Content.ReadAsByteArrayAsync(),
            ArcanumJsonContext.Default.ApiResponseChronosyncReport);

        Assert.False(envelope?.IsSuccess);

        Assert.Equal(ErrorCodes.Security.IdempotencyConflict, envelope?.Error?.Code);

        Assert.Equal(1, await CountWorkspaceContextsAsync(root));

    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        PatternSnapshot snapshot,
        string? idempotencyKey = null)
    {

        string payload = JsonSerializer.Serialize(
            snapshot,
            ArcanumJsonContext.Default.PatternSnapshot);

        using HttpRequestMessage request = new(HttpMethod.Post, "/api/perception/chronosync")
        {

            Content = new StringContent(payload, Encoding.UTF8, "application/json"),

        };

        if (idempotencyKey is not null)
        {

            request.Headers.Add(ArcanumApiHeaders.IdempotencyKey, idempotencyKey);

        }

        return await client.SendAsync(request);

    }

    private async Task<int> CountWorkspaceContextsAsync(string root)
    {

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();

        ArcanumDbContext context = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        return await context.WorkspaceContexts
            .AsNoTracking()
            .CountAsync(item => item.WorkspacePath == root);

    }

    private static string UniqueCanonicalRoot() =>
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(
                Path.Combine(Path.GetTempPath(), $"arcanum-chronosync-{Guid.NewGuid():N}")));

}
