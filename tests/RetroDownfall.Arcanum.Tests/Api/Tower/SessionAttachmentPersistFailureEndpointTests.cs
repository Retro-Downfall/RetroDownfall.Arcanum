using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

/// <summary>
/// The session attachment routes report <c>Attachment.LimitExceeded</c> only for the store's typed
/// limit refusal. Any other <see cref="InvalidOperationException"/> out of persistence is not a limit and
/// must not be told to the caller as one.
/// </summary>
[Collection("ApiHost")]
public sealed class SessionAttachmentPersistFailureEndpointTests
{
    [SkippableFact]
    public async Task PostAttachments_Multipart_with_a_persistence_failure_is_not_reported_as_LimitExceeded()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateFactoryFailingPersistenceWith(
            () => new InvalidOperationException("simulated insert failure"));

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = await CreateSessionAsync(client);

        HttpResponseMessage response = await PostSnapshotAsync(client, sessionId);

        string body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(ErrorCodes.Attachment.LimitExceeded, body, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    [SkippableFact]
    public async Task PostAttachments_Multipart_over_the_storage_limit_answers_409_LimitExceeded()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = CreateFactoryFailingPersistenceWith(
            () => new AttachmentLimitExceededException("Physical session-attachment storage boundary reached."));

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = await CreateSessionAsync(client);

        HttpResponseMessage response = await PostSnapshotAsync(client, sessionId);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        Assert.Equal(ErrorCodes.Attachment.LimitExceeded, await ReadErrorCodeAsync(response));
    }

    [SkippableFact]
    public async Task PostAttachmentReference_whose_verified_bytes_do_not_match_the_source_hash_is_not_reported_as_LimitExceeded()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {
                services.RemoveAll<IAttachmentSourceResolver>();

                services.AddSingleton<IAttachmentSourceResolver>(new MismatchedHashResolver());
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = await CreateSessionAsync(client);

        string fileName = $"hash-mismatch-{Guid.NewGuid():N}.txt";

        await File.WriteAllTextAsync(Path.Combine(factory.TempHome, fileName), "current bytes");

        HttpResponseMessage response = await client.PostAsync(
            $"/api/sessions/{sessionId:D}/attachments/reference",
            new StringContent("{\"workspacePath\":\"" + fileName + "\"}", Encoding.UTF8, "application/json"));

        string body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain(ErrorCodes.Attachment.LimitExceeded, body, StringComparison.Ordinal);

        // A resolution whose bytes disagree with its own recorded hash is a server fault (API.md), so any
        // earlier 4xx refusal of the request would be the wrong answer, not just a non-201.
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }

    /// <summary>
    /// A host whose session attachment store throws the supplied exception from inside the persistence
    /// step, where a limit refusal or a database failure would surface.
    /// </summary>
    private static ArcanumWebApplicationFactory CreateFactoryFailingPersistenceWith(Func<Exception> failure) =>
        new()
        {
            ServiceOverrides = services =>
            {
                services.RemoveAll<ISessionAttachmentStore>();

                services.AddScoped<ISessionAttachmentStore>(provider =>
                {
                    SessionAttachmentStore store = ActivatorUtilities.CreateInstance<SessionAttachmentStore>(provider);

                    store.AfterBytesCommittedBeforeDbForTesting = _ => throw failure();

                    return store;
                });
            },
        };

    private static async Task<Guid> CreateSessionAsync(HttpClient client)
    {
        HttpResponseMessage response = await client.PostAsync(
            "/api/sessions",
            new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        ApiResponse<SessionDetailDto>? body = JsonSerializer.Deserialize(
            await response.Content.ReadAsStringAsync(),
            ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

        return body!.Data!.Id;
    }

    private static async Task<HttpResponseMessage> PostSnapshotAsync(HttpClient client, Guid sessionId)
    {
        using MultipartFormDataContent form = new();

        using ByteArrayContent file = new(Encoding.UTF8.GetBytes("snapshot bytes"));

        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        form.Add(file, "file", "snapshot.txt");

        return await client.PostAsync($"/api/sessions/{sessionId:D}/attachments", form);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.TryGetProperty("error", out JsonElement error)
            && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("code", out JsonElement code)
                ? code.GetString()
                : null;
    }

    /// <summary>Resolves a complete, refreshable workspace source whose recorded hash is not the bytes' hash.</summary>
    private sealed class MismatchedHashResolver : IAttachmentSourceResolver
    {
        public Task<AttachmentSourceResolution> ResolveForPersistenceAsync(
            AttachmentSourceClaim claim,
            ReadOnlyMemory<byte> snapshotBytes,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AttachmentSourceMetadata> RevalidateAsync(
            AttachmentSourceMetadata source,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AttachmentSourceResolution> ResolveForReferenceAsync(
            AttachmentSourceClaim claim,
            long maxBytes,
            AttachmentSourcePathAuthorizer authorizeCanonicalPath,
            CancellationToken cancellationToken = default)
        {
            byte[] bytes = Encoding.UTF8.GetBytes("current bytes");

            AttachmentSourceMetadata metadata = new(
                AttachmentSourceKind.WorkspaceFile,
                "workspace-identity",
                "relative.txt",
                claim.AbsolutePath,
                new string('0', 64),
                "file-identity",
                DateTimeOffset.UtcNow,
                bytes.Length,
                AttachmentSourceStatus.Refreshable,
                null);

            return Task.FromResult(new AttachmentSourceResolution(metadata, bytes, "text/plain"));
        }
    }
}
