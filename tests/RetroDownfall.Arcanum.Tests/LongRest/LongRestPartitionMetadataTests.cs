using RetroDownfall.Arcanum.Core.Covenant;
using RetroDownfall.Arcanum.Core.Tower;
using RetroDownfall.Arcanum.Tests.Fixtures;

namespace RetroDownfall.Arcanum.Tests.LongRest;

[Collection("Grimoire")]
public sealed class LongRestPartitionMetadataTests
{
    [Theory]
    [InlineData("source-alias")]
    [InlineData("zero-generation")]
    [InlineData("duplicate-generation")]
    [InlineData("unsorted-generation")]
    public async Task Partition_metadata_rejects_ambiguous_source_identity_and_invalid_provenance(string corruption)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        Guid memory = Guid.NewGuid();

        await h.SeedAsync(memory.ToString("D"), "protected observation");

        await h.ProtectAsync(memory, GenerationProvenance.CreateExact([Guid.NewGuid()]));

        if (corruption == "source-alias")
        {
            await h.SeedAsync(memory.ToString("D").ToUpperInvariant(), "alias observation");
        }
        else
        {
            await h.ExecuteAsync("DROP TRIGGER artifact_sensitivity_guard_update;");

            await h.ExecuteAsync("PRAGMA ignore_check_constraints = ON;");

            byte[] one = Guid.Parse("11111111-1111-1111-1111-111111111111").ToByteArray(bigEndian: true);

            byte[] two = Guid.Parse("22222222-2222-2222-2222-222222222222").ToByteArray(bigEndian: true);

            byte[] bytes = corruption switch
            {
                "duplicate-generation" => [..one, ..one],
                "unsorted-generation" => [..two, ..one],
                _ => new byte[16],
            };

            await h.ExecuteAsync("UPDATE artifact_sensitivity SET ExactGenerationIds = $bytes;", ("$bytes", bytes));
        }

        long before = (long)(await h.ScalarAsync("SELECT total_changes();"))!;

        Assert.True((await h.Store.ReadNextProtectedPartitionAsync(CanonicalCampaignContext.GlobalOnly, null, CancellationToken.None)).IsFailure);

        Assert.Equal(before, await h.ScalarAsync("SELECT total_changes();"));
    }

    [Fact]
    public async Task Protected_partition_metadata_is_strictly_paged_and_keeps_clean_and_other_scope_separate()
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        List<CovenantDigest> expected = [];

        for (int index = 0; index < 3; index++)
        {
            Guid memory = Guid.NewGuid();

            await h.SeedAsync(memory.ToString("D"), "protected observation");

            expected.Add(await h.ProtectAsync(memory, GenerationProvenance.CreateExact([Guid.NewGuid()])));
        }

        await h.SeedAsync("clean", "clean observation");

        Guid other = Guid.NewGuid();

        await h.SeedAsync(other.ToString("D"), "another Campaign", await h.SessionAsync(Guid.NewGuid()));

        await h.ProtectAsync(other, GenerationProvenance.CreateExact([Guid.NewGuid()]));

        expected.Sort((left, right) => string.CompareOrdinal(left.ToString(), right.ToString()));

        long before = (long)(await h.ScalarAsync("SELECT total_changes();"))!;

        CovenantDigest? cursor = null;

        foreach (CovenantDigest digest in expected)
        {
            cursor = LongRestSweepHarness.Require(await h.Store.ReadNextProtectedPartitionAsync(CanonicalCampaignContext.GlobalOnly, cursor, CancellationToken.None));

            Assert.Equal(digest, cursor);
        }

        Assert.Null(LongRestSweepHarness.Require(await h.Store.ReadNextProtectedPartitionAsync(CanonicalCampaignContext.GlobalOnly, cursor, CancellationToken.None)));

        Assert.Equal(before, await h.ScalarAsync("SELECT total_changes();"));

        Assert.Equal(0L, await h.ScalarAsync("SELECT count(*) FROM long_rest_discovery_generations;"));
    }
    [Theory]
    [InlineData("digest")]
    [InlineData("owner")]
    [InlineData("ambiguous")]
    public async Task Malformed_or_ambiguous_partition_metadata_refuses_without_writes(string corruption)
    {
        await using LongRestSweepHarness h = await LongRestSweepHarness.CreateAsync();

        Guid memory = Guid.NewGuid();

        await h.SeedAsync(memory.ToString("D"), "protected observation");

        await h.ProtectAsync(memory, GenerationProvenance.CreateExact([Guid.NewGuid()]));

        await h.ExecuteAsync("DROP TRIGGER artifact_sensitivity_guard_update;");

        if (corruption == "digest")
        {
            await h.ExecuteAsync("PRAGMA ignore_check_constraints = ON;");

            await h.ExecuteAsync("UPDATE artifact_sensitivity SET SensitivityDigest = x'01';");
        }
        else if (corruption == "owner")
        {
            await h.ExecuteAsync("UPDATE artifact_sensitivity SET SessionId = $session;", ("$session", Guid.NewGuid().ToString("D")));
        }
        else
        {
            await h.ExecuteAsync("CREATE TEMP TABLE duplicate_label AS SELECT * FROM artifact_sensitivity;");

            await h.ExecuteAsync("UPDATE duplicate_label SET LabelId = $label, ArtifactId = replace(ArtifactId, '-', '');", ("$label", Guid.NewGuid().ToString("D")));

            await h.ExecuteAsync("INSERT INTO artifact_sensitivity SELECT * FROM duplicate_label;");
        }

        long before = (long)(await h.ScalarAsync("SELECT total_changes();"))!;

        Assert.True((await h.Store.ReadNextProtectedPartitionAsync(CanonicalCampaignContext.GlobalOnly, null, CancellationToken.None)).IsFailure);

        Assert.Equal(before, await h.ScalarAsync("SELECT total_changes();"));
    }

}
