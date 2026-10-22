using System.Net;

using System.Net.Http.Headers;

using System.Net.Http.Json;

using System.Text;

using Microsoft.Extensions.DependencyInjection;

using Microsoft.Extensions.Options;

using RetroDownfall.Arcanum.Api.Serialization;

using RetroDownfall.Arcanum.Core.Configuration;

using RetroDownfall.Arcanum.Core.Intelligence.Models;

using RetroDownfall.Arcanum.Core.Primitives;

using RetroDownfall.Arcanum.Core.Storage;

using RetroDownfall.Arcanum.Core.Tower;

using RetroDownfall.Arcanum.Infrastructure.Weave;

using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

[Collection("ApiHost")]
public sealed class SessionAttachmentComprehensionIntakeTests
{
    [SkippableTheory]
    [InlineData("document.arccomp")]
    [InlineData("document.bin")]
    public async Task MultipartSnapshot_RetainsDetectedDocumentMimeAndBecomesEligibleAfterOptIn(string fileName)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { AttachmentRetrieval = false, Comprehension = false },
            },
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = await CreateSessionAsync(client);

        using MultipartFormDataContent form = new();

        using ByteArrayContent file = new("ARCCOMP1\nsnapshot payload"u8.ToArray());

        file.Headers.ContentType = new MediaTypeHeaderValue(SessionAttachmentComprehensionPolicy.MimeType);

        form.Add(file, "file", fileName);

        using HttpResponseMessage response = await client.PostAsync($"/api/sessions/{sessionId:D}/attachments", form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        SessionAttachmentDto created = await ReadAttachmentAsync(response);

        await AssertStoredDocumentAsync(factory, created, "snapshot payload");
    }

    [SkippableTheory]
    [InlineData("document.arccomp")]
    [InlineData("document.bin")]
    public async Task WorkspaceReferenceAndRefresh_RetainDocumentMimeAcrossVersions(string fileName)
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = settings => settings with
            {
                Features = settings.Features with { AttachmentRetrieval = false, Comprehension = false },
            },
        };

        string path = Path.Combine(factory.TempHome, fileName);

        await File.WriteAllBytesAsync(path, "ARCCOMP1\noriginal payload"u8.ToArray());

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = await CreateSessionAsync(client);

        using StringContent request = new("{\"workspacePath\":\"" + fileName + "\"}", Encoding.UTF8, "application/json");

        using HttpResponseMessage referenced = await client.PostAsync(
            $"/api/sessions/{sessionId:D}/attachments/reference", request);

        Assert.Equal(HttpStatusCode.Created, referenced.StatusCode);

        SessionAttachmentDto first = await ReadAttachmentAsync(referenced);

        Assert.True(first.IsRefreshable);

        await AssertStoredDocumentAsync(factory, first, "original payload");

        await File.WriteAllBytesAsync(path, "ARCCOMP1\nupdated payload"u8.ToArray());

        using HttpResponseMessage refreshed = await client.PostAsync(
            $"/api/sessions/{sessionId:D}/attachments/{first.Id:D}/refresh", content: null);

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        ApiResponse<AttachmentRefreshEvent>? refreshEvent = await refreshed.Content
            .ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseAttachmentRefreshEvent);

        Assert.NotNull(refreshEvent?.Data);

        Assert.True(refreshEvent.Data.NewVersionCreated);

        using HttpResponseMessage listed = await client.GetAsync($"/api/sessions/{sessionId:D}/attachments");

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        ApiResponse<SessionAttachmentDto[]>? listing = await listed.Content
            .ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray);

        Assert.NotNull(listing?.Data);

        SessionAttachmentDto second = Assert.Single(listing.Data, item => item.Id == refreshEvent.Data.AttachmentId);

        Assert.Equal(2, second.Version);

        Assert.Equal(first.LogicalKey, second.LogicalKey);

        Assert.NotEqual(first.ContentSha256, second.ContentSha256);

        await AssertStoredDocumentAsync(factory, second, "updated payload");
    }

    private static async Task AssertStoredDocumentAsync(
        ArcanumWebApplicationFactory factory,
        SessionAttachmentDto metadata,
        string expectedText)
    {
        Assert.Equal(SessionAttachmentComprehensionPolicy.MimeType, metadata.MimeType);

        Assert.Equal(SessionAttachmentKind.Binary, metadata.Kind);

        using IServiceScope scope = factory.Services.CreateScope();

        ISessionAttachmentStore store = scope.ServiceProvider.GetRequiredService<ISessionAttachmentStore>();

        SessionAttachmentRecord? record = await store.GetByIdAsync(metadata.Id);

        Assert.NotNull(record);

        ArcanumSettings settings = scope.ServiceProvider.GetRequiredService<IOptions<ArcanumSettings>>().Value;

        Assert.False(SessionAttachmentComprehensionPolicy.IsEligibleDocument(record, settings));

        ArcanumSettings optedIn = settings with
        {
            Features = settings.Features with { AttachmentRetrieval = true, Comprehension = true },
        };

        Assert.True(SessionAttachmentComprehensionPolicy.IsEligibleDocument(record, optedIn));

        SessionAttachmentExtractionResult extraction = await SessionAttachmentTextExtractor.ExtractDocumentAsync(
            store, record, optedIn, 1024, CancellationToken.None);

        Assert.Equal(SessionAttachmentExtractionStatus.Extracted, extraction.Status);

        Assert.Equal(expectedText, extraction.Text);
    }

    private static async Task<Guid> CreateSessionAsync(HttpClient client)
    {
        using HttpResponseMessage response = await client.PostAsync(
            "/api/sessions", new StringContent("{}", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        ApiResponse<SessionDetailDto>? result = await response.Content
            .ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseSessionDetailDto);

        Assert.NotNull(result?.Data);

        return result.Data.Id;
    }

    private static async Task<SessionAttachmentDto> ReadAttachmentAsync(HttpResponseMessage response)
    {
        ApiResponse<SessionAttachmentDto>? result = await response.Content
            .ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseSessionAttachmentDto);

        Assert.NotNull(result?.Data);

        return result.Data;
    }
}
