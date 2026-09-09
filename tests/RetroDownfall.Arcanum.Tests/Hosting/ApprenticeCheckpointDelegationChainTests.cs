using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Intelligence;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Core.Storage.Entities;
using RetroDownfall.Arcanum.Core.Conclave;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Hosting;
using RetroDownfall.Arcanum.Infrastructure.Repositories;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Hosting;

/// <summary>
/// <see cref="ApprenticeCheckpoint.DelegationChain"/> is the only durable carrier of the A2A delegation
/// chain, and <c>ConclaveDelegationChain.ContainsSelf</c> at the receiving peer is the sole cycle guard
/// (issue #55 forbids a hop ceiling). Every <see cref="ApprenticeService"/> checkpoint rewrite must
/// therefore carry the chain forward, or an Apprentice spawned by an inbound Sending goes loop-blind
/// from its second step onward.
/// </summary>
[Collection("ApprenticeReliability")]
public sealed class ApprenticeCheckpointDelegationChainTests
{
    private static readonly string[] InboundChain = ["origin-node-a", "this-node-b"];

    [Fact]
    public async Task InterveneAsync_PreservesDelegationChain()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = DelegatedApprentice(apprenticeId, ApprenticeStatus.Escalated);

        RecordingApprenticeRepository repo = new(apprentice);

        ApprenticeService service = CreateService(repo);

        Result<string> result = await service.InterveneAsync(
            apprenticeId,
            "Try the other approach.",
            resume: false,
            CancellationToken.None);

        Assert.True(result.IsSuccess);

