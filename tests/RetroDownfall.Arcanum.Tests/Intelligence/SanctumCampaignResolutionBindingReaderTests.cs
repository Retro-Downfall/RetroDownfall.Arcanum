using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using RetroDownfall.Arcanum.Api.Intelligence;
using RetroDownfall.Arcanum.Api.Tower;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Sanctum;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Data.Covenant;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// The Session source of <see cref="SanctumCampaignResolution"/> with the production resolver in front of a binding reader, and the real
/// binding reader against the Grimoire: a Session that exists with no binding row names no Campaign, and a Session whose stored binding
/// cannot be read intact leaves the Sanctum unreadable, although the reader reports both under one code.
/// </summary>
[Collection("Grimoire")]
public sealed class SanctumCampaignResolutionBindingReaderTests : IAsyncLifetime
{
    private static readonly CancellationToken Token = CancellationToken.None;

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public SanctumCampaignResolutionBindingReaderTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        if (!GrimoireFixture.SqlCipherAvailable)
        {
            return Task.CompletedTask;
        }

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

    [Fact]
    public async Task A_session_with_no_binding_row_names_no_campaign_through_the_production_resolver()
    {
        SanctumCampaignResolution.Resolved resolved = await ResolveAsync(
            new Reader(Result<SessionCampaignBindingRecord?>.Failure(SessionCampaignBindingErrors.NoBinding)));

        Assert.Null(resolved.CampaignId);

        Assert.False(resolved.PolicyUnreadable);
    }

    [Fact]
    public async Task A_session_whose_binding_is_malformed_leaves_the_sanctum_unreadable_through_the_production_resolver()
    {
        SanctumCampaignResolution.Resolved resolved = await ResolveAsync(
            new Reader(
                Result<SessionCampaignBindingRecord?>.Failure(
                    new Error(ErrorCodes.Covenant.IntegrityFailure, "This Session's Campaign binding is malformed."))));

        Assert.True(resolved.PolicyUnreadable);
    }

    // The recognised refusal is the one the reader returns. If the reader ever words it differently the Session source would quietly
    // become unreadable for every legacy Session, or worse, a malformed row would be taken for a missing one.
    [SkippableFact]
    public async Task The_real_binding_reader_reports_a_session_without_a_binding_row_as_the_recognised_refusal()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = Guid.NewGuid();

        DateTimeOffset now = DateTimeOffset.UtcNow;

        _ = _db!.Sessions.Add(
            new Session
            {
                Id = sessionId,
                CreatedAt = now,
                UpdatedAt = now,
                Status = "active",
                Title = "legacy session without a binding row",
            });

        _ = await _db.SaveChangesAsync(Token);

        SqliteConnection connection = (SqliteConnection)_db.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(Token);
        }

        SessionCampaignBindingReader reader = new(new FixedCovenantConnectionSource(connection));

        Result<SessionCampaignBindingRecord?> bound = await reader.FindAsync(sessionId, Token);

        Assert.True(bound.IsFailure);

        Assert.True(SessionCampaignBindingErrors.IsNoBinding(bound.Error));

        Result<SessionCampaignBindingRecord?> missing = await reader.FindAsync(Guid.NewGuid(), Token);

        Assert.True(missing.IsFailure);

        Assert.Equal(ErrorCodes.Session.NotFound, missing.Error.Code);

        Assert.False(SessionCampaignBindingErrors.IsNoBinding(missing.Error));

        SanctumCampaignResolution.Resolved resolved = await ResolveAsync(reader, sessionId);

        Assert.Null(resolved.CampaignId);

        Assert.False(resolved.PolicyUnreadable);
    }

    private static async Task<SanctumCampaignResolution.Resolved> ResolveAsync(ISessionCampaignBindingReader bindings, Guid? sessionId = null)
    {
        CanonicalCampaignContextResolver resolver = new(
            bindings,
            UnregisteredCampaignRoots.Instance,
            new NoAvailability());

        return await SanctumCampaignResolution.ResolveAsync(
            sessionId ?? Guid.NewGuid(),
            explicitCampaignId: null,
            workingDirectory: null,
            known: null,
            resolver,
            new EmptyTable(),
            Token);
    }

    private sealed class Reader(Result<SessionCampaignBindingRecord?> answer) : ISessionCampaignBindingReader
    {
        public ValueTask<Result<SessionCampaignBindingRecord?>> FindAsync(Guid? sessionId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(answer);
    }

    private sealed class NoAvailability : ICampaignAvailabilityReader
    {
        public ValueTask<Result<long?>> FindAvailabilityGenerationAsync(Guid campaignId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(Result<long?>.Success(null));
    }

    /// <summary>A Campaign table with no rows.</summary>
    private sealed class EmptyTable : ICampaignRepository
    {
        public Task<Campaign?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByPathAsync(string path, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<Campaign?> GetByNameAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult<Campaign?>(null);

        public Task<ListPageResult<Campaign>> ListAsync(
            Core.Workspaces.WorkspaceType? typeFilter,
            int? limit = null,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ListPageResult<Campaign>([], false));

        public Task<Result<Campaign>> AddAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<Campaign>.Success(campaign));

        public Task<Campaign> UpdateAsync(Campaign campaign, CancellationToken cancellationToken = default) =>
            Task.FromResult(campaign);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<int> CountAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
