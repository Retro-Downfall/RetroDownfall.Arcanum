using System.Runtime.CompilerServices;

namespace RetroDownfall.Arcanum.Tests.Build;

public sealed class GuildCohortBoundedSourceTests
{
    [Fact]
    public void Cohort_recovery_and_cancellation_use_narrow_issuer_and_quest_metadata()
    {
        string root = FindRoot();

        foreach (string name in new[] { "GuildCohortStore.Recovery.cs", "GuildQuestStore.CohortCancellation.cs" })
        {
            string source = File.ReadAllText(Path.Combine(root, "src", "RetroDownfall.Arcanum.Infrastructure", "Guild", name));

            Assert.DoesNotContain("GuildQuestStore.ReadWithinAsync(", source, StringComparison.Ordinal);

            if (name == "GuildQuestStore.CohortCancellation.cs")
                Assert.DoesNotContain("await ReadWithinAsync(", source, StringComparison.Ordinal);
        }
    }

    private static string FindRoot([CallerFilePath] string path = "")
    {
        DirectoryInfo? directory = new(Path.GetDirectoryName(path)!);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("Repository root unavailable.");
    }
}
