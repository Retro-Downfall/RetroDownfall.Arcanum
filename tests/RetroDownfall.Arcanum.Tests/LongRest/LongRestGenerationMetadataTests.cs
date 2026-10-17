using RetroDownfall.Arcanum.Core.LongRest;
using RetroDownfall.Arcanum.Infrastructure.Data.LongRest;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

[Collection("Grimoire")]
public sealed class LongRestGenerationMetadataTests
{
    [Fact]
    public async Task Exact_generation_metadata_retains_original_coordinates_without_reading_source_or_writing()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        await h.SeedAsync("subject", "source text");

        LongRestDiscoveryGeneration generation = await h.CaptureAsync();

        LongRestSweepStore sweep = new(h.Saga.Context, TimeProvider.System);

        LongRestSweepHarness.Require(await sweep.BeginOrResumeAsync(generation.OperationId, generation.Scope, generation.Fence.Configuration, CancellationToken.None));

        // Generation metadata is deliberately independent of the protected/plaintext leaf lane.
        await h.ExecuteAsync("DROP TRIGGER long_rest_discovery_leaves_guard_update; UPDATE long_rest_discovery_leaves SET SnapshotJson = '{invalid}';");

        long before = (long)(await h.ScalarAsync("SELECT total_changes();"))!;

        var found = await h.Store.ReadGenerationMetadataAsync(generation.OperationId, CancellationToken.None);

        Assert.True(found.IsSuccess);

        Assert.Equal(generation, found.Value);

        Assert.Equal(before, await h.ScalarAsync("SELECT total_changes();"));

        Assert.Equal(0L, await h.ScalarAsync("SELECT count(*) FROM long_rest_provider_effects;"));
    }

    [Fact]
    public async Task Exact_not_started_operation_returns_null_without_writes()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        Guid operation = Guid.NewGuid();

        await h.ExecuteAsync("INSERT INTO LongRunningOperations (Id, Kind, State, RecoveryPolicy, CreatedAt, PublicSummary) VALUES ($id, 'long-rest-sweep', 1, 1, '2026-10-10T00:00:00.0000000Z', 'sweep');", ("$id", operation.ToString("N")));

        long before = (long)(await h.ScalarAsync("SELECT total_changes();"))!;

        var found = await h.Store.ReadGenerationMetadataAsync(operation, CancellationToken.None);

        Assert.True(found.IsSuccess);

        Assert.Null(found.Value);

        Assert.Equal(before, await h.ScalarAsync("SELECT total_changes();"));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("wrong-kind")]
    [InlineData("missing-sweep")]
    [InlineData("scope")]
    [InlineData("generation")]
    [InlineData("fence")]
    public async Task Invalid_or_mismatched_ownership_never_returns_generation_metadata(string change)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        LongRestDiscoveryGeneration generation = await h.StartAsync();

        LongRestSweepStore sweep = new(h.Saga.Context, TimeProvider.System);

        LongRestSweepHarness.Require(await sweep.BeginOrResumeAsync(generation.OperationId, generation.Scope, generation.Fence.Configuration, CancellationToken.None));

        switch (change)
        {
            case "wrong-kind": await h.ExecuteAsync("UPDATE LongRunningOperations SET Kind = 'other';"); break;
            case "missing-sweep": await h.ExecuteAsync("DELETE FROM long_rest_sweeps;"); break;
            case "scope": await h.ExecuteAsync("UPDATE long_rest_sweeps SET ScopeKindCode = 2, CampaignId = '22222222-2222-4222-8222-222222222222';"); break;
            case "generation": await h.ExecuteAsync("PRAGMA foreign_keys = OFF; UPDATE long_rest_sweeps SET GenerationId = '22222222-2222-4222-8222-222222222222';"); break;
            case "fence": await h.ExecuteAsync("UPDATE long_rest_discovery_generations SET FenceJson = '{}';"); break;
        }

        Guid operation = change == "empty" ? Guid.Empty : change == "unknown" ? Guid.NewGuid() : generation.OperationId;

        long before = (long)(await h.ScalarAsync("SELECT total_changes();"))!;

        Assert.True((await h.Store.ReadGenerationMetadataAsync(operation, CancellationToken.None)).IsFailure);

        Assert.Equal(before, await h.ScalarAsync("SELECT total_changes();"));
    }
}
