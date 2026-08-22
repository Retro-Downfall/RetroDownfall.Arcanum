using RetroDownfall.Arcanum.Core.ProvingGrounds;
using RetroDownfall.TheForge.Core.IO;
using RetroDownfall.TheForge.Core.Models.Trials;
using RetroDownfall.TheForge.Core.Serialization;
using RetroDownfall.TheForge.Core.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public class TrialSuiteStoreTests
{

    [Fact]
    public async Task RoundTrip_PersistsSuiteAndCapsRuns()
    {

        string path = Path.Combine(Path.GetTempPath(), $"forge-suites-{Guid.NewGuid():N}.json");

        try
        {

            TrialSuiteStore store = new(
                path,
                ImmediateTheForgeLocalMutationRunner.Instance,
                maxRunsPerSuite: 2);

            DateTimeOffset now = DateTimeOffset.UtcNow;

            Trial trial = new(TrialTargetKind.Spell, "echo", [new RegexInquisitor("ok")], null, null, null, "t1");

            TrialSuiteItemRecord item = new(Guid.NewGuid(), "item", trial, ["tag"], null);

            List<TrialSuiteRunRecord> runs =
            [
                new(Guid.NewGuid(), Guid.Empty, now.AddMinutes(-3), now.AddMinutes(-2), null, null, null, []),
                new(Guid.NewGuid(), Guid.Empty, now.AddMinutes(-2), now.AddMinutes(-1), null, null, null, []),
                new(Guid.NewGuid(), Guid.Empty, now.AddMinutes(-1), now, null, null, null, []),
            ];

            Guid suiteId = Guid.NewGuid();

            runs = runs.Select(r => r with { SuiteId = suiteId }).ToList();

            TrialSuiteRecord suite = new(suiteId, "Suite A", "desc", now, now, [item], runs);

            TrialSuiteStoreDocument document = new(1, now, now, [suite]);

            await store.SaveAsync(document);

            TrialSuiteStoreDocument loaded = await store.LoadAsync();

            Assert.Equal(1, loaded.SchemaVersion);

            Assert.Single(loaded.Suites);

            Assert.Equal(2, loaded.Suites[0].Runs.Count);

            Assert.Equal(runs[2].Id, loaded.Suites[0].Runs[0].Id);

            Assert.Equal("echo", loaded.Suites[0].Trials[0].Trial.Target);

        }
        finally
        {

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }

    }

    [Fact]
    public async Task AtomicWrite_ProducesReadableDocument()
    {

        string path = Path.Combine(Path.GetTempPath(), $"forge-suites-atomic-{Guid.NewGuid():N}.json");

        try
        {

            DateTimeOffset now = DateTimeOffset.UtcNow;

            TrialSuiteStoreDocument document = new(1, now, now, []);

            await TheForgeAtomicJsonFile.WriteAsync(
                path,
                document,
                TheForgeTrialSuitesJsonContext.Default.TrialSuiteStoreDocument,
                CancellationToken.None);

            TrialSuiteStoreDocument? loaded = await TheForgeAtomicJsonFile.ReadAsync(
                path,
                TheForgeTrialSuitesJsonContext.Default.TrialSuiteStoreDocument,
                CancellationToken.None);

            Assert.NotNull(loaded);

            Assert.Equal(1, loaded!.SchemaVersion);

            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"));

        }
        finally
        {

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }

    }

}
