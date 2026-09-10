using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Workspaces;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Repositories;

[Collection("Grimoire")]
public sealed class SessionTurnBeginSqlParityTests : IAsyncLifetime
{
    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public SessionTurnBeginSqlParityTests(GrimoireFixture fixture) =>
        _fixture = fixture;

    public Task InitializeAsync()
    {
        _dbPath = _fixture.CopyDatabase();
        _db = _fixture.CreateContext(_dbPath);

        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }

        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [SkippableFact]
    public async Task BeginAssistantReplyAsync_reports_not_found_for_a_missing_session()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Result<AssistantReplyBeginReceipt> result = await Repository().BeginAssistantReplyAsync(
            Guid.NewGuid(),
            CanonicalCampaignContext.GlobalOnly,
            "hello",
            "test-model",
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Session.NotFound, result.Error.Code);
        Assert.Empty(await _db!.Entries.AsNoTracking().ToArrayAsync(CancellationToken.None));
    }

    [SkippableFact]
    public async Task BeginAssistantReplyAsync_reports_integrity_failure_for_a_session_without_a_binding()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        _ = _db!.Sessions.Add(new Session
        {
            Id = sessionId,
            Title = "unbound",
            Status = "active",
            CreatedAt = now,
            UpdatedAt = now,
        });
        _ = await _db.SaveChangesAsync(CancellationToken.None);

        Result<AssistantReplyBeginReceipt> result = await Repository().BeginAssistantReplyAsync(
            sessionId,
            CanonicalCampaignContext.GlobalOnly,
            "hello",
            "test-model",
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Covenant.IntegrityFailure, result.Error.Code);
        Assert.Empty(await _db.Entries.AsNoTracking().ToArrayAsync(CancellationToken.None));
    }

    [SkippableFact]
    public async Task BeginAssistantReplyAsync_rechecks_that_its_bound_campaign_is_still_live()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid campaignId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string name = "turn-begin-" + campaignId.ToString("N");

        Campaign campaign = new()
        {
            Id = campaignId,
            Name = name,
            NameLower = name,
            Path = Path.Combine(Path.GetTempPath(), name),
            Type = WorkspaceType.Campaign,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _ = _db!.Campaigns.Add(campaign);
        _ = await _db.SaveChangesAsync(CancellationToken.None);

        CanonicalCampaignContext campaignContext = CanonicalCampaignContext.Create(
            SessionCampaignBinding.ForCampaign(campaignId),
            campaignAvailabilityGeneration: 1,
            pathIdentityPolicyVersion: 1,
            pathIdentityRevision: null,
            rootIdentityDigest: null);
        GrimoireRepository repository = Repository();

        Result<Guid> created = await repository.CreateBoundSessionAsync(
            campaignContext,
            "campaign-bound",
            CancellationToken.None);
        Assert.True(created.IsSuccess, created.Error.Message);

        CampaignRepository campaigns = new(
            _db,
            NullLogger<CampaignRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()));
        Assert.True(await campaigns.DeleteAsync(campaignId, CancellationToken.None));

        Result<AssistantReplyBeginReceipt> result = await repository.BeginAssistantReplyAsync(
            created.Value,
            campaignContext,
            "hello",
            "test-model",
            CancellationToken.None);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Campaign.NotFound, result.Error.Code);
        Assert.Empty(await _db.Entries.AsNoTracking().ToArrayAsync(CancellationToken.None));
    }

    private GrimoireRepository Repository() =>
        new(
            _db!,
            new NoOpSessionAttachmentStore(),
            NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            attachmentIndex: null,
            covenantKernel: null,
            FixtureOrdinaryConnectionFactory.For(_db!),
            FixtureLabeledArtifactGuard.For(_db!));
}
