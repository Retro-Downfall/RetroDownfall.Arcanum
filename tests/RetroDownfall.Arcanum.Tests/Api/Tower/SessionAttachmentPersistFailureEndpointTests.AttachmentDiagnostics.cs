using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

public sealed partial class SessionAttachmentPersistFailureEndpointTests
{
    [Theory]
    [InlineData("hash")]
    [InlineData("provenance")]
    [InlineData("limit")]
    [InlineData("private-fault")]
    [InlineData("valid")]
    public async Task AttachmentDiagnostics_Refresh_route_distinguishes_integrity_and_limits(string mode)
    {
        RefreshDiagnosticResolver resolver = new(mode);
        bool refreshing = false;
        await using ArcanumWebApplicationFactory factory = new()
        {
            ServiceOverrides = services =>
            {
                services.RemoveAll<IAttachmentSourceResolver>();
                services.AddSingleton<IAttachmentSourceResolver>(resolver);
                services.RemoveAll<ISessionAttachmentStore>();
                services.AddScoped<ISessionAttachmentStore>(provider =>
                {
                    SessionAttachmentStore store = ActivatorUtilities.CreateInstance<SessionAttachmentStore>(provider);
                    store.AfterBytesCommittedBeforeDbForTesting = _ => refreshing && mode is "limit" or "private-fault"
                        ? Task.FromException(mode == "limit" ? new AttachmentLimitExceededException("private-path-canary") : new InvalidOperationException("private-path-canary"))
                        : Task.CompletedTask;
                    return store;
                });
            },
        };
        using HttpClient client = factory.CreateAuthenticatedClient();
        Guid sessionId = await CreateSessionAsync(client);
        string fileName = "refresh-notes.txt";
        await File.WriteAllTextAsync(Path.Combine(factory.TempHome, fileName), "original");
        using HttpResponseMessage created = await client.PostAsync($"/api/sessions/{sessionId}/attachments/reference", new StringContent("{\"workspacePath\":\"" + fileName + "\"}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using JsonDocument document = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Guid attachmentId = document.RootElement.GetProperty("data").GetProperty("id").GetGuid();
        refreshing = true;
        using HttpResponseMessage response = await client.PostAsync($"/api/sessions/{sessionId}/attachments/{attachmentId}/refresh", null);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Equal(mode == "valid" ? HttpStatusCode.OK : mode == "limit" ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest, response.StatusCode);
        if (mode != "valid") Assert.Contains(mode == "limit" ? ErrorCodes.Attachment.LimitExceeded : ErrorCodes.Attachment.SourceUnavailable, body, StringComparison.Ordinal);
        Assert.DoesNotContain("private-path-canary", body, StringComparison.Ordinal);
        Assert.Equal(1, resolver.CurrentReads);
    }

    private sealed class RefreshDiagnosticResolver(string mode) : IAttachmentSourceResolver
    {
        public int CurrentReads { get; private set; }
        public Task<AttachmentSourceResolution> ResolveForPersistenceAsync(AttachmentSourceClaim claim, ReadOnlyMemory<byte> snapshotBytes, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<AttachmentSourceMetadata> RevalidateAsync(AttachmentSourceMetadata source, CancellationToken cancellationToken = default) => Task.FromResult(source);
        public async Task<AttachmentSourceResolution> ResolveForReferenceAsync(AttachmentSourceClaim claim, long maxBytes, AttachmentSourcePathAuthorizer authorizeCanonicalPath, CancellationToken cancellationToken = default)
        {
            Assert.True(await authorizeCanonicalPath(new AttachmentSourceRead(claim.AbsolutePath, claim.AbsolutePath), cancellationToken));
            byte[] bytes = "original"u8.ToArray();
            return new(new(AttachmentSourceKind.WorkspaceFile, "workspace-identity", "refresh-notes.txt", claim.AbsolutePath, Convert.ToHexString(SHA256.HashData(bytes)), "file-identity", DateTimeOffset.UtcNow, bytes.Length, AttachmentSourceStatus.Refreshable, null), bytes, "text/plain");
        }

        public async Task<AttachmentSourceResolution> ResolveCurrentAsync(AttachmentSourceMetadata source, string expectedSnapshotSha256, long maxBytes, AttachmentSourcePathAuthorizer authorizeRead, CancellationToken cancellationToken = default)
        {
            CurrentReads++;
            Assert.True(await authorizeRead(new AttachmentSourceRead(source.LastKnownCanonicalPath!, source.LastKnownCanonicalPath!), cancellationToken));
            byte[] bytes = "changed document"u8.ToArray();
            return new(source with { LastObservedContentSha256 = mode == "hash" ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(bytes)), LastObservedFileIdentity = mode == "provenance" ? null : source.LastObservedFileIdentity, LastObservedByteLength = bytes.Length, Status = AttachmentSourceStatus.PriorVersion }, bytes, "text/plain");
        }
    }
}
