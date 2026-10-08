using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Core.Weave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Data;

/// <summary>
/// What <see cref="MemoryScopeResolver"/> answers for one Session's binding, read from a real Grimoire.
/// </summary>
/// <remarks>
/// DESIGN §10.6 states the boundary the memory tools rely on: with Campaign scoping on, only a call bound
/// to no Session at all is refused, and a Session id with no Campaign binding - no binding row, or a
/// Global-only one - resolves to the installation-scoped memories alone rather than failing. The tools
/// refuse the first case themselves; this pins the resolver's half of the second.
/// </remarks>
public sealed class MemoryScopeResolverTests : IClassFixture<GrimoireFixture>, IAsyncLifetime
{
    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    static MemoryScopeResolverTests() => SqliteNativeRuntime.Instance.Initialize();

    public MemoryScopeResolverTests(GrimoireFixture fixture)
    {
        _fixture = fixture;
    }

    public Task InitializeAsync()
    {
        if (GrimoireFixture.SqlCipherAvailable)
        {
            _dbPath = _fixture.CopyDatabase();

            _db = _fixture.CreateContext(_dbPath);
        }

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
    public async Task A_session_id_with_no_binding_row_resolves_to_the_installation_scoped_memories_alone()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        MemoryScope scope = await CreateResolver(campaignScopedMemory: true)
            .ResolveForSessionAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(new MemoryScope(MemoryScopeKind.GlobalOnly, null), scope);

        Assert.Equal(LexiconScope.Global, scope.ToLexiconScope());
    }

    [SkippableFact]
    public async Task A_session_with_a_global_only_binding_resolves_to_the_installation_scoped_memories_alone()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid sessionId = await CreateSessionAsync(CanonicalCampaignContext.GlobalOnly);

        MemoryScope scope = await CreateResolver(campaignScopedMemory: true)
            .ResolveForSessionAsync(sessionId, CancellationToken.None);

        Assert.Equal(new MemoryScope(MemoryScopeKind.GlobalOnly, null), scope);
    }

    /// <summary>
    /// The control for the two cases above: a Session the repository bound to a Campaign resolves to that
    /// Campaign, so a resolver that answered Global-only for everything could not pass them all.
    /// </summary>
    [SkippableFact]
    public async Task A_session_bound_to_a_campaign_resolves_to_that_campaign()
    {
        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        Guid campaignId = Guid.NewGuid();

        await SeedCampaignRowAsync(campaignId);

        Guid sessionId = await SessionBindingWriters.BoundByTheRepositoryAsync(
            _db!,
            campaignId,
            CancellationToken.None);

        MemoryScope scope = await CreateResolver(campaignScopedMemory: true)
            .ResolveForSessionAsync(sessionId, CancellationToken.None);

        Assert.Equal(new MemoryScope(MemoryScopeKind.Campaign, campaignId), scope);

        Assert.Equal(LexiconScope.ForCampaign(campaignId), scope.ToLexiconScope());
    }

    private MemoryScopeResolver CreateResolver(bool campaignScopedMemory) =>
        new(
            _db!,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings
            {
                Features = new FeatureSettings { CampaignScopedMemory = campaignScopedMemory },
            }));

    private async Task<Guid> CreateSessionAsync(CanonicalCampaignContext campaign)
    {
        GrimoireRepository repository = new(
            _db!,
            new NoOpSessionAttachmentStore(),
            NullLogger<GrimoireRepository>.Instance,
            new TestOptionsSnapshot<ArcanumSettings>(new ArcanumSettings()),
            covenantKernel: null,
            availabilityRepublisher: null,
            FixtureOrdinaryConnectionFactory.For(_db!),
            FixtureLabeledArtifactGuard.For(_db!));

        Result<Guid> created = await repository.CreateBoundSessionAsync(
            campaign,
            "a new conversation",
            CancellationToken.None);

        Assert.True(created.IsSuccess, created.IsFailure ? created.Error.Message : string.Empty);

        return created.Value;
    }

    private async Task SeedCampaignRowAsync(Guid campaignId)
    {
        SqliteConnection connection = (SqliteConnection)_db!.Database.GetDbConnection();

        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO "Campaigns"
                ("Id", "Name", "NameLower", "Path", "Type", "Settings", "CreatedAt", "UpdatedAt")
            VALUES ($id, $name, $name, $path, 0, '{}', $at, $at);
            """;

        _ = command.Parameters.AddWithValue("$id", campaignId.ToString("D").ToUpperInvariant());

        _ = command.Parameters.AddWithValue("$name", campaignId.ToString("N"));

        _ = command.Parameters.AddWithValue("$path", $"/campaigns/{campaignId:N}");

        _ = command.Parameters.AddWithValue("$at", "2026-01-01T00:00:00.0000000+00:00");

        _ = await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
