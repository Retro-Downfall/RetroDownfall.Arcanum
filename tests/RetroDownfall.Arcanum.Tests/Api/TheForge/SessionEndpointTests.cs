using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Intelligence.Models;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.TheForge;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Workspaces;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.TheForge;

[Collection("ApiHost")]
public sealed class SessionEndpointTests
{

    private readonly ArcanumWebApplicationFactory _factory;

    public SessionEndpointTests(ArcanumWebApplicationFactory factory)
    {

        _factory = factory;

    }

    [SkippableFact]
    public async Task GetEntries_CountOnly_ReturnsEntryCount()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(3);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/sessions/{sessionId}/entries?countOnly=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<SessionEntryCountDto>? body = await response.Content
            .ReadFromJsonAsync<ApiResponse<SessionEntryCountDto>>(ArcanumJsonContext.Default.ApiResponseSessionEntryCountDto);

        Assert.NotNull(body);
        Assert.NotNull(body.Data);
        Assert.True(body.IsSuccess);
        Assert.Equal(3, body.Data.Count);

    }

    [SkippableFact]
    public async Task GetEntries_CountOnly_UnknownSession_Returns404()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid unknownId = Guid.NewGuid();
        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/sessions/{unknownId}/entries?countOnly=true");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

    }

    [SkippableFact]
    public async Task GetEntries_WithoutCountOnly_ReturnsEntries()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(2);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/sessions/{sessionId}/entries");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<EntryDto[]>? body = await response.Content
            .ReadFromJsonAsync<ApiResponse<EntryDto[]>>(ArcanumJsonContext.Default.ApiResponseEntryDtoArray);

        Assert.NotNull(body);
        Assert.NotNull(body.Data);
        Assert.True(body.IsSuccess);
        Assert.Equal(2, body.Data.Length);

    }

    [SkippableFact]

    public async Task GetStream_WithEntryCursor_ReplaysOnlyLaterEntries()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(3);

        Guid[] entryIds = await GetEntryIdsAsync(sessionId);

        using HttpClient client = _factory.CreateAuthenticatedClient();

        using HttpResponseMessage response = await client.GetAsync(

            $"/api/sessions/{sessionId:D}/stream?since={entryIds[0]:D}",

            HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        await using Stream stream = await response.Content.ReadAsStreamAsync();

        using StreamReader reader = new(stream, Encoding.UTF8);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));

        List<Guid> replayedIds = [];

        while (true)

        {

            string? line = await reader.ReadLineAsync(timeout.Token);

            Assert.NotNull(line);

            if (string.Equals(line, "data: {\"type\":\"live\"}", StringComparison.Ordinal))

            {

                break;

            }

            if (!line.StartsWith("data: ", StringComparison.Ordinal))

            {

                continue;

            }

            EntryDto? entry = JsonSerializer.Deserialize(

                line["data: ".Length..],

                ArcanumJsonContext.Default.EntryDto);

            Assert.NotNull(entry);

            replayedIds.Add(entry.Id);

        }

        Assert.Equal(entryIds[1..], replayedIds);

    }

    [SkippableFact]

    public async Task GetStream_WithMissingOrForeignEntryCursor_Returns404WithoutSseHeaders()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(1);

        Guid foreignSessionId = await CreateSessionWithEntriesAsync(1);

        Guid foreignEntryId = Assert.Single(await GetEntryIdsAsync(foreignSessionId));

        Guid missingEntryId = Guid.NewGuid();

        using HttpClient client = _factory.CreateAuthenticatedClient();

        foreach (Guid cursor in new[] { missingEntryId, foreignEntryId })

        {

            using HttpResponseMessage response = await client.GetAsync(

                $"/api/sessions/{sessionId:D}/stream?since={cursor:D}");

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

            Assert.False(response.Headers.Contains("X-Accel-Buffering"));

            Assert.False(response.Headers.CacheControl?.NoCache ?? false);

            ApiResponse<EntryDto>? body = await response.Content

                .ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseEntryDto);

            Assert.NotNull(body);

            Assert.False(body.IsSuccess);

            Assert.Equal(ErrorCodes.Session.EntryNotFound, body.Error?.Code);

        }

    }

    [SkippableFact]
    public async Task GetStream_ReplayFailure_ReleasesTheSessionEventHubSubscription()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new();

        factory.ServiceOverrides = services =>
        {
            services.RemoveAll<ISessionRepository>();

            services.AddScoped<ISessionRepository, ReplayFailingSessionRepository>();
        };

        using HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = ReplayFailingSessionRepository.SessionId;

        try
        {

            using HttpResponseMessage response = await client.GetAsync(
                $"/api/sessions/{sessionId:D}/stream",
                HttpCompletionOption.ResponseContentRead);

            Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);

        }
        catch (Exception)
        {
            // The replay fault may surface as a transport failure rather than a status code; either
            // way the endpoint has finished and the pump subscription must be gone.
        }

        SessionEventHub hub = factory.Services.GetRequiredService<SessionEventHub>();

        for (int attempt = 0; attempt < 100 && hub.GetSubscriberCount(sessionId) > 0; attempt++)
        {

            await Task.Delay(50);

        }

        Assert.Equal(0, hub.GetSubscriberCount(sessionId));

    }

    [SkippableFact]
    public async Task GetAttachments_UnknownSession_Returns404()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid unknownId = Guid.NewGuid();

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/sessions/{unknownId}/attachments");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        ApiResponse<SessionAttachmentDto[]>? body = await response.Content
            .ReadFromJsonAsync<ApiResponse<SessionAttachmentDto[]>>(
                ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray);

        Assert.NotNull(body);
        Assert.False(body.IsSuccess);
        Assert.Equal(ErrorCodes.Session.NotFound, body.Error?.Code);

    }

    [SkippableFact]
    public async Task GetAttachments_ReturnsBoundRowsOnly()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(0);

        SessionAttachmentRecord bound;

        using (IServiceScope scope = _factory.Services.CreateScope())
        {

            ISessionAttachmentStore store = scope.ServiceProvider.GetRequiredService<ISessionAttachmentStore>();

            bound = await store.PersistNewAsync(
                sessionId,
                pendingTurnId: null,
                entryId: null,
                logicalNameHint: "notes.txt",
                originalFileName: "notes.txt",
                Encoding.UTF8.GetBytes("bound-bytes"),
                mimeType: "text/plain",
                SessionAttachmentKind.Text);

            _ = await store.PersistNewAsync(
                sessionId: null,
                pendingTurnId: "turn-" + Guid.NewGuid().ToString("N"),
                entryId: null,
                logicalNameHint: "pending.txt",
                originalFileName: "pending.txt",
                Encoding.UTF8.GetBytes("pending-bytes"),
                mimeType: "text/plain",
                SessionAttachmentKind.Text);

        }

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/sessions/{sessionId}/attachments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<SessionAttachmentDto[]>? body = await response.Content
            .ReadFromJsonAsync<ApiResponse<SessionAttachmentDto[]>>(
                ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray);

        Assert.NotNull(body);
        Assert.True(body.IsSuccess);
        Assert.NotNull(body.Data);
        Assert.Single(body.Data);

        SessionAttachmentDto dto = body.Data[0];

        Assert.Equal(bound.Id, dto.Id);
        Assert.Equal(bound.LogicalKey, dto.LogicalKey);
        Assert.Equal(bound.OriginalFileName, dto.OriginalFileName);
        Assert.Equal(bound.Version, dto.Version);
        Assert.Equal(bound.RelativePath, dto.RelativePath);
        Assert.Equal(bound.MimeType, dto.MimeType);
        Assert.Equal(bound.ByteLength, dto.ByteLength);
        Assert.Equal(SessionAttachmentKind.Text, dto.Kind);
        Assert.Equal(bound.ContentSha256, dto.ContentSha256);

    }

    [SkippableFact]
    public async Task GetAttachments_EmptySession_ReturnsEmptyArray()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(0);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.GetAsync($"/api/sessions/{sessionId}/attachments");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        ApiResponse<SessionAttachmentDto[]>? body = await response.Content
            .ReadFromJsonAsync<ApiResponse<SessionAttachmentDto[]>>(
                ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray);

        Assert.NotNull(body);
        Assert.True(body.IsSuccess);
        Assert.NotNull(body.Data);
        Assert.Empty(body.Data);

    }

    [SkippableFact]

    public async Task Tracked_attachment_get_detects_drift_and_refresh_endpoint_confirms_live_version()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(0);

        string sourcePath = Path.Combine(

            _factory.TempHome,

            "issue16-" + Guid.NewGuid().ToString("N") + ".txt");

        try

        {

            byte[] before = Encoding.UTF8.GetBytes("before");

            await File.WriteAllBytesAsync(sourcePath, before);

            SessionAttachmentRecord original;

            using (IServiceScope scope = _factory.Services.CreateScope())

            {

                ISessionAttachmentStore store = scope.ServiceProvider

                    .GetRequiredService<ISessionAttachmentStore>();

                original = await store.PersistNewFromSourceAsync(

                    sessionId,

                    pendingTurnId: null,

                    entryId: null,

                    logicalNameHint: "notes",

                    originalFileName: "notes.txt",

                    before,

                    mimeType: "text/plain",

                    SessionAttachmentKind.Text,

                    new AttachmentSourceClaim(sourcePath));

            }

            await File.WriteAllBytesAsync(sourcePath, Encoding.UTF8.GetBytes("after"));

            HttpClient client = _factory.CreateAuthenticatedClient();

            ApiResponse<SessionAttachmentDto[]>? stale = await client

                .GetFromJsonAsync(

                    $"/api/sessions/{sessionId:D}/attachments",

                    ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray);

            SessionAttachmentDto staleRow = Assert.Single(stale!.Data!);

            Assert.Equal(AttachmentSourceStatus.PriorVersion, staleRow.SourceStatus);

            Assert.False(staleRow.IsRefreshable);

            HttpResponseMessage response = await client.PostAsync(

                $"/api/sessions/{sessionId:D}/attachments/{original.Id:D}/refresh",

                content: null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            ApiResponse<AttachmentRefreshEvent>? refreshed = await response.Content

                .ReadFromJsonAsync(

                    ArcanumJsonContext.Default.ApiResponseAttachmentRefreshEvent);

            Assert.NotNull(refreshed?.Data);

            Assert.True(refreshed.IsSuccess);

            Assert.True(refreshed.Data.NewVersionCreated);

            Assert.Equal(2, refreshed.Data.Version);

            Assert.Equal("notes", refreshed.Data.LogicalKey);

            ApiResponse<SessionAttachmentDto[]>? live = await client

                .GetFromJsonAsync(

                    $"/api/sessions/{sessionId:D}/attachments",

                    ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray);

            SessionAttachmentDto latest = live!.Data!

                .OrderByDescending(static row => row.Version)

                .First();

            Assert.Equal(AttachmentSourceStatus.Refreshable, latest.SourceStatus);

            Assert.True(latest.IsRefreshable);

            Assert.Equal(refreshed.Data.ContentSha256, latest.ContentSha256);

        }
        finally

        {

            if (File.Exists(sourcePath))

            {

                File.Delete(sourcePath);

            }

        }

    }

    [SkippableFact]

    public async Task PostAttachments_Multipart_CreatesBoundSnapshotOnlyRow()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(0);

        byte[] expectedBytes = Encoding.UTF8.GetBytes("standalone snapshot upload");

        HttpClient client = _factory.CreateAuthenticatedClient();

        using MultipartFormDataContent form = new();

        using ByteArrayContent fileContent = new(expectedBytes);

        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");

        form.Add(fileContent, "file", "snapshot-notes.txt");

        HttpResponseMessage response = await client.PostAsync(

            $"/api/sessions/{sessionId:D}/attachments",

            form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        ApiResponse<SessionAttachmentDto>? payload = await response.Content

            .ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseSessionAttachmentDto);

        Assert.NotNull(payload);

        Assert.True(payload.IsSuccess);

        Assert.Equal(sessionId, payload.Data!.SessionId);

        SessionAttachmentRecord row = Assert.Single(await GetBoundAttachmentsAsync(sessionId));

        Assert.Equal(SessionAttachmentState.Bound, row.State);

        Assert.Equal("snapshot-notes.txt", row.OriginalFileName);

        Assert.Equal("text/plain", row.MimeType);

        AttachmentSourceMetadata source = row.Source ?? AttachmentSourceMetadata.SnapshotOnly;

        Assert.Equal(AttachmentSourceKind.SnapshotOnly, source.Kind);

        Assert.Equal(AttachmentSourceStatus.NotApplicable, source.Status);

        Assert.False(source.IsRefreshable);

        using IServiceScope scope = _factory.Services.CreateScope();

        ISessionAttachmentStore store = scope.ServiceProvider.GetRequiredService<ISessionAttachmentStore>();

        Assert.Equal(expectedBytes, (await store.ReadBytesAsync(row)).ToArray());

    }

    [SkippableFact]

    public async Task PostAttachments_Multipart_AcceptsOneMaximumSizeFileWithEnvelopeOverhead()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(0);

        long maximumReadBytes = ResolveMaximumAttachmentReadBytes();

        byte[] expectedBytes = new byte[checked((int)maximumReadBytes)];

        HttpClient client = _factory.CreateAuthenticatedClient();

        using MultipartFormDataContent form = new();

        using ByteArrayContent fileContent = new(expectedBytes);

        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        form.Add(fileContent, "file", "maximum.bin");

        Assert.True(form.Headers.ContentLength > maximumReadBytes);

        HttpResponseMessage response = await client.PostAsync(

            $"/api/sessions/{sessionId:D}/attachments",

            form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        SessionAttachmentRecord row = Assert.Single(await GetBoundAttachmentsAsync(sessionId));

        Assert.Equal(maximumReadBytes, row.ByteLength);

    }

    [SkippableTheory]

    [InlineData(false)]

    [InlineData(true)]

    public async Task PostAttachments_Multipart_RejectsAggregateBodyAboveFileLimitAndEnvelopeAllowance(

        bool unknownLength)

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(0);

        long maximumReadBytes = ResolveMaximumAttachmentReadBytes();

        int sectionBytes = checked((int)(maximumReadBytes * 3 / 5));

        HttpClient client = _factory.CreateAuthenticatedClient();

        using MultipartFormDataContent form = new();

        using ByteArrayContent fileContent = new(new byte[sectionBytes]);

        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        form.Add(fileContent, "file", "first.bin");

        using ByteArrayContent extraContent = new(new byte[sectionBytes]);

        extraContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        form.Add(extraContent, "extra", "second.bin");

        using HttpRequestMessage request = new(

            HttpMethod.Post,

            $"/api/sessions/{sessionId:D}/attachments")

        {

            Content = unknownLength

                ? new UnknownLengthHttpContent(form)

                : form,

        };

        if (unknownLength)

        {

            request.Headers.TransferEncodingChunked = true;

            Assert.Null(request.Content.Headers.ContentLength);

        }

        HttpResponseMessage response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        ApiResponse<SessionAttachmentDto>? payload = await response.Content

            .ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseSessionAttachmentDto);

        Assert.NotNull(payload);

        Assert.False(payload.IsSuccess);

        Assert.Equal(ErrorCodes.Attachment.TooLarge, payload.Error?.Code);

        Assert.Empty(await GetBoundAttachmentsAsync(sessionId));

    }

    [SkippableTheory]

    [InlineData("application/pdf", "report.pdf", "application/pdf")]

    [InlineData(

        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",

        "report.docx",

        "application/vnd.openxmlformats-officedocument.wordprocessingml.document")]

    [InlineData("application/octet-stream", "payload.bin", "application/octet-stream")]

    public async Task PostAttachments_Multipart_KeepsUnsupportedBinaryAsValidNotEligibleSnapshot(

        string declaredMimeType,

        string fileName,

        string expectedMimeType)

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(0);

        byte[] expectedBytes = fileName.EndsWith(".pdf", StringComparison.Ordinal)

            ? "%PDF-1.7\nunsupported attachment fixture"u8.ToArray()

            : fileName.EndsWith(".docx", StringComparison.Ordinal)

                ? [0x50, 0x4B, 0x03, 0x04, 0x00, 0xFF, 0x10, 0x80]

                : [0x00, 0xFF, 0x10, 0x80, 0x7F];

        HttpClient client = _factory.CreateAuthenticatedClient();

        using MultipartFormDataContent form = new();

        using ByteArrayContent fileContent = new(expectedBytes);

        fileContent.Headers.ContentType = new MediaTypeHeaderValue(declaredMimeType);

        form.Add(fileContent, "file", fileName);

        HttpResponseMessage response = await client.PostAsync(

            $"/api/sessions/{sessionId:D}/attachments",

            form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        ApiResponse<SessionAttachmentDto>? payload = await response.Content

            .ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseSessionAttachmentDto);

        Assert.NotNull(payload);

        Assert.True(payload.IsSuccess);

        Assert.Equal("Binary", payload.Data!.Kind.ToString());

        Assert.Equal(expectedMimeType, payload.Data.MimeType);

        Assert.Equal(SessionAttachmentIndexStatus.NotEligible, payload.Data.IndexingStatus);

        SessionAttachmentRecord row = Assert.Single(await GetBoundAttachmentsAsync(sessionId));

        AttachmentSourceMetadata source = row.Source ?? AttachmentSourceMetadata.SnapshotOnly;

        Assert.Equal(AttachmentSourceKind.SnapshotOnly, source.Kind);

        Assert.Equal(expectedBytes, (await ReadAttachmentBytesAsync(row)).ToArray());

    }

    [SkippableFact]

    public async Task PostAttachments_Multipart_UnsupportedBinaryStillHonorsAttachmentByteBudget()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(0);

        ArcanumSettings settings = _factory.Services

            .GetRequiredService<IOptions<ArcanumSettings>>()

            .Value;

        long maximumReadBytes = SessionAttachmentContentPolicy.ResolveMaximumReadBytes(settings);

        byte[] oversized = new byte[checked((int)maximumReadBytes + 1)];

        HttpClient client = _factory.CreateAuthenticatedClient();

        using MultipartFormDataContent form = new();

        using ByteArrayContent fileContent = new(oversized);

        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        form.Add(fileContent, "file", "oversized.bin");

        HttpResponseMessage response = await client.PostAsync(

            $"/api/sessions/{sessionId:D}/attachments",

            form);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);

        Assert.Empty(await GetBoundAttachmentsAsync(sessionId));

    }

    [SkippableFact]

    public async Task GetAttachmentContent_ReturnsPlaintextDownloadWithoutSourcePath()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(0);

        byte[] expectedBytes = Encoding.UTF8.GetBytes("downloaded snapshot bytes");

        string sourcePath = Path.Combine(

            _factory.TempHome,

            "issue26-content-" + Guid.NewGuid().ToString("N") + ".txt");

        try

        {

            await File.WriteAllBytesAsync(sourcePath, expectedBytes);

            SessionAttachmentRecord attachment;

            using (IServiceScope scope = _factory.Services.CreateScope())

            {

                ISessionAttachmentStore store = scope.ServiceProvider

                    .GetRequiredService<ISessionAttachmentStore>();

                attachment = await store.PersistNewFromSourceAsync(

                    sessionId,

                    pendingTurnId: null,

                    entryId: null,

                    logicalNameHint: "download-notes",

                    originalFileName: "download-notes.txt",

                    expectedBytes,

                    mimeType: "text/plain",

                    SessionAttachmentKind.Text,

                    new AttachmentSourceClaim(sourcePath));

            }

            HttpClient client = _factory.CreateAuthenticatedClient();

            HttpResponseMessage response = await client.GetAsync(

                $"/api/sessions/{sessionId:D}/attachments/{attachment.Id:D}/content");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            Assert.Equal(expectedBytes, await response.Content.ReadAsByteArrayAsync());

            Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);

            Assert.NotNull(response.Content.Headers.ContentDisposition);

            Assert.Equal(

                "attachment",

                response.Content.Headers.ContentDisposition!.DispositionType);

            Assert.Contains("no-store", response.Headers.CacheControl!.ToString(), StringComparison.Ordinal);

            Assert.Equal(

                "nosniff",

                Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));

            string responseHeaders = response.Headers.ToString()

                + response.Content.Headers;

            Assert.DoesNotContain(sourcePath, responseHeaders, StringComparison.Ordinal);

            Assert.DoesNotContain(_factory.TempHome, responseHeaders, StringComparison.Ordinal);

        }
        finally

        {

            if (File.Exists(sourcePath))

            {

                File.Delete(sourcePath);

            }

        }

    }

    [SkippableFact]

    public async Task RefreshAttachment_MissingAttachment_ReturnsNotFound()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(0);

        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage response = await client.PostAsync(

            $"/api/sessions/{sessionId:D}/attachments/{Guid.NewGuid():D}/refresh",

            content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        ApiResponse<AttachmentRefreshEvent>? payload = await response.Content

            .ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseAttachmentRefreshEvent);

        Assert.NotNull(payload);

        Assert.False(payload.IsSuccess);

        Assert.Equal(ErrorCodes.Attachment.NotFound, payload.Error?.Code);

    }

    [SkippableFact]

    public async Task PostAttachmentReference_WorkspaceFile_CreatesRefreshableRowFromCurrentBytes()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(0);

        string fileName = "issue26-reference-" + Guid.NewGuid().ToString("N") + ".txt";

        string sourcePath = Path.Combine(_factory.TempHome, fileName);

        byte[] expectedBytes = Encoding.UTF8.GetBytes("current live workspace bytes");

        try

        {

            await File.WriteAllBytesAsync(sourcePath, expectedBytes);

            HttpClient client = _factory.CreateAuthenticatedClient();

            using JsonContent request = JsonContent.Create(new

            {

                workspacePath = fileName,

            });

            HttpResponseMessage response = await client.PostAsync(

                $"/api/sessions/{sessionId:D}/attachments/reference",

                request);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            string responseBody = await response.Content.ReadAsStringAsync();

            Assert.DoesNotContain(_factory.TempHome, responseBody, StringComparison.Ordinal);

            SessionAttachmentRecord row = Assert.Single(await GetBoundAttachmentsAsync(sessionId));

            Assert.Equal(SessionAttachmentState.Bound, row.State);

            Assert.NotNull(row.Source);

            Assert.Equal(AttachmentSourceKind.WorkspaceFile, row.Source.Kind);

            Assert.Equal(AttachmentSourceStatus.Refreshable, row.Source.Status);

            Assert.True(row.Source.IsRefreshable);

            Assert.Equal(fileName, row.Source.WorkspaceRelativePath);

            using IServiceScope scope = _factory.Services.CreateScope();

            ISessionAttachmentStore store = scope.ServiceProvider.GetRequiredService<ISessionAttachmentStore>();

            Assert.Equal(expectedBytes, (await store.ReadBytesAsync(row)).ToArray());

        }
        finally

        {

            if (File.Exists(sourcePath))

            {

                File.Delete(sourcePath);

            }

        }

    }

    [SkippableFact]

    public async Task PostAttachmentReference_ExplicitRegisteredWorkspace_UsesSelectedRoot()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(0);

        string workspaceRoot = Path.Combine(

            _factory.TempHome,

            "issue26-workspace-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(workspaceRoot);

        WorkspaceInfo? workspace = null;

        try

        {

            using (IServiceScope scope = _factory.Services.CreateScope())

            {

                IWorkspaceRegistry registry = scope.ServiceProvider.GetRequiredService<IWorkspaceRegistry>();

                Result<WorkspaceInfo> registered = await registry.RegisterAsync(

                    new CreateWorkspaceRequest(

                        "issue26-" + Guid.NewGuid().ToString("N"),

                        workspaceRoot,

                        WorkspaceType.Custom),

                    CancellationToken.None);

                Assert.True(registered.IsSuccess, registered.Error.Message);

                workspace = registered.Value;

            }

            string fileName = "selected-workspace.txt";

            byte[] expectedBytes = Encoding.UTF8.GetBytes("selected workspace bytes");

            await File.WriteAllBytesAsync(

                Path.Combine(workspaceRoot, fileName),

                expectedBytes);

            HttpClient client = _factory.CreateAuthenticatedClient();

            using JsonContent request = JsonContent.Create(new

            {

                workspacePath = fileName,

                workspaceId = workspace!.Id,

            });

            HttpResponseMessage response = await client.PostAsync(

                $"/api/sessions/{sessionId:D}/attachments/reference",

                request);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            SessionAttachmentRecord row = Assert.Single(await GetBoundAttachmentsAsync(sessionId));

            Assert.Equal(workspace!.Id, row.Source!.WorkspaceIdentity);

            Assert.Equal(fileName, row.Source.WorkspaceRelativePath);

            using IServiceScope readScope = _factory.Services.CreateScope();

            ISessionAttachmentStore store = readScope.ServiceProvider.GetRequiredService<ISessionAttachmentStore>();

            Assert.Equal(expectedBytes, (await store.ReadBytesAsync(row)).ToArray());

        }
        finally

        {

            if (workspace is not null)

            {

                using IServiceScope scope = _factory.Services.CreateScope();

                IWorkspaceRegistry registry = scope.ServiceProvider.GetRequiredService<IWorkspaceRegistry>();

                _ = await registry.UnregisterAsync(workspace.Id, CancellationToken.None);

            }

            if (Directory.Exists(workspaceRoot))

            {

                Directory.Delete(workspaceRoot, recursive: true);

            }

        }

    }

    [SkippableFact]

    public async Task PostAttachmentReference_ThenManualRefresh_PersistsNewCurrentVersion()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(0);

        string fileName = "issue26-refresh-" + Guid.NewGuid().ToString("N") + ".txt";

        string sourcePath = Path.Combine(_factory.TempHome, fileName);

        byte[] initialBytes = Encoding.UTF8.GetBytes("initial live bytes");

        byte[] refreshedBytes = Encoding.UTF8.GetBytes("refreshed live bytes");

        try

        {

            await File.WriteAllBytesAsync(sourcePath, initialBytes);

            HttpClient client = _factory.CreateAuthenticatedClient();

            using JsonContent request = JsonContent.Create(new

            {

                workspacePath = fileName,

                logicalName = "refreshable-notes",

            });

            HttpResponseMessage createdResponse = await client.PostAsync(

                $"/api/sessions/{sessionId:D}/attachments/reference",

                request);

            Assert.Equal(HttpStatusCode.Created, createdResponse.StatusCode);

            ApiResponse<SessionAttachmentDto>? created = await createdResponse.Content

                .ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseSessionAttachmentDto);

            Assert.NotNull(created?.Data);

            await File.WriteAllBytesAsync(sourcePath, refreshedBytes);

            HttpResponseMessage refreshResponse = await client.PostAsync(

                $"/api/sessions/{sessionId:D}/attachments/{created.Data.Id:D}/refresh",

                content: null);

            Assert.Equal(HttpStatusCode.OK, refreshResponse.StatusCode);

            ApiResponse<AttachmentRefreshEvent>? refreshed = await refreshResponse.Content

                .ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseAttachmentRefreshEvent);

            Assert.NotNull(refreshed?.Data);

            Assert.True(refreshed.IsSuccess);

            Assert.True(refreshed.Data.NewVersionCreated);

            Assert.Equal(2, refreshed.Data.Version);

            Assert.Equal("refreshable-notes", refreshed.Data.LogicalKey);

            IReadOnlyList<SessionAttachmentRecord> rows = await GetBoundAttachmentsAsync(sessionId);

            SessionAttachmentRecord current = Assert.Single(

                rows,

                static row => row.Version == 2);

            using IServiceScope scope = _factory.Services.CreateScope();

            ISessionAttachmentStore store = scope.ServiceProvider.GetRequiredService<ISessionAttachmentStore>();

            Assert.Equal(refreshedBytes, (await store.ReadBytesAsync(current)).ToArray());

        }
        finally

        {

            if (File.Exists(sourcePath))

            {

                File.Delete(sourcePath);

            }

        }

    }

    [SkippableFact]

    public async Task PostAttachmentReference_TraversalOutsideWorkspace_FailsWithoutPersistence()

    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionWithEntriesAsync(0);

        string fileName = "issue26-outside-" + Guid.NewGuid().ToString("N") + ".txt";

        string outsidePath = Path.Combine(

            Path.GetDirectoryName(_factory.TempHome)!,

            fileName);

        try

        {

            await File.WriteAllTextAsync(outsidePath, "outside workspace");

            HttpClient client = _factory.CreateAuthenticatedClient();

            using JsonContent request = JsonContent.Create(new

            {

                workspacePath = "../" + fileName,

            });

            HttpResponseMessage response = await client.PostAsync(

                $"/api/sessions/{sessionId:D}/attachments/reference",

                request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            Assert.Empty(await GetBoundAttachmentsAsync(sessionId));

        }
        finally

        {

            if (File.Exists(outsidePath))

            {

                File.Delete(outsidePath);

            }

        }

    }

    private long ResolveMaximumAttachmentReadBytes()

    {

        ArcanumSettings settings = _factory.Services

            .GetRequiredService<IOptions<ArcanumSettings>>()

            .Value;

        return SessionAttachmentContentPolicy.ResolveMaximumReadBytes(settings);

    }

    private async Task<IReadOnlyList<SessionAttachmentRecord>> GetBoundAttachmentsAsync(Guid sessionId)

    {

        using IServiceScope scope = _factory.Services.CreateScope();

        ISessionAttachmentStore store = scope.ServiceProvider.GetRequiredService<ISessionAttachmentStore>();

        return await store.ListBoundAsync(sessionId);

    }

    private async Task<ReadOnlyMemory<byte>> ReadAttachmentBytesAsync(

        SessionAttachmentRecord attachment)

    {

        using IServiceScope scope = _factory.Services.CreateScope();

        ISessionAttachmentStore store = scope.ServiceProvider.GetRequiredService<ISessionAttachmentStore>();

        return await store.ReadBytesAsync(attachment);

    }

    private async Task<Guid> CreateSessionWithEntriesAsync(int count)
    {

        using IServiceScope scope = _factory.Services.CreateScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        Session session = new() { Title = "count-test" };

        _ = db.Sessions.Add(session);

        for (int i = 0; i < count; i++)
        {

            Entry entry = new()
            {
                SessionId = session.Id,
                Role = MessageRole.User,
                Content = $"entry {i}",
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-count + i),
                Sequence = i + 1,
            };

            _ = db.Entries.Add(entry);

        }

        await db.SaveChangesAsync();

        return session.Id;

    }

    private async Task<Guid[]> GetEntryIdsAsync(Guid sessionId)

    {

        using IServiceScope scope = _factory.Services.CreateScope();

        ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

        return await db.Entries

            .Where(entry => entry.SessionId == sessionId)

            .OrderBy(entry => entry.Sequence)

            .Select(entry => entry.Id)

            .ToArrayAsync();

    }

    /// <summary>
    /// A session repository whose Grimoire replay read faults. Everything the stream endpoint needs
    /// before the replay succeeds, so the fault lands squarely inside the replay region of
    /// <c>GET /api/sessions/{id}/stream</c>.
    /// </summary>
    private sealed class ReplayFailingSessionRepository : ISessionRepository
    {

        public static readonly Guid SessionId = Guid.Parse("6f1b7d4c-5e4a-4a1b-9f2d-8c7b6a5d4e3f");

        public Task<Session?> GetByIdAsync(Guid id, CancellationToken ct) =>
            Task.FromResult<Session?>(
                id == SessionId
                    ? new Session { Id = SessionId, Title = "replay-failure" }
                    : null);

        public Task<List<Entry>> GetEntriesAscendingAsync(Guid sessionId, int takeLast, CancellationToken ct = default) =>
            throw new InvalidOperationException("Grimoire replay read failed.");

        public Task<Session> CreateAsync(Guid? campaignId, string? title, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SessionQueryResult> QueryAsync(SessionQueryRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<SessionAnalytics> GetAnalyticsAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Result<SessionExportResult>> ExportAsync(Guid id, SessionExportFormat format, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Result<Entry>> AddEntryAsync(Guid sessionId, Entry entry, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Result<Session>> ForkAsync(Guid sourceId, ForkSessionRequest request, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<List<Entry>> GetEntriesAfterAsync(Guid sessionId, long afterSequence, int limit, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Entry?> GetEntryAsync(Guid sessionId, Guid entryId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<List<Entry>> GetEntriesAsync(
            Guid sessionId,
            int offset = 0,
            int limit = 100,
            DateTimeOffset? beforeCreatedAt = null,
            Guid? beforeId = null,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<int> GetEntryCountAsync(Guid sessionId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task UpdateSessionAsync(Session session, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task ArchiveAsync(Guid id, CancellationToken ct) =>
            throw new NotSupportedException();

    }

    private sealed class UnknownLengthHttpContent : HttpContent

    {

        private readonly HttpContent _inner;

        public UnknownLengthHttpContent(HttpContent inner)

        {

            _inner = inner;

            foreach (KeyValuePair<string, IEnumerable<string>> header in inner.Headers)

            {

                if (!string.Equals(

                        header.Key,

                        "Content-Length",

                        StringComparison.OrdinalIgnoreCase))

                {

                    _ = Headers.TryAddWithoutValidation(header.Key, header.Value);

                }

            }

        }

        protected override Task SerializeToStreamAsync(

            Stream stream,

            TransportContext? context) =>

            _inner.CopyToAsync(stream);

        protected override bool TryComputeLength(out long length)

        {

            length = 0;

            return false;

        }

        protected override void Dispose(bool disposing)

        {

            if (disposing)

            {

                _inner.Dispose();

            }

            base.Dispose(disposing);

        }

    }

}
