using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using RetroDownfall.Arcanum.Api.Serialization;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Api.Tower;

/// <summary>
/// Which Campaign's Sanctum judges a live attachment source, and what its mode lets through. The reference route and the standalone refresh
/// route decide the governing Campaign with the claims every other turn surface uses (the Session's binding and stored Campaign, and the
/// registered root that contains the path), and honour that Campaign's mode: a Strict Sanctum stops the read and records the breach, an
/// audit-only one records the breach and lets the read go on, and a path no registered root contains stays unrestricted.
/// </summary>
/// <remarks>
/// Every case reads a link that lies inside a Campaign's root and points at a file outside it. That is the one path a Campaign's own path
/// boundary refuses although the root contains the spelling the request used.
/// </remarks>
[Collection("ApiHost")]
public sealed class SessionAttachmentSanctumEndpointTests
{
    private readonly ArcanumWebApplicationFactory _factory;

    public SessionAttachmentSanctumEndpointTests(ArcanumWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [SkippableFact]
    public async Task A_reference_through_a_link_out_of_a_strict_campaign_reached_only_by_path_is_refused_and_recorded()
    {
        SkipUnlessLinksCanBeMade();

        using Scenario scenario = await Scenario.CreateAsync(_factory);

        Guid sessionId = await scenario.CreateSessionAsync(campaignId: null);

        Guid campaignId = await scenario.RegisterCampaignAsync(scenario.Area, SanctumMode.Strict);

        HttpResponseMessage response = await scenario.PostReferenceAsync(sessionId, scenario.LinkOutOfArea);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(ErrorCodes.Attachment.SourceUnavailable, await ReadErrorCodeAsync(response));

        Assert.Empty(await scenario.BoundAttachmentsAsync(sessionId));

        SanctumBreachRecord breach = Assert.Single(await scenario.BreachesAsync(campaignId));

        Assert.Equal("PathEscape", breach.BreachType);

        Assert.Equal("attachment_reference", breach.ToolName);
    }

    [SkippableFact]
    public async Task A_reference_through_a_link_out_of_an_audit_only_campaign_reached_only_by_path_is_recorded_and_allowed()
    {
        SkipUnlessLinksCanBeMade();

        using Scenario scenario = await Scenario.CreateAsync(_factory);

        Guid sessionId = await scenario.CreateSessionAsync(campaignId: null);

        Guid campaignId = await scenario.RegisterCampaignAsync(scenario.Area, SanctumMode.AuditOnly);

        HttpResponseMessage response = await scenario.PostReferenceAsync(sessionId, scenario.LinkOutOfArea);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        _ = Assert.Single(await scenario.BoundAttachmentsAsync(sessionId));

        SanctumBreachRecord breach = Assert.Single(await scenario.BreachesAsync(campaignId));

        Assert.Equal("PathEscape", breach.BreachType);

        Assert.Equal("attachment_reference", breach.ToolName);
    }

    [SkippableFact]
    public async Task A_reference_in_a_directory_no_campaign_contains_stays_unrestricted()
    {
        SkipUnlessLinksCanBeMade();

        using Scenario scenario = await Scenario.CreateAsync(_factory);

        Guid sessionId = await scenario.CreateSessionAsync(campaignId: null);

        // A Campaign elsewhere is registered, so the Campaign table is not empty; nothing in it contains the link.
        Guid elsewhereId = await scenario.RegisterCampaignAsync(scenario.Elsewhere, SanctumMode.Strict);

        HttpResponseMessage response = await scenario.PostReferenceAsync(sessionId, scenario.LinkOutOfArea);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        _ = Assert.Single(await scenario.BoundAttachmentsAsync(sessionId));

        Assert.Empty(await scenario.BreachesAsync(elsewhereId));
    }

    [SkippableFact]
    public async Task A_reference_inside_a_strict_campaign_root_is_allowed_and_records_nothing()
    {
        using Scenario scenario = await Scenario.CreateAsync(_factory);

        Guid sessionId = await scenario.CreateSessionAsync(campaignId: null);

        Guid campaignId = await scenario.RegisterCampaignAsync(scenario.Area, SanctumMode.Strict);

        HttpResponseMessage response = await scenario.PostReferenceAsync(sessionId, scenario.FileInArea);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.Empty(await scenario.BreachesAsync(campaignId));
    }

    [SkippableFact]
    public async Task A_reference_from_a_session_bound_to_a_strict_campaign_is_refused_when_the_file_is_outside_its_root()
    {
        using Scenario scenario = await Scenario.CreateAsync(_factory);

        Guid campaignId = await scenario.RegisterCampaignAsync(scenario.Area, SanctumMode.Strict);

        Guid sessionId = await scenario.CreateSessionAsync(campaignId);

        HttpResponseMessage response = await scenario.PostReferenceAsync(sessionId, scenario.FileOutsideArea);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Empty(await scenario.BoundAttachmentsAsync(sessionId));

        _ = Assert.Single(await scenario.BreachesAsync(campaignId));
    }

    [SkippableFact]
    public async Task A_reference_from_a_session_bound_to_an_audit_only_campaign_is_recorded_and_allowed_when_the_file_is_outside_its_root()
    {
        using Scenario scenario = await Scenario.CreateAsync(_factory);

        Guid campaignId = await scenario.RegisterCampaignAsync(scenario.Area, SanctumMode.AuditOnly);

        Guid sessionId = await scenario.CreateSessionAsync(campaignId);

        HttpResponseMessage response = await scenario.PostReferenceAsync(sessionId, scenario.FileOutsideArea);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        _ = Assert.Single(await scenario.BoundAttachmentsAsync(sessionId));

        SanctumBreachRecord breach = Assert.Single(await scenario.BreachesAsync(campaignId));

        Assert.Equal("PathEscape", breach.BreachType);
    }

    [SkippableFact]
    public async Task The_stricter_of_the_session_campaign_and_the_campaign_reached_by_path_governs_the_reference()
    {
        SkipUnlessLinksCanBeMade();

        using Scenario scenario = await Scenario.CreateAsync(_factory);

        Guid auditOnlyId = await scenario.RegisterCampaignAsync(scenario.Elsewhere, SanctumMode.AuditOnly);

        Guid strictId = await scenario.RegisterCampaignAsync(scenario.Area, SanctumMode.Strict);

        Guid sessionId = await scenario.CreateSessionAsync(auditOnlyId);

        HttpResponseMessage response = await scenario.PostReferenceAsync(sessionId, scenario.LinkOutOfArea);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Empty(await scenario.BoundAttachmentsAsync(sessionId));

        _ = Assert.Single(await scenario.BreachesAsync(strictId));
    }

    [SkippableFact]
    public async Task A_refresh_through_a_link_out_of_a_strict_campaign_reached_only_by_path_is_refused_and_recorded()
    {
        SkipUnlessLinksCanBeMade();

        using Scenario scenario = await Scenario.CreateAsync(_factory);

        Guid sessionId = await scenario.CreateSessionAsync(campaignId: null);

        Guid attachmentId = await scenario.ReferenceAsync(sessionId, scenario.LinkOutOfArea);

        Guid campaignId = await scenario.RegisterCampaignAsync(scenario.Area, SanctumMode.Strict);

        HttpResponseMessage response = await scenario.PostRefreshAsync(sessionId, attachmentId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(ErrorCodes.Attachment.SourceUnavailable, await ReadErrorCodeAsync(response));

        SanctumBreachRecord breach = Assert.Single(await scenario.BreachesAsync(campaignId));

        Assert.Equal("PathEscape", breach.BreachType);

        Assert.Equal("attachment_refresh", breach.ToolName);
    }

    [SkippableFact]
    public async Task A_refresh_through_a_link_out_of_an_audit_only_campaign_reached_only_by_path_is_recorded_and_allowed()
    {
        SkipUnlessLinksCanBeMade();

        using Scenario scenario = await Scenario.CreateAsync(_factory);

        Guid sessionId = await scenario.CreateSessionAsync(campaignId: null);

        Guid attachmentId = await scenario.ReferenceAsync(sessionId, scenario.LinkOutOfArea);

        Guid campaignId = await scenario.RegisterCampaignAsync(scenario.Area, SanctumMode.AuditOnly);

        HttpResponseMessage response = await scenario.PostRefreshAsync(sessionId, attachmentId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SanctumBreachRecord breach = Assert.Single(await scenario.BreachesAsync(campaignId));

        Assert.Equal("PathEscape", breach.BreachType);
    }

    [SkippableFact]
    public async Task A_refresh_from_a_session_bound_to_an_audit_only_campaign_is_recorded_and_allowed_when_the_file_is_outside_its_root()
    {
        using Scenario scenario = await Scenario.CreateAsync(_factory);

        Guid sessionId = await scenario.CreateSessionAsync(campaignId: null);

        Guid attachmentId = await scenario.ReferenceAsync(sessionId, scenario.FileOutsideArea);

        Guid campaignId = await scenario.RegisterCampaignAsync(scenario.Area, SanctumMode.AuditOnly);

        await scenario.BindSessionAsync(sessionId, campaignId);

        HttpResponseMessage response = await scenario.PostRefreshAsync(sessionId, attachmentId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        _ = Assert.Single(await scenario.BreachesAsync(campaignId));
    }

    [SkippableFact]
    public async Task A_refresh_from_a_session_bound_to_a_strict_campaign_is_refused_when_the_file_is_outside_its_root()
    {
        using Scenario scenario = await Scenario.CreateAsync(_factory);

        Guid sessionId = await scenario.CreateSessionAsync(campaignId: null);

        Guid attachmentId = await scenario.ReferenceAsync(sessionId, scenario.FileOutsideArea);

        Guid campaignId = await scenario.RegisterCampaignAsync(scenario.Area, SanctumMode.Strict);

        await scenario.BindSessionAsync(sessionId, campaignId);

        HttpResponseMessage response = await scenario.PostRefreshAsync(sessionId, attachmentId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        _ = Assert.Single(await scenario.BreachesAsync(campaignId));
    }

    [SkippableFact]
    public async Task A_refresh_in_a_directory_no_campaign_contains_stays_unrestricted()
    {
        SkipUnlessLinksCanBeMade();

        using Scenario scenario = await Scenario.CreateAsync(_factory);

        Guid sessionId = await scenario.CreateSessionAsync(campaignId: null);

        Guid attachmentId = await scenario.ReferenceAsync(sessionId, scenario.LinkOutOfArea);

        Guid elsewhereId = await scenario.RegisterCampaignAsync(scenario.Elsewhere, SanctumMode.Strict);

        HttpResponseMessage response = await scenario.PostRefreshAsync(sessionId, attachmentId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Empty(await scenario.BreachesAsync(elsewhereId));
    }

    // The path claim is the Campaign whose root contains the way the source is spelled, so it does not depend on where the spelling's links
    // lead: a root reached through a link to it, a link inside the root that points into another registered root, and a directory link
    // inside the root that points into another registered root are each still the first Campaign's read.
    [SkippableFact]
    public async Task A_reference_spelled_through_a_link_to_a_strict_campaign_root_is_refused_and_recorded()
    {
        SkipUnlessLinksCanBeMade();

        using Scenario scenario = await Scenario.CreateAsync(_factory);

        Guid sessionId = await scenario.CreateSessionAsync(campaignId: null);

        Guid campaignId = await scenario.RegisterCampaignAsync(scenario.Area, SanctumMode.Strict);

        HttpResponseMessage response = await scenario.PostReferenceAsync(sessionId, scenario.LinkOutOfAreaThroughAlias);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(ErrorCodes.Attachment.SourceUnavailable, await ReadErrorCodeAsync(response));

        Assert.Empty(await scenario.BoundAttachmentsAsync(sessionId));

        SanctumBreachRecord breach = Assert.Single(await scenario.BreachesAsync(campaignId));

        Assert.Equal("PathEscape", breach.BreachType);

        Assert.Equal("attachment_reference", breach.ToolName);
    }

    [SkippableFact]
    public async Task A_refresh_spelled_through_a_link_to_a_strict_campaign_root_is_refused_and_recorded()
    {
        SkipUnlessLinksCanBeMade();

        using Scenario scenario = await Scenario.CreateAsync(_factory);

        Guid sessionId = await scenario.CreateSessionAsync(campaignId: null);

        Guid attachmentId = await scenario.ReferenceAsync(sessionId, scenario.LinkOutOfAreaThroughAlias);

        Guid campaignId = await scenario.RegisterCampaignAsync(scenario.Area, SanctumMode.Strict);

        HttpResponseMessage response = await scenario.PostRefreshAsync(sessionId, attachmentId);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Equal(ErrorCodes.Attachment.SourceUnavailable, await ReadErrorCodeAsync(response));

        SanctumBreachRecord breach = Assert.Single(await scenario.BreachesAsync(campaignId));

        Assert.Equal("PathEscape", breach.BreachType);

        Assert.Equal("attachment_refresh", breach.ToolName);
    }

    [SkippableTheory]
    [InlineData(SanctumMode.Strict)]
    [InlineData(SanctumMode.AuditOnly)]
    public async Task A_reference_through_a_link_deeper_in_a_strict_campaign_than_the_root_it_points_into_is_refused_and_recorded(SanctumMode otherMode)
    {
        SkipUnlessLinksCanBeMade();

        using Scenario scenario = await Scenario.CreateAsync(_factory);

        Guid sessionId = await scenario.CreateSessionAsync(campaignId: null);

        Guid strictId = await scenario.RegisterCampaignAsync(scenario.Area, SanctumMode.Strict);

        Guid otherId = await scenario.RegisterCampaignAsync(scenario.Other, otherMode);

        HttpResponseMessage response = await scenario.PostReferenceAsync(sessionId, scenario.DeepLinkIntoOther);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Empty(await scenario.BoundAttachmentsAsync(sessionId));

        SanctumBreachRecord breach = Assert.Single(await scenario.BreachesAsync(strictId));

        Assert.Equal("PathEscape", breach.BreachType);

        Assert.Empty(await scenario.BreachesAsync(otherId));
    }

    [SkippableTheory]
    [InlineData(SanctumMode.Strict)]
    [InlineData(SanctumMode.AuditOnly)]
    public async Task A_reference_through_a_directory_link_out_of_a_strict_campaign_into_another_campaign_is_refused_and_recorded(SanctumMode otherMode)
    {
        SkipUnlessLinksCanBeMade();

        using Scenario scenario = await Scenario.CreateAsync(_factory);

        Guid sessionId = await scenario.CreateSessionAsync(campaignId: null);

        Guid strictId = await scenario.RegisterCampaignAsync(scenario.Area, SanctumMode.Strict);

        Guid otherId = await scenario.RegisterCampaignAsync(scenario.Other, otherMode);

        HttpResponseMessage response = await scenario.PostReferenceAsync(sessionId, scenario.FileThroughDirectoryLinkIntoOther);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        Assert.Empty(await scenario.BoundAttachmentsAsync(sessionId));

        _ = Assert.Single(await scenario.BreachesAsync(strictId));

        Assert.Empty(await scenario.BreachesAsync(otherId));
    }

    [SkippableFact]
    public async Task A_reference_through_a_link_into_another_campaign_from_a_directory_no_campaign_contains_is_governed_by_the_other_campaign()
    {
        SkipUnlessLinksCanBeMade();

        using Scenario scenario = await Scenario.CreateAsync(_factory);

        Guid sessionId = await scenario.CreateSessionAsync(campaignId: null);

        Guid otherId = await scenario.RegisterCampaignAsync(scenario.Other, SanctumMode.Strict);

        HttpResponseMessage response = await scenario.PostReferenceAsync(sessionId, scenario.DeepLinkIntoOther);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        Assert.Empty(await scenario.BreachesAsync(otherId));
    }

    // Listing a Session's attachments revalidates each bound source (its status, digest, length and write time) and takes no Sanctum decision:
    // the stored snapshot is what a turn reads, and a passive poll that recorded a breach for every refused source would fill the bounded
    // breach history. DESIGN 11.15 states this exception, so a change that governs the listing must change that sentence with it.
    [SkippableFact]
    public async Task Listing_attachments_revalidates_a_source_outside_a_strict_campaign_without_a_Sanctum_decision()
    {
        SkipUnlessLinksCanBeMade();

        using Scenario scenario = await Scenario.CreateAsync(_factory);

        Guid sessionId = await scenario.CreateSessionAsync(campaignId: null);

        Guid attachmentId = await scenario.ReferenceAsync(sessionId, scenario.FileOutsideArea);

        Guid campaignId = await scenario.RegisterCampaignAsync(scenario.Area, SanctumMode.Strict);

        await scenario.BindSessionAsync(sessionId, campaignId);

        await File.WriteAllTextAsync(scenario.FileOutsideArea, "outside, rewritten");

        HttpResponseMessage response = await scenario.GetAttachmentsAsync(sessionId);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        SessionAttachmentDto listed = Assert.Single(await ReadListAsync(response));

        Assert.Equal(attachmentId, listed.Id);

        Assert.Empty(await scenario.BreachesAsync(campaignId));
    }

    private static void SkipUnlessLinksCanBeMade()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Skip.If(OperatingSystem.IsWindows(), "Creating a symbolic link needs a privilege the Windows test host does not have.");
    }

    private static async Task<SessionAttachmentDto[]> ReadListAsync(HttpResponseMessage response)
    {
        ApiResponse<SessionAttachmentDto[]>? payload = await response.Content
            .ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseSessionAttachmentDtoArray);

        return payload?.Data ?? [];
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        ApiResponse<SessionAttachmentDto>? payload = await response.Content
            .ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseSessionAttachmentDto);

        return payload?.Error?.Code;
    }

    /// <summary>
    /// One case's files and rows. The workspace is the host's default one, so the area, the file outside it and the unregistered directory are
    /// three directories of that workspace, each named for the case; every row the case registers is removed with it.
    /// </summary>
    private sealed class Scenario : IDisposable
    {
        private readonly ArcanumWebApplicationFactory _factory;

        private readonly List<Guid> _campaigns = [];

        private readonly List<Guid> _sessions = [];

        private Scenario(ArcanumWebApplicationFactory factory, string suffix)
        {
            _factory = factory;

            Area = Directory.CreateDirectory(Path.Combine(factory.TempHome, "sanctum-area-" + suffix)).FullName;

            Elsewhere = Directory.CreateDirectory(Path.Combine(factory.TempHome, "sanctum-elsewhere-" + suffix)).FullName;

            FileInArea = Path.Combine(Area, "inside.txt");

            FileOutsideArea = Path.Combine(factory.TempHome, "sanctum-outside-" + suffix + ".txt");

            LinkOutOfArea = Path.Combine(Area, "link.txt");

            Alias = Path.Combine(factory.TempHome, "sanctum-alias-" + suffix);

            Other = Path.Combine(factory.TempHome, "sanctum-other-" + suffix);

            LinkOutOfAreaThroughAlias = Path.Combine(Alias, "link.txt");

            DeepLinkIntoOther = Path.Combine(Area, "sub", "deep.txt");

            FileThroughDirectoryLinkIntoOther = Path.Combine(Area, "dirlink", "target.txt");
        }

        public string Area { get; }

        public string Elsewhere { get; }

        public string FileInArea { get; }

        public string FileOutsideArea { get; }

        public string LinkOutOfArea { get; }

        /// <summary>A directory link to <see cref="Area"/>; the same link out of the area, spelled through it.</summary>
        public string Alias { get; }

        public string LinkOutOfAreaThroughAlias { get; }

        /// <summary>A second directory, holding <c>target.txt</c>, for a Campaign that contains what a link in the area points at.</summary>
        public string Other { get; }

        /// <summary>A link two components below the area that points at <c>target.txt</c> one component below <see cref="Other"/>.</summary>
        public string DeepLinkIntoOther { get; }

        /// <summary>The way into <c>target.txt</c> through a directory link in the area that points at <see cref="Other"/>.</summary>
        public string FileThroughDirectoryLinkIntoOther { get; }

        public static async Task<Scenario> CreateAsync(ArcanumWebApplicationFactory factory)
        {
            Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

            Scenario scenario = new(factory, Guid.NewGuid().ToString("N"));

            await File.WriteAllTextAsync(scenario.FileInArea, "inside the campaign");

            await File.WriteAllTextAsync(scenario.FileOutsideArea, "outside the campaign");

            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(scenario.LinkOutOfArea, scenario.FileOutsideArea);

                _ = Directory.CreateDirectory(scenario.Other);

                string target = Path.Combine(scenario.Other, "target.txt");

                await File.WriteAllTextAsync(target, "inside the other campaign");

                Directory.CreateSymbolicLink(scenario.Alias, scenario.Area);

                _ = Directory.CreateDirectory(Path.GetDirectoryName(scenario.DeepLinkIntoOther)!);

                File.CreateSymbolicLink(scenario.DeepLinkIntoOther, target);

                Directory.CreateSymbolicLink(Path.Combine(scenario.Area, "dirlink"), scenario.Other);
            }

            return scenario;
        }

        public async Task<Guid> RegisterCampaignAsync(string path, SanctumMode mode)
        {
            using IServiceScope scope = _factory.Services.CreateScope();

            ICampaignRepository repository = scope.ServiceProvider.GetRequiredService<ICampaignRepository>();

            DateTimeOffset now = DateTimeOffset.UtcNow;

            Result<Campaign> added = await repository.AddAsync(
                new Campaign
                {
                    Id = Guid.NewGuid(),
                    Name = "sanctum-attachment-" + Guid.NewGuid().ToString("N"),
                    Path = path,
                    Type = WorkspaceType.Campaign,
                    Settings = CampaignRepository.SerializeSettings(CampaignSettings.CreateDefault()),
                    SanctumConfigJson = CampaignRepository.SerializeSanctumConfig(new SanctumConfig { Enabled = true, Mode = mode }),
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                CancellationToken.None);

            Assert.True(added.IsSuccess, added.IsFailure ? added.Error.Code : null);

            _campaigns.Add(added.Value.Id);

            return added.Value.Id;
        }

        public async Task<Guid> CreateSessionAsync(Guid? campaignId)
        {
            using IServiceScope scope = _factory.Services.CreateScope();

            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            Session session = new() { Title = "sanctum-attachment", CampaignId = campaignId };

            _ = db.Sessions.Add(session);

            await db.SaveChangesAsync();

            _sessions.Add(session.Id);

            return session.Id;
        }

        public async Task BindSessionAsync(Guid sessionId, Guid campaignId)
        {
            using IServiceScope scope = _factory.Services.CreateScope();

            ArcanumDbContext db = scope.ServiceProvider.GetRequiredService<ArcanumDbContext>();

            Session session = await db.Sessions.FindAsync(sessionId) ?? throw new InvalidOperationException("The session was not created.");

            session.CampaignId = campaignId;

            _ = await db.SaveChangesAsync();
        }

        public async Task<HttpResponseMessage> PostReferenceAsync(Guid sessionId, string path)
        {
            HttpClient client = _factory.CreateAuthenticatedClient();

            using JsonContent request = JsonContent.Create(
                new { workspacePath = Path.GetRelativePath(_factory.TempHome, path) },
                options: AdHocJson.Options);

            return await client.PostAsync($"/api/sessions/{sessionId:D}/attachments/reference", request);
        }

        public async Task<Guid> ReferenceAsync(Guid sessionId, string path)
        {
            HttpResponseMessage response = await PostReferenceAsync(sessionId, path);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            ApiResponse<SessionAttachmentDto>? created = await response.Content
                .ReadFromJsonAsync(ArcanumJsonContext.Default.ApiResponseSessionAttachmentDto);

            Assert.NotNull(created?.Data);

            return created.Data.Id;
        }

        public async Task<HttpResponseMessage> GetAttachmentsAsync(Guid sessionId)
        {
            HttpClient client = _factory.CreateAuthenticatedClient();

            return await client.GetAsync($"/api/sessions/{sessionId:D}/attachments");
        }

        public async Task<HttpResponseMessage> PostRefreshAsync(Guid sessionId, Guid attachmentId)
        {
            HttpClient client = _factory.CreateAuthenticatedClient();

            return await client.PostAsync($"/api/sessions/{sessionId:D}/attachments/{attachmentId:D}/refresh", content: null);
        }

        public async Task<IReadOnlyList<SessionAttachmentRecord>> BoundAttachmentsAsync(Guid sessionId)
        {
            using IServiceScope scope = _factory.Services.CreateScope();

            ISessionAttachmentStore store = scope.ServiceProvider.GetRequiredService<ISessionAttachmentStore>();

            return await store.ListBoundAsync(sessionId);
        }

        public async Task<IReadOnlyList<SanctumBreachRecord>> BreachesAsync(Guid campaignId)
        {
            using IServiceScope scope = _factory.Services.CreateScope();

            ISanctumBreachRepository breaches = scope.ServiceProvider.GetRequiredService<ISanctumBreachRepository>();

            return await breaches.QueryAsync(campaignId.ToString("D"), limit: 100);
        }

        public void Dispose()
        {
            using IServiceScope scope = _factory.Services.CreateScope();

            ICampaignRepository repository = scope.ServiceProvider.GetRequiredService<ICampaignRepository>();

            foreach (Guid id in _campaigns)
            {
                _ = repository.DeleteAsync(id).GetAwaiter().GetResult();
            }

            // The alias is a link, so it goes before the directory it points at and is removed without removing what it points at.
            if (Directory.Exists(Alias))
            {
                Directory.Delete(Alias);
            }

            _ = TestDirectoryCleanup.TryDelete(Area, nameof(SessionAttachmentSanctumEndpointTests));

            _ = TestDirectoryCleanup.TryDelete(Elsewhere, nameof(SessionAttachmentSanctumEndpointTests));

            _ = TestDirectoryCleanup.TryDelete(Other, nameof(SessionAttachmentSanctumEndpointTests));

            if (File.Exists(FileOutsideArea))
            {
                File.Delete(FileOutsideArea);
            }
        }
    }
}
