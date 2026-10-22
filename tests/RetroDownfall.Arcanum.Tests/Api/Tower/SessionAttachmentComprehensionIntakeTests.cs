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

using RetroDownfall.Arcanum.Tests.Support;

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

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MultipartSnapshot_NamedOfficePackages_AreBinaryWithTheirRegistryMimeAndExtractToTheirVisibleText(bool comprehension)
    {
        // Sensitivity: the extension map in AttachmentMimeDetector. A format missing from it is stored as
        // application/octet-stream and its row fails on the MIME, whatever the feature flags say.
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = comprehension ? ComprehensionOn : ComprehensionOff,
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = await CreateSessionAsync(client);

        foreach (ComprehensionFormatFixture fixture in ComprehensionFormatFixtures.All)
        {
            SessionAttachmentDto created = await UploadAsync(client, sessionId, fixture.FileName, fixture.Package, fixture.MimeType);

            Assert.Equal(fixture.MimeType, created.MimeType);

            Assert.Equal(SessionAttachmentKind.Binary, created.Kind);

            using IServiceScope scope = factory.Services.CreateScope();

            ISessionAttachmentStore store = scope.ServiceProvider.GetRequiredService<ISessionAttachmentStore>();

            SessionAttachmentRecord record = await store.GetByIdAsync(created.Id)
                ?? throw new InvalidOperationException("The uploaded attachment was not stored.");

            Assert.Equal(fixture.MimeType, record.MimeType);

            Assert.Equal(SessionAttachmentKind.Binary, record.Kind);

            ArcanumSettings settings = scope.ServiceProvider.GetRequiredService<IOptions<ArcanumSettings>>().Value;

            Assert.Equal(comprehension, SessionAttachmentComprehensionPolicy.IsEligibleDocument(record, settings));

            ArcanumSettings optedIn = settings with
            {
                Features = settings.Features with { AttachmentRetrieval = true, Comprehension = true },
            };

            Assert.True(SessionAttachmentComprehensionPolicy.IsEligibleDocument(record, optedIn));

            SessionAttachmentExtractionResult extraction = await SessionAttachmentTextExtractor.ExtractDocumentAsync(
                store, record, optedIn, 1024 * 1024, CancellationToken.None);

            Assert.Equal(SessionAttachmentExtractionStatus.Extracted, extraction.Status);

            fixture.AssertSurfaces(extraction.Text);
        }
    }

    [SkippableFact]
    public async Task MultipartSnapshot_OfficePackagesUploadedUnderABinName_AreOctetStreamNeverEligibleAndNeverExtracted()
    {
        // Sensitivity: sniffing. A package is classified by its file name only; its bytes and a declared Content-Type
        // naming the registry MIME never grant eligibility, so a renamed file cannot reach a parser.
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        await using ArcanumWebApplicationFactory factory = new()
        {
            SettingsOverride = ComprehensionOn,
        };

        HttpClient client = factory.CreateAuthenticatedClient();

        Guid sessionId = await CreateSessionAsync(client);

        foreach (ComprehensionFormatFixture fixture in ComprehensionFormatFixtures.All)
        {
            string renamed = Path.GetFileNameWithoutExtension(fixture.FileName) + ".bin";

            SessionAttachmentDto created = await UploadAsync(client, sessionId, renamed, fixture.Package, fixture.MimeType);

            Assert.Equal("application/octet-stream", created.MimeType);

            Assert.Equal(SessionAttachmentKind.Binary, created.Kind);

            using IServiceScope scope = factory.Services.CreateScope();

            ISessionAttachmentStore realStore = scope.ServiceProvider.GetRequiredService<ISessionAttachmentStore>();

            SessionAttachmentRecord record = await realStore.GetByIdAsync(created.Id)
                ?? throw new InvalidOperationException("The uploaded attachment was not stored.");

            Assert.Equal("application/octet-stream", record.MimeType);

            ArcanumSettings settings = scope.ServiceProvider.GetRequiredService<IOptions<ArcanumSettings>>().Value;

            ArcanumSettings optedIn = settings with
            {
                Features = settings.Features with { AttachmentRetrieval = true, Comprehension = true },
            };

            Assert.False(SessionAttachmentComprehensionPolicy.IsEligibleDocument(record, optedIn));

            NoOpSessionAttachmentStore store = new(
                readBytes: (_, _) => throw new InvalidOperationException("A renamed package must never be read."),
                openRead: (_, _) => throw new InvalidOperationException("A renamed package must never be streamed."));

            SessionAttachmentExtractionResult extraction = await SessionAttachmentTextExtractor.ExtractDocumentAsync(
                store, record, optedIn, 1024 * 1024, CancellationToken.None);

            Assert.Equal(SessionAttachmentExtractionStatus.NotEligible, extraction.Status);

            Assert.Empty(extraction.Text);
        }
    }

    private static ArcanumSettings ComprehensionOff(ArcanumSettings settings) =>
        settings with
        {
            Features = settings.Features with { AttachmentRetrieval = false, Comprehension = false },
        };

    /// <summary>Attachment retrieval needs a configured embedding provider before it validates; the test provider is enough.</summary>
    private static ArcanumSettings ComprehensionOn(ArcanumSettings settings) =>
        settings with
        {
            Features = settings.Features with { AttachmentRetrieval = true, Comprehension = true },

            Integrations = settings.Integrations with
            {
                Embeddings = settings.Integrations.Embeddings with
                {
                    Provider = "test",

                    Model = "mistral:latest",

                    Dimensions = 64,
                },
            },
        };

    private static async Task<SessionAttachmentDto> UploadAsync(
        HttpClient client,
        Guid sessionId,
        string fileName,
        byte[] bytes,
        string declaredMimeType)
    {
        using MultipartFormDataContent form = new();

        using ByteArrayContent file = new(bytes);

        file.Headers.ContentType = new MediaTypeHeaderValue(declaredMimeType);

        form.Add(file, "file", fileName);

        using HttpResponseMessage response = await client.PostAsync($"/api/sessions/{sessionId:D}/attachments", form);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await ReadAttachmentAsync(response);
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
