using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// The references to the trusted platform assemblies that analyzer fixtures compile against. Each reference copies its assembly's metadata
/// into native memory, so a class that made a set of its own held a second image of every platform assembly for the life of the test host:
/// the fixture lane's resident set grew by that much for every such class, on a runner that has about 7 GB. The sets are made in one place.
/// </summary>
public sealed class TrustedPlatformReferencesTests
{
    private const string Variable = "TRUSTED_PLATFORM_ASSEMBLIES";

    [Fact]
    public void The_shared_set_is_one_set_for_the_process_and_a_separate_one_is_another()
    {
        Assert.NotEmpty(TrustedPlatformReferences.Shared);

        for (int index = 0; index < TrustedPlatformReferences.Shared.Length; index++)
        {
            Assert.Same(TrustedPlatformReferences.Shared[index], TrustedPlatformReferences.Shared[index]);
        }

        Microsoft.CodeAnalysis.PortableExecutableReference[] separate = [.. TrustedPlatformReferences.Separate()];

        Assert.Equal(TrustedPlatformReferences.Shared.Length, separate.Length);

        Assert.All(
            separate.Select((reference, index) => (reference, shared: TrustedPlatformReferences.Shared[index])),
            static pair => Assert.NotSame(pair.shared, pair.reference));
    }

    [Fact]
    public void No_test_source_reads_the_trusted_platform_assemblies_except_the_one_that_makes_the_sets()
    {
        string tests = Path.Combine(TestRepositoryPaths.RepositoryRoot(), "tests", "RetroDownfall.Arcanum.Tests");

        string[] offenders =
        [
            .. Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
                .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(static path => !string.Equals(Path.GetFileName(path), nameof(TrustedPlatformReferencesTests) + ".cs", StringComparison.Ordinal)
                    && !string.Equals(Path.GetFileName(path), nameof(TrustedPlatformReferences) + ".cs", StringComparison.Ordinal))
                .Where(static path => File.ReadAllText(path).Contains(Variable, StringComparison.Ordinal))
                .Select(path => Path.GetRelativePath(tests, path)),
        ];

        Assert.Empty(offenders);
    }
}
