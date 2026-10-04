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
public sealed class LexiconCorrectionConcurrencyTests(GrimoireFixture fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Two_corrections_of_one_predecessor_commit_exactly_once(bool protectedEntry)
    {
        await using CorrectionFixture owner = new(fixture);

        LexiconEntryDetail before = await owner.SeedAsync();

        if (protectedEntry)
        {
            await owner.ProtectAsync();

            before = await owner.ShowProtectedAsync();
        }

        await using ArcanumDbContext firstDb = fixture.CreateContext(owner.Path);

        await using ArcanumDbContext secondDb = fixture.CreateContext(owner.Path);

        ILexiconCurationService first = Create(firstDb);

        ILexiconCurationService second = Create(secondDb);

        BarrierRegistration firstGate = new();

        BarrierRegistration secondGate = new();

        await using CovenantWriteLease firstLease = new(firstGate);

        await using CovenantWriteLease secondLease = new(secondGate);

        Task<Result<LexiconCurationResult>> firstTask = Task.Run(() => first.CorrectAsync(before.Target, new("Person", ["beta", "gamma"]), firstLease));

        Task<Result<LexiconCurationResult>> secondTask = Task.Run(() => second.CorrectAsync(before.Target, new("Person", ["beta", "delta"]), secondLease));

        try
        {
            await Task.WhenAll(firstGate.Entered.Task, secondGate.Entered.Task).WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            firstGate.Release.TrySetResult();

            secondGate.Release.TrySetResult();
        }

        Result<LexiconCurationResult>[] results = await Task.WhenAll(firstTask, secondTask).WaitAsync(TimeSpan.FromSeconds(20));

        var winner = Assert.Single(results, result => result.IsSuccess).Value;

        Assert.Equal(ErrorCodes.Lexicon.StaleCurationTarget, Assert.Single(results, result => result.IsFailure).Error.Code);

        LexiconEntryDetail after = protectedEntry ? await owner.ShowProtectedAsync() : await owner.ShowAsync();

        Assert.Equal(2, after.CurationGeneration);

        Assert.Equal(winner.Entry.SnapshotDigest, after.SnapshotDigest);

        Assert.Equal(2, after.AnnalHistory.Length);

        Assert.Equal(3, after.HistoricalFactProvenance.Length);

        Assert.Single(after.Entry.FactProvenance!);

        Assert.Equal(1L, await owner.ScalarAsync("SELECT count(*) FROM annal_claims"));

        Assert.Equal(1L, await owner.ScalarAsync("SELECT count(*) FROM annal_dependencies"));

        Assert.Equal(protectedEntry ? 1L : 0L, await owner.ScalarAsync("SELECT count(*) FROM artifact_sensitivity"));
    }

    [Fact]
    public async Task Scribe_committing_after_inspection_makes_the_paused_correction_stale()
    {
        await using CorrectionFixture owner = new(fixture);

        LexiconEntryDetail before = await owner.SeedAsync();

        await using ArcanumDbContext correctionDb = fixture.CreateContext(owner.Path);

        ILexiconCurationService correction = Create(correctionDb);

        BarrierRegistration gate = new();

        await using CovenantWriteLease lease = new(gate);

        Task<Result<LexiconCurationResult>> correctionTask = Task.Run(() => correction.CorrectAsync(before.Target, new("Person", ["beta"]), lease));

        string[] snapshot;

        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));

            var scribed = await owner.Concrete.UpsertAsync("Entity", "general", ["scribed"], LexiconScope.Global);

            Assert.True(scribed.IsSuccess, scribed.Error.Message);

            snapshot = await owner.SnapshotAsync();
        }
        finally
        {
            gate.Release.TrySetResult();
        }

        var result = await correctionTask.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(ErrorCodes.Lexicon.StaleCurationTarget, result.Error.Code);

        Assert.Equal(snapshot, await owner.SnapshotAsync());

        Assert.Equal(["alpha", "beta", "scribed"], (await owner.ShowAsync()).Entry.Facts);
    }

    private static LexiconService Create(ArcanumDbContext db) => new(db, NullLogger<LexiconService>.Instance,
        new TestOptionsMonitor<ArcanumSettings>(new ArcanumSettings { Features = new FeatureSettings { Annals = false } }),
        MemoryErasureTestKeys.Isolated());

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