        Assert.Equal(InboundChain, ChainOf(repo.Get(apprenticeId)));
    }

    [Fact]
    public async Task CompleteStepAsync_PreservesDelegationChain()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = DelegatedApprentice(apprenticeId, ApprenticeStatus.Running);

        RecordingApprenticeRepository repo = new(apprentice);

        await InvokeCompleteStepAsync(CreateService(repo), repo, apprentice, apprenticeId);

        Assert.Equal(InboundChain, ChainOf(repo.Get(apprenticeId)));
    }

    /// <summary>
    /// The general contract behind the chain loss: a rewrite forwards every member it does not deliberately
    /// change. Reflecting over the record instead of naming members means a property added to
    /// <see cref="ApprenticeCheckpoint"/> later fails here unless the rewrite paths carry it.
    /// </summary>
    [Fact]
    public async Task CompleteStepAsync_ForwardsEveryCheckpointMemberItDoesNotDeliberatelyChange()
    {
        string[] deliberatelyChanged = ["CurrentStep", "Timestamp", "DmGuidance"];

        Guid apprenticeId = Guid.NewGuid();

        ApprenticeCheckpoint seeded = new()
        {
            CurrentStep = 0,
            ConversationSummary = "summary of the turns so far",
            CompletedToolCallIds = ["tool-call-1", "tool-call-2"],
            Timestamp = DateTimeOffset.UtcNow.AddMinutes(-5),
            EscalationReason = "an earlier escalation",
            DmGuidance = "guidance the completed step consumed",
            ParentApprenticeId = Guid.NewGuid(),
            DelegationChain = InboundChain,
        };
        Apprentice apprentice = DelegatedApprentice(apprenticeId, ApprenticeStatus.Running);

        apprentice.CheckpointData = ApprenticeRepository.SerializeCheckpoint(seeded);

        RecordingApprenticeRepository repo = new(apprentice);

        await InvokeCompleteStepAsync(CreateService(repo), repo, apprentice, apprenticeId);

        ApprenticeCheckpoint? rewritten =
            ApprenticeRepository.DeserializeCheckpoint(repo.Get(apprenticeId).CheckpointData);

        Assert.NotNull(rewritten);

        PropertyInfo[] properties = typeof(ApprenticeCheckpoint).GetProperties();

        Assert.NotEmpty(properties);

        foreach (string name in deliberatelyChanged)
        {
            Assert.Contains(properties, property => property.Name == name);
        }

        foreach (PropertyInfo property in properties.Where(p => !deliberatelyChanged.Contains(p.Name)))
        {
            Assert.True(
                ValuesMatch(property.GetValue(seeded), property.GetValue(rewritten)),
                $"CompleteStepAsync dropped ApprenticeCheckpoint.{property.Name}.");
        }
    }

    [Fact]
    public async Task EscalateAsync_PreservesDelegationChain()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = DelegatedApprentice(apprenticeId, ApprenticeStatus.Running);

        RecordingApprenticeRepository repo = new(apprentice);

        ApprenticeService service = CreateService(repo);

        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod("EscalateAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        Task task = (Task)method!.Invoke(
            service,
            [
                repo,
                apprentice,
                apprenticeId,
                0,
                "needs a Dungeon Master",
                false,
                CancellationToken.None,
            ])!;

        await task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(InboundChain, ChainOf(repo.Get(apprenticeId)));
    }

    [Fact]
    public async Task ResumeCrashRecoveryAsync_PreservesDelegationChainWhileEscalatingInterruptedPlanning()
    {
        Guid apprenticeId = Guid.NewGuid();

        Apprentice apprentice = DelegatedApprentice(apprenticeId, ApprenticeStatus.Planning);

        RecordingApprenticeRepository repo = new(apprentice);

        ApprenticeService service = CreateService(repo);

        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod("ResumeCrashRecoveryAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        Task task = (Task)method!.Invoke(service, new object?[] { CancellationToken.None })!;

        await task.WaitAsync(TimeSpan.FromSeconds(15));

        Apprentice persisted = repo.Get(apprenticeId);

        Assert.Equal(ApprenticeStatus.Escalated.ToString(), persisted.Status);

        Assert.Equal(InboundChain, ChainOf(persisted));
    }

    /// <summary>
    /// A locally cast child of a delegated Apprentice receives the caller's lineage atomically with
    /// creation, before any later launch or settlement can observe it.
    /// </summary>
    [Fact]
    public async Task CastAsync_PersistsTheParentDelegationChainAtCreation()
    {
        Guid parentId = Guid.NewGuid();

        RecordingApprenticeRepository repo = new();
        ConclaveArchmage archmage = CreateArchmage(repo);

        Result<Apprentice> cast = await archmage.CastAsync(new ConclaveCastRequest(
            Goal: "locally cast child",
            Name: "child",
            WorkspacePath: "/tmp/arcanum-test",
            ParentApprenticeId: parentId,
            DelegationChain: InboundChain,
            LaunchRequested: true));

        Assert.True(cast.IsSuccess, cast.Error.Message);

        Apprentice persisted = repo.Get(cast.Value.Id);

        Assert.Equal(parentId, persisted.ParentApprenticeId);

        Assert.Equal(InboundChain, ChainOf(persisted));

        Assert.True(ApprenticeRepository.DeserializeCheckpoint(persisted.CheckpointData)?.LaunchRequested);
    }

    /// <summary>
    /// A child cast by an Apprentice that is not itself delegated stays purely local: no chain is
    /// invented, so a later dispatch is not refused by a peer that never saw this work.
    /// </summary>
    [Fact]
    public async Task CastAsync_LeavesTheChildChainEmptyForPurelyLocalWork()
    {
        Guid parentId = Guid.NewGuid();
        RecordingApprenticeRepository repo = new();
        ConclaveArchmage archmage = CreateArchmage(repo);

        Result<Apprentice> cast = await archmage.CastAsync(new ConclaveCastRequest(
            Goal: "locally cast child",
            Name: "local-child",
            WorkspacePath: "/tmp/arcanum-test",
            ParentApprenticeId: parentId,
            LaunchRequested: true));

        Assert.True(cast.IsSuccess, cast.Error.Message);
        Assert.Null(ChainOf(repo.Get(cast.Value.Id)));
    }

    private static async Task InvokeCompleteStepAsync(
        ApprenticeService service,
        IApprenticeRepository repo,
        Apprentice apprentice,
        Guid apprenticeId)
    {
        MethodInfo? method = typeof(ApprenticeService)
            .GetMethod("CompleteStepAsync", BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.NotNull(method);

        Task task = (Task)method!.Invoke(
            service,
            [
                repo,
                apprentice,
                ApprenticeRepository.DeserializePlan(apprentice.Plan),
                0,
                "step one done",
                12L,
                apprenticeId,
                CancellationToken.None,
            ])!;

        await task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static bool ValuesMatch(object? seeded, object? rewritten)
    {
        if (seeded is System.Collections.IEnumerable left and not string
            && rewritten is System.Collections.IEnumerable right)
        {
            return left.Cast<object>().SequenceEqual(right.Cast<object>());
        }

        return Equals(seeded, rewritten);
    }

    private static IReadOnlyList<string>? ChainOf(Apprentice apprentice) =>
        ApprenticeRepository.DeserializeCheckpoint(apprentice.CheckpointData)?.DelegationChain;

    private static Apprentice DelegatedApprentice(Guid apprenticeId, ApprenticeStatus status) =>
        new()
        {
            Id = apprenticeId,
            Name = "delegated",
            Goal = "work delegated over A2A",
            WorkspacePath = "/tmp/arcanum-test",
            Status = status.ToString(),
            Plan = ApprenticeRepository.SerializePlan(
            [
                new PlanStep { Index = 1, Description = "First step" },
                new PlanStep { Index = 2, Description = "Second step" },
            ]),
            CurrentStep = 0,
            SessionId = Guid.NewGuid(),
            CheckpointData = ApprenticeRepository.SerializeCheckpoint(new ApprenticeCheckpoint
            {
                CurrentStep = 0,
                Timestamp = DateTimeOffset.UtcNow,
                DelegationChain = InboundChain,
            }),
        };
    private static ApprenticeService CreateService(
        IApprenticeRepository repo,
        bool apprenticesEnabled = true)
    {
        ServiceCollection services = new();

        services.AddSingleton(repo);

        IServiceScopeFactory scopeFactory = services
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

        ArcanumSettings settings = new()
        {
            Features = new FeatureSettings { Apprentices = apprenticesEnabled },
        };
        return new ApprenticeService(
            scopeFactory,
            new TestOptionsMonitor<ArcanumSettings>(settings),
            new ChronicleHub(),
            NullLogger<ApprenticeService>.Instance,
            new GrimoireConnectionAdmissionGate(TimeProvider.System));
    }

    private static ConclaveArchmage CreateArchmage(IApprenticeRepository repository) =>
        new(
            repository,
            new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings
            {
                Features = new FeatureSettings { Conclave = true },
            }));

    private sealed class RecordingApprenticeRepository : IApprenticeRepository
    {
        private readonly Dictionary<Guid, Apprentice> _store = new();

        public RecordingApprenticeRepository(params Apprentice[] apprentices)
        {
            foreach (Apprentice apprentice in apprentices)
            {
                _store[apprentice.Id] = apprentice;
            }
        }

        public Apprentice Get(Guid id) => _store[id];

        public Task<Apprentice> AddAsync(Apprentice apprentice, CancellationToken cancellationToken = default)
        {
            _store[apprentice.Id] = apprentice;

            return Task.FromResult(apprentice);
        }

        public Task<Apprentice?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_store.TryGetValue(id, out Apprentice? found) ? found : null);

        public Task<Apprentice> UpdateAsync(Apprentice apprentice, CancellationToken cancellationToken = default)
        {
            _store[apprentice.Id] = apprentice;

            return Task.FromResult(apprentice);
        }

        public Task<IReadOnlyList<Apprentice>> GetResumableAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Apprentice>>([]);

        public Task<IReadOnlyList<Apprentice>> GetInterruptedPlanningAsync(CancellationToken cancellationToken = default)
        {
            string planning = ApprenticeStatus.Planning.ToString();

            IReadOnlyList<Apprentice> interrupted = _store.Values
                .Where(a => string.Equals(a.Status, planning, StringComparison.Ordinal))
                .ToList();

            return Task.FromResult(interrupted);
        }

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_store.Remove(id));

        public Task<ListPageResult<Apprentice>> ListAsync(
            Guid? campaignId,
            string? status,
            int? limit = null,
            DateTimeOffset? beforeUpdatedAt = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
