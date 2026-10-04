using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Lexicon;
using RetroDownfall.Arcanum.Core.Primitives;
using RetroDownfall.Arcanum.Core.Storage;
using RetroDownfall.Arcanum.Infrastructure.Data;
using RetroDownfall.Arcanum.Tests.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;
using RetroDownfall.Arcanum.Infrastructure.Lexicon;
using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Lexicon;

[Collection("Grimoire")]
[Trait("Category", "Integration")]
public sealed class LexiconPinTests(GrimoireFixture fixture)
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Pin_and_unpin_change_only_pin_and_generation(bool capture, bool retired)
    {
        await using CorrectionFixture test = new(fixture, capture);

        LexiconEntryDetail before = await test.SeedAsync();

        if (retired)
        {
            before = (await test.Service.RetireAsync(before.Target, null)).Value.Entry;
        }

        string[] evidence = Evidence(await test.SnapshotAsync());

        var pinned = await test.Service.PinAsync(before.Target, null);

        Assert.True(pinned.IsSuccess, pinned.Error.Message);

        Assert.Equal(LexiconCurationOutcomeKind.Applied, pinned.Value.Outcome);

        LexiconEntryDetail after = pinned.Value.Entry;

        Assert.NotNull(after.Lifecycle.PinnedAtUtc);

        Assert.Equal(TimeSpan.Zero, after.Lifecycle.PinnedAtUtc.Value.Offset);

        Assert.Equal(UtcInstantText.Format(after.Lifecycle.PinnedAtUtc.Value), await test.ScalarAsync("SELECT PinnedAtUtc FROM lexicon_entries"));

        Assert.Equal(before.CurationGeneration + 1, after.CurationGeneration);

        AssertPreserved(before, after);

        Assert.Equal(evidence, Evidence(await test.SnapshotAsync()));

        Assert.Equal(retired ? 0L : 1L, await test.ScalarAsync("SELECT count(*) FROM lexicon_fts WHERE lexicon_fts MATCH 'alpha'"));

        var unpinned = await test.Service.UnpinAsync(after.Target, null);

        Assert.True(unpinned.IsSuccess, unpinned.Error.Message);

        Assert.Equal(LexiconCurationOutcomeKind.Applied, unpinned.Value.Outcome);

        Assert.Null(unpinned.Value.Entry.Lifecycle.PinnedAtUtc);

        Assert.Equal(before.CurationGeneration + 2, unpinned.Value.Entry.CurationGeneration);

        AssertPreserved(before, unpinned.Value.Entry);

        Assert.Equal(evidence, Evidence(await test.SnapshotAsync()));
    }

    [Fact]
    public async Task Desired_state_requires_exact_current_target_and_writes_nothing()
    {
        await using CorrectionFixture test = new(fixture, annals: true);

        LexiconEntryDetail before = await test.SeedAsync();

        string[] snapshot = await test.SnapshotAsync();

        var notPinned = await test.Service.UnpinAsync(before.Target, null);

        Assert.True(notPinned.IsSuccess, notPinned.Error.Message);

        Assert.Equal(LexiconCurationOutcomeKind.NotPinned, notPinned.Value.Outcome);

        Assert.Equal(snapshot, await test.SnapshotAsync());

        var pinned = await test.Service.PinAsync(before.Target, null);

        Assert.True(pinned.IsSuccess, pinned.Error.Message);

        snapshot = await test.SnapshotAsync();

        Assert.Equal(ErrorCodes.Lexicon.StaleCurationTarget, (await test.Service.PinAsync(before.Target, null)).Error.Code);

        Assert.Equal(LexiconCurationOutcomeKind.AlreadyPinned, (await test.Service.PinAsync(pinned.Value.Entry.Target, null)).Value.Outcome);

        Assert.Equal(snapshot, await test.SnapshotAsync());

        var unpinned = await test.Service.UnpinAsync(pinned.Value.Entry.Target, null);

        Assert.True(unpinned.IsSuccess, unpinned.Error.Message);

        snapshot = await test.SnapshotAsync();

        Assert.Equal(ErrorCodes.Lexicon.StaleCurationTarget, (await test.Service.UnpinAsync(pinned.Value.Entry.Target, null)).Error.Code);

        Assert.Equal(LexiconCurationOutcomeKind.NotPinned, (await test.Service.UnpinAsync(unpinned.Value.Entry.Target, null)).Value.Outcome);

        Assert.Equal(snapshot, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Protected_pin_changes_require_exact_scope_lease_and_preserve_all_evidence(bool unpin)
    {
        await using CorrectionFixture test = new(fixture);

        await test.SeedAsync();

        await test.ProtectAsync(withHead: true);

        LexiconEntryDetail before = await test.ShowProtectedAsync();

        using LeaseRegistration registration = new(CovenantLeaseKind.Write);

        await using CovenantWriteLease lease = new(registration);

        if (unpin)
        {
            var pin = await test.Service.PinAsync(before.Target, lease);

            Assert.True(pin.IsSuccess, pin.Error.Message);

            before = pin.Value.Entry;
        }

        string[] snapshot = await test.SnapshotAsync();

        var refused = unpin ? await test.Service.UnpinAsync(before.Target, null) : await test.Service.PinAsync(before.Target, null);

        Assert.Equal(ErrorCodes.Lexicon.ProtectedMutationRefused, refused.Error.Code);

        Assert.Equal(snapshot, await test.SnapshotAsync());

        var result = unpin ? await test.Service.UnpinAsync(before.Target, lease) : await test.Service.PinAsync(before.Target, lease);

        Assert.True(result.IsSuccess, result.Error.Message);

        AssertPreserved(before, result.Value.Entry);

        Assert.Equal(Evidence(snapshot), Evidence(await test.SnapshotAsync()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revocation_before_commit_rolls_back_pin_change(bool unpin)
    {
        await using CorrectionFixture test = new(fixture);

        LexiconEntryDetail before = await test.SeedAsync();

        if (unpin)
        {
            var pin = await test.Service.PinAsync(before.Target, null);

            Assert.True(pin.IsSuccess, pin.Error.Message);

            before = pin.Value.Entry;
        }

        string[] snapshot = await test.SnapshotAsync();

        using LeaseRegistration registration = new(CovenantLeaseKind.Write) { StaleAfter = 1 };

        await using CovenantWriteLease lease = new(registration);

        var result = unpin ? await test.Service.UnpinAsync(before.Target, lease) : await test.Service.PinAsync(before.Target, lease);

        Assert.Equal(ErrorCodes.Covenant.StaleSnapshot, result.Error.Code);

        Assert.Equal(2, registration.Revalidations);

        Assert.Equal(snapshot, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Exhausted_generation_refuses_changes_and_desired_states(bool pinned, bool unpin)
    {
        await using CorrectionFixture test = new(fixture);

        await test.SeedAsync();

        await test.ExecuteAsync(pinned
            ? "UPDATE lexicon_entries SET CurationGeneration = 9223372036854775807, PinnedAtUtc = '2026-09-01T00:00:00.0000000Z'"
            : "UPDATE lexicon_entries SET CurationGeneration = 9223372036854775807");

        LexiconEntryDetail before = await test.ShowAsync();

        string[] snapshot = await test.SnapshotAsync();

        var result = unpin ? await test.Service.UnpinAsync(before.Target, null) : await test.Service.PinAsync(before.Target, null);

        Assert.Equal(ErrorCodes.Lexicon.CurationGenerationExhausted, result.Error.Code);

        Assert.Equal(snapshot, await test.SnapshotAsync());
    }

    [Theory]
    [InlineData("scope")]
    [InlineData("identity")]
    [InlineData("generation")]
    [InlineData("digest")]
    [InlineData("lifecycle")]
    [InlineData("head")]
    [InlineData("label")]
    public async Task Unpin_compares_every_target_component_before_desired_state(string field)
    {
        await using CorrectionFixture test = new(fixture);

        await test.SeedAsync();

        await test.ProtectAsync(withHead: true);

        LexiconEntryDetail before = await test.ShowProtectedAsync();

        LexiconCurationTarget target = field switch
        {
            "scope" => before.Target with { Scope = new(LexiconScopeKind.Campaign, Guid.NewGuid()) },
            "identity" => before.Target with { EntryId = Guid.NewGuid() },
            "generation" => before.Target with { CurationGeneration = 2 },
            "digest" => before.Target with { SnapshotDigest = new string('A', 64) },
            "lifecycle" => before.Target with { Lifecycle = before.Lifecycle with { PinnedAtUtc = DateTimeOffset.UtcNow } },
            "head" => before.Target with { AnnalHead = before.Target.AnnalHead with { VersionId = "other" } },
            _ => before.Target with { SensitivityLabel = before.Target.SensitivityLabel with { ArtifactRevision = 2 } },
        };

        using LeaseRegistration registration = new(CovenantLeaseKind.Write)
        {
            Snapshot = new(Guid.NewGuid(), 1, CovenantLeaseKind.Write, CovenantLeaseCoverage.Scoped,
                field == "scope" ? CovenantOperationScope.ForCampaign(target.Scope.CampaignId!.Value) : CovenantOperationScope.Global,
                null, 1, 1, 0, null, null, null, null, null, false),
        };

        await using CovenantWriteLease lease = new(registration);

        string[] snapshot = await test.SnapshotAsync();

        var result = await test.Service.UnpinAsync(target, lease);

        Assert.Equal(ErrorCodes.Lexicon.StaleCurationTarget, result.Error.Code);

        Assert.Equal(snapshot, await test.SnapshotAsync());
    }

    [Fact]
    public async Task Pin_does_not_block_scribe_correction_retirement_reinstatement_or_hard_delete()
    {
        await using CorrectionFixture test = new(fixture, annals: true);

        LexiconEntryDetail before = await test.SeedAsync();

        var pinned = await test.Service.PinAsync(before.Target, null);

        Assert.True(pinned.IsSuccess, pinned.Error.Message);

        var scribed = await test.Concrete.UpsertAsync("Entity", "Person", ["gamma"], LexiconScope.Global);

        Assert.True(scribed.IsSuccess, scribed.Error.Message);

        before = await test.ShowAsync();

        var corrected = await test.Service.CorrectAsync(before.Target, new("Person", ["delta"]), null);

        Assert.True(corrected.IsSuccess, corrected.Error.Message);

        var retired = await test.Service.RetireAsync(corrected.Value.Entry.Target, null);

        Assert.True(retired.IsSuccess, retired.Error.Message);

        var reinstated = await test.Service.ReinstateAsync(retired.Value.Entry.Target, null);

        Assert.True(reinstated.IsSuccess, reinstated.Error.Message);

        Assert.Equal(pinned.Value.Entry.Lifecycle.PinnedAtUtc, reinstated.Value.Entry.Lifecycle.PinnedAtUtc);

        var deleted = await test.Concrete.DeleteByNameAsync("Entity", LexiconScope.Global);

        Assert.True(deleted.IsSuccess, deleted.Error.Message);

        Assert.True(deleted.Value);

        Assert.Empty(await test.SnapshotAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Opposite_writer_with_predecessor_target_cannot_undo_committed_pin_change(bool initiallyPinned)
    {
        await using CorrectionFixture owner = new(fixture, annals: true);

        LexiconEntryDetail before = await owner.SeedAsync();

        if (initiallyPinned)
        {
            before = (await owner.Service.PinAsync(before.Target, null)).Value.Entry;
        }

        await using ArcanumDbContext other = fixture.CreateContext(owner.Path);

        ILexiconCurationService staleWriter = new LexiconService(other, NullLogger<LexiconService>.Instance,
            new TestOptionsMonitor<Core.Configuration.ArcanumSettings>(owner.Settings),
            MemoryErasureTestKeys.Isolated());

        PinBarrierRegistration barrier = new();

        await using CovenantWriteLease lease = new(barrier);

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));

        Task<Result<LexiconCurationResult>> stale = initiallyPinned
            ? staleWriter.PinAsync(before.Target, lease, timeout.Token)
            : staleWriter.UnpinAsync(before.Target, lease, timeout.Token);

        string[] committed;

        try
        {
            await barrier.Entered.Task.WaitAsync(timeout.Token);

            var result = initiallyPinned
                ? await owner.Service.UnpinAsync(before.Target, null, timeout.Token)
                : await owner.Service.PinAsync(before.Target, null, timeout.Token);

            Assert.True(result.IsSuccess, result.Error.Message);

            committed = await owner.SnapshotAsync();
        }
        finally
        {
            barrier.Release.TrySetResult();
        }

        Assert.Equal(ErrorCodes.Lexicon.StaleCurationTarget, (await stale.WaitAsync(timeout.Token)).Error.Code);

        Assert.Equal(committed, await owner.SnapshotAsync());

        LexiconEntryDetail after = await owner.ShowAsync();

        Assert.Equal(before.CurationGeneration + 1, after.CurationGeneration);

        Assert.Equal(!initiallyPinned, after.Lifecycle.PinnedAtUtc is not null);

        AssertPreserved(before, after);
    }

    private sealed class PinBarrierRegistration : ICovenantLeaseRegistration
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CovenantOperationLeaseSnapshot Snapshot { get; } = new(Guid.NewGuid(), 1, CovenantLeaseKind.Write,
            CovenantLeaseCoverage.Scoped, CovenantOperationScope.Global, null, 1, 1, 0, null, null, null, null, null, false);

        public CancellationToken Revocation => CancellationToken.None;

        public async ValueTask<Result> RevalidateAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();

            await Release.Task.WaitAsync(cancellationToken);

            return Result.Success();
        }

        public ValueTask ReleaseAsync() => ValueTask.CompletedTask;
    }

    private static string[] Evidence(string[] snapshot) =>
        snapshot.Where(row => !row.StartsWith("lexicon_entries:", StringComparison.Ordinal)).ToArray();

    private static void AssertPreserved(LexiconEntryDetail before, LexiconEntryDetail after)
    {
        Assert.Equal(before.Entry.UpdatedAt, after.Entry.UpdatedAt);

        Assert.Equal(before.Entry.Type, after.Entry.Type);

        Assert.Equal(before.Entry.Facts, after.Entry.Facts);

        Assert.Equal(before.Entry.FactProvenance, after.Entry.FactProvenance);

        Assert.Equal(before.Lifecycle.RetiredAtUtc, after.Lifecycle.RetiredAtUtc);

        Assert.Equal(before.SnapshotDigest, after.SnapshotDigest);

        Assert.Equal(before.Target.AnnalHead, after.Target.AnnalHead);

        Assert.Equal(before.Target.SensitivityLabel, after.Target.SensitivityLabel);
    }
}
