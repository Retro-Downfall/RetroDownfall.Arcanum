using RetroDownfall.TheForge.Core.Models.Comparisons;
using RetroDownfall.TheForge.Core.Models.DiagnosticMcp;
using RetroDownfall.TheForge.Core.Models.Traces;
using RetroDownfall.TheForge.Core.Models.Trials;
using RetroDownfall.TheForge.Core.IO;
using RetroDownfall.TheForge.Core.Services;
using Xunit;

namespace RetroDownfall.TheForge.Tests;

public sealed class TheForgeStoreMutationLifetimeTests
{

    [Theory]
    [InlineData(StoreKind.TrialSuites)]
    [InlineData(StoreKind.Comparisons)]
    [InlineData(StoreKind.InferenceTraces)]
    [InlineData(StoreKind.DiagnosticFixtures)]
    public async Task SaveAsync_WhenFileChangesWhileWaitingForAdmission_RefusesStaleReplacement(
        StoreKind storeKind)
    {

        string path = Path.Combine(
            Path.GetTempPath(),
            $"forge-store-lifetime-{storeKind}-{Guid.NewGuid():N}.json");

        const string replacementContents = "RESET-GENERATION";

        try
        {

            DateTimeOffset now = DateTimeOffset.UtcNow;

            Func<Task> seed = CreateSave(storeKind, path, ImmediateTheForgeLocalMutationRunner.Instance, now);

            await seed();

            bool replaced = false;

            BeforeMutationTheForgeLocalMutationRunner runner = new(
                () =>
                {

                    if (replaced)
                    {

                        return;

                    }

                    replaced = true;

                    File.WriteAllText(path, replacementContents);

                });

            Func<Task> staleSave = CreateSave(storeKind, path, runner, now.AddMinutes(1));

            await Assert.ThrowsAsync<TheForgeStoreChangedException>(staleSave);

            Assert.Equal(replacementContents, await File.ReadAllTextAsync(path));

        }
        finally
        {

            if (File.Exists(path))
            {

                File.Delete(path);

            }

        }

    }

    private static Func<Task> CreateSave(
        StoreKind storeKind,
        string path,
        ITheForgeLocalMutationRunner runner,
        DateTimeOffset now) =>
        storeKind switch
        {
            StoreKind.TrialSuites => () => new TrialSuiteStore(path, runner)
                .SaveAsync(new TrialSuiteStoreDocument(1, now, now, [])),

            StoreKind.Comparisons => () => new ComparisonRunStore(path, runner)
                .SaveAsync(new ComparisonStoreDocument(1, now, now, [])),

            StoreKind.InferenceTraces => () => new InferenceTraceStore(path, runner)
                .SaveAsync(new InferenceTraceStoreDocument(1, now, now, [])),

            StoreKind.DiagnosticFixtures => () => new DiagnosticMcpFixtureStore(path, runner)
                .SaveAsync(new DiagnosticMcpFixtureStoreDocument(1, now, now, [])),

            _ => throw new ArgumentOutOfRangeException(nameof(storeKind), storeKind, null),
        };

    public enum StoreKind
    {

        TrialSuites,

        Comparisons,

        InferenceTraces,

        DiagnosticFixtures,

    }

}
