using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.Conclave;

/// <summary>
/// The Apprentice lifecycle and the Archmage that mints it are the multi-agent coordination domain, so
/// they are declared in <c>RetroDownfall.Arcanum.Core.Conclave</c> rather than under the name of the
/// desktop application that happened to consume them first.
/// </summary>
/// <remarks>
/// Two invariants are pinned here because both are silent when broken. A type left behind in the old
/// namespace still compiles, because every consumer simply keeps its old <c>using</c>. And the eight
/// <see cref="ApprenticeStatus"/> names are the literal text persisted to <c>Apprentices.Status</c>:
/// renaming a member is a source-compatible edit that strands every row already written under the old
/// spelling, and no compiler diagnostic marks it.
/// </remarks>
[Collection("Grimoire")]
public sealed class ConclaveAgentTypeContractTests : IAsyncLifetime
{

    private const string ConclaveNamespace = "RetroDownfall.Arcanum.Core.Conclave";

    private const string TheForgeNamespace = "RetroDownfall.Arcanum.Core.TheForge";

    /// <summary>
    /// Every public type declared by the twelve agent files. Named rather than discovered, so that a
    /// type quietly dropped from the move fails the assertion instead of shrinking the sample.
    /// </summary>
    private static readonly Type[] AgentTypes =
    [
        typeof(Apprentice),

        typeof(ApprenticeCheckpoint),

        typeof(CreateApprenticeRequest),

        typeof(ReweaveApprenticeRequest),

        typeof(InterveneApprenticeRequest),

        typeof(CastApprenticeRequest),

        typeof(ApprenticeSummaryDto),

        typeof(ApprenticeDetailDto),

        typeof(ApprenticeEvent),

        typeof(ApprenticeEventType),

        typeof(StepFailureKind),

        typeof(ApprenticeExecutionPolicy),

        typeof(ApprenticePlanParser),

        typeof(ApprenticeStatus),

        typeof(IApprenticeRepository),

        typeof(IApprenticeRuntime),

        typeof(ConclaveCastRequest),

        typeof(IConclaveArchmage),

        typeof(PlanStep),
    ];

    private readonly GrimoireFixture _fixture;

    private string _dbPath = string.Empty;

    private ArcanumDbContext? _db;

    public ConclaveAgentTypeContractTests(GrimoireFixture fixture)
    {

        _fixture = fixture;

    }

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

    public static TheoryData<Type> AgentTypeData()
    {

        TheoryData<Type> data = [];

        foreach (Type type in AgentTypes)
        {

            data.Add(type);

        }

        return data;

    }

    [Theory]
    [MemberData(nameof(AgentTypeData))]
    public void Agent_type_declares_the_conclave_namespace(Type type)
    {

        Assert.Equal(ConclaveNamespace, type.Namespace);

    }

    [Fact]
    public void No_agent_type_remains_in_the_forge_namespace()
    {

        HashSet<string> agentTypeNames = [.. AgentTypes.Select(static type => type.Name)];

        Type[] strays = typeof(ApprenticeStatus).Assembly
            .GetTypes()
            .Where(type => string.Equals(type.Namespace, TheForgeNamespace, StringComparison.Ordinal))
            .Where(type => agentTypeNames.Contains(type.Name))
            .ToArray();

        Assert.Empty(strays);

    }

    /// <summary>
    /// The persisted spelling of every <see cref="ApprenticeStatus"/> member, asserted literally rather
    /// than through <c>ToString()</c> on both sides — which would compare a rename against itself.
    /// </summary>
    [Fact]
    public void Apprentice_status_names_are_the_values_persisted_to_the_status_column()
    {

        string[] persisted =
        [
            "Idle",

            "Planning",

            "Running",

            "Paused",

            "Completed",

            "Failed",

            "Cancelled",

            "Escalated",
        ];

        Assert.Equal(persisted, Enum.GetNames<ApprenticeStatus>());

        Assert.Equal(8, Enum.GetValues<ApprenticeStatus>().Length);

    }

    /// <summary>
    /// Proves the literals above are what actually reaches SQLite, by writing one Apprentice per status
    /// through the real repository and reading the <c>Status</c> column back as raw text. The mapping is
    /// a plain string column rather than a converted enum, so nothing but this round trip would catch a
    /// writer that started persisting the ordinal.
    /// </summary>
    [SkippableFact]
    public async Task Apprentice_status_values_round_trip_through_the_status_column()
    {

        Skip.IfNot(GrimoireFixture.SqlCipherAvailable, GrimoireFixture.SqlCipherUnavailableReason);

        ApprenticeRepository repository = new(_db!, NullLogger<ApprenticeRepository>.Instance);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        Dictionary<Guid, string> expected = [];

        foreach (ApprenticeStatus status in Enum.GetValues<ApprenticeStatus>())
        {

            Guid id = Guid.NewGuid();

            expected[id] = status.ToString();

            await repository.AddAsync(
                new Apprentice
                {
                    Id = id,
                    Name = $"Apprentice {status}",
                    Goal = "Persist a status",
                    Status = status.ToString(),
                    WorkspacePath = "/tmp/conclave",
                    CreatedAt = now,
                    UpdatedAt = now,
                },
                CancellationToken.None);

        }

        Dictionary<Guid, string> stored = [];

        await using (SqliteCommand read = ((SqliteConnection)_db!.Database.GetDbConnection()).CreateCommand())
        {

            // Read the column as raw text and key on the parsed Guid rather than filtering on a
            // formatted id: the text spelling EF writes for the key is not this assertion's subject,
            // and matching on it would turn a casing difference into a false pass on an empty result.
            read.CommandText = "SELECT Id, Status FROM Apprentices;";

            await using SqliteDataReader reader = await read.ExecuteReaderAsync(CancellationToken.None);

            while (await reader.ReadAsync(CancellationToken.None))
            {

                stored[Guid.Parse(reader.GetString(0))] = reader.GetString(1);

            }

        }

        foreach ((Guid id, string status) in expected)
        {

            Assert.True(stored.TryGetValue(id, out string? persisted), $"No Apprentices row for {id}.");

            Assert.Equal(status, persisted);

        }

    }

}
