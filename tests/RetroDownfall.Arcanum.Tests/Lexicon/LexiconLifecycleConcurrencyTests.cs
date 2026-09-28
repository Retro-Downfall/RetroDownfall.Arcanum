using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Core.Configuration;
using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Tests.Fixtures;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Lexicon;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LexiconLifecycleConcurrencyTests(GrimoireFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_lifecycle_writers_of_one_predecessor_commit_exactly_once(bool reinstate)
    {
        await using CorrectionFixture owner = new(fixture);

        LexiconEntryDetail before = await owner.SeedAsync();

        if (reinstate)
        {
            var retired = await owner.Service.RetireAsync(before.Target, null);

            Assert.True(retired.IsSuccess, retired.Error.Message);

            before = retired.Value.Entry;
        }

        await using ArcanumDbContext firstDb = fixture.CreateContext(owner.Path);

        await using ArcanumDbContext secondDb = fixture.CreateContext(owner.Path);

        ILexiconCurationService first = Create(firstDb);

        ILexiconCurationService second = Create(secondDb);

        BarrierRegistration firstGate = new();

        BarrierRegistration secondGate = new();

        await using CovenantWriteLease firstLease = new(firstGate);

        await using CovenantWriteLease secondLease = new(secondGate);

        Task<Result<LexiconCurationResult>> firstTask = Task.Run(() => reinstate ? first.ReinstateAsync(before.Target, firstLease) : first.RetireAsync(before.Target, firstLease));

        Task<Result<LexiconCurationResult>> secondTask = Task.Run(() => reinstate ? second.ReinstateAsync(before.Target, secondLease) : second.RetireAsync(before.Target, secondLease));

        try
        {
            await Task.WhenAll(firstGate.Entered.Task, secondGate.Entered.Task).WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            firstGate.Release.TrySetResult();

            secondGate.Release.TrySetResult();
        }

        var results = await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Single(results, result => result.IsSuccess);

        Assert.Equal(ErrorCodes.Lexicon.StaleCurationTarget, Assert.Single(results, result => result.IsFailure).Error.Code);

        LexiconEntryDetail after = await owner.ShowAsync();

        Assert.Equal(before.CurationGeneration + 1, after.CurationGeneration);

        Assert.Equal(reinstate ? 3 : 2, after.AnnalHistory.Length);

        Assert.Equal(reinstate ? 1L : 0L, await owner.ScalarAsync("SELECT count(*) FROM lexicon_fts WHERE lexicon_fts MATCH 'alpha'"));
    }

    [Fact]
    public async Task Scribe_committing_after_inspection_makes_the_paused_retirement_stale()
    {
        await using CorrectionFixture owner = new(fixture);

        LexiconEntryDetail before = await owner.SeedAsync();

        await using ArcanumDbContext lifecycleDb = fixture.CreateContext(owner.Path);

        ILexiconCurationService lifecycle = Create(lifecycleDb);

        BarrierRegistration gate = new();

        await using CovenantWriteLease lease = new(gate);

        Task<Result<LexiconCurationResult>> retiring = Task.Run(() => lifecycle.RetireAsync(before.Target, lease));

        string[] snapshot;

        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True((await owner.Concrete.UpsertAsync("Entity", "Person", ["gamma"], LexiconScope.Global)).IsSuccess);

            snapshot = await owner.SnapshotAsync();
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        Assert.Equal(ErrorCodes.Lexicon.StaleCurationTarget, (await retiring.WaitAsync(TimeSpan.FromSeconds(20))).Error.Code);

        Assert.Equal(snapshot, await owner.SnapshotAsync());

        Assert.Null((await owner.ShowAsync()).Lifecycle.RetiredAtUtc);
    }

    [Fact]
    public async Task Retire_and_reinstate_from_one_active_predecessor_cannot_undo_a_committed_retirement()
    {
        await using CorrectionFixture owner = new(fixture);

        LexiconEntryDetail before = await owner.SeedAsync();

        await using ArcanumDbContext reinstateDb = fixture.CreateContext(owner.Path);

        ILexiconCurationService lifecycle = Create(reinstateDb);

        BarrierRegistration gate = new();

        await using CovenantWriteLease lease = new(gate);

        Task<Result<LexiconCurationResult>> reinstating = Task.Run(() => lifecycle.ReinstateAsync(before.Target, lease));

        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True((await owner.Service.RetireAsync(before.Target, null)).IsSuccess);
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        Assert.Equal(ErrorCodes.Lexicon.StaleCurationTarget, (await reinstating.WaitAsync(TimeSpan.FromSeconds(20))).Error.Code);

        LexiconEntryDetail after = await owner.ShowAsync();

        Assert.NotNull(after.Lifecycle.RetiredAtUtc);

        Assert.Equal(2, after.CurationGeneration);

        Assert.Equal(2, after.AnnalHistory.Length);
    }

    private static LexiconService Create(ArcanumDbContext db) => new(db, NullLogger<LexiconService>.Instance,
        new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Features = new FeatureSettings { Annals = false } }));

    private sealed class BarrierRegistration : ICovenantLeaseRegistration
    {
        private int _calls;

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(Guid.NewGuid(), 1, CovenantLeaseKind.Write,
            CovenantLeaseCoverage.Scoped, CovenantOperationScope.Global, null, 1, 1, 0, null, null, null, null, null, false);

        public CancellationToken Revocation => CancellationToken.None;

        public async ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                Entered.TrySetResult();

                await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }

            return Result.Success();
        }

        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;
    }
}
