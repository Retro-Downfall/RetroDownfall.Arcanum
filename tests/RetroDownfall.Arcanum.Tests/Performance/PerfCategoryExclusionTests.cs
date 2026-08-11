using System.Reflection;

namespace RetroDownfall.Arcanum.Tests.Performance;

/// <summary>
/// Guards the exclusion that the wall-clock baselines depend on. These assertions must stay outside
/// the "Perf" category, otherwise the filter they verify would also filter out the verification.
/// </summary>
public sealed class PerfCategoryExclusionTests
{

    private const string PerfCategory = "Perf";

    [Fact]
    public void Coverage_run_excludes_the_perf_category()
    {

        string script = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "scripts", "coverage.sh"));

        Assert.Contains(
            "Category!=Perf",
            script,
            StringComparison.Ordinal);

    }

    [Fact]
    public void Every_perf_namespace_test_class_carries_the_perf_category()
    {

        IEnumerable<Type> candidates = typeof(PerfCategoryExclusionTests).Assembly
            .GetTypes()
            .Where(static type =>
                type.Namespace == typeof(PerfCategoryExclusionTests).Namespace
                && type != typeof(PerfCategoryExclusionTests)
                && HasTestMethods(type));

        foreach (Type candidate in candidates)
        {

            Assert.True(
                HasPerfCategory(candidate),
                $"{candidate.Name} runs wall-clock assertions but is missing "
                    + $"[Trait(\"Category\", \"{PerfCategory}\")], so the coverage run cannot exclude it.");

        }

    }

    private static bool HasTestMethods(Type type) =>
        type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Any(static method => method.GetCustomAttributesData().Any(static attribute =>
                attribute.AttributeType.Name is "FactAttribute"
                    or "TheoryAttribute"
                    or "SkippableFactAttribute"
                    or "SkippableTheoryAttribute"));

    private static bool HasPerfCategory(Type type) =>
        type.GetCustomAttributesData()
            .Where(static attribute => attribute.AttributeType == typeof(TraitAttribute))
            .Any(static attribute =>
                attribute.ConstructorArguments.Count == 2
                && attribute.ConstructorArguments[0].Value as string == "Category"
                && attribute.ConstructorArguments[1].Value as string == PerfCategory);

    private static string FindRepositoryRoot()
    {

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {

            if (File.Exists(Path.Combine(directory.FullName, "RetroDownfall.Arcanum.slnx")))
            {

                return directory.FullName;

            }

            directory = directory.Parent;

        }

        throw new InvalidOperationException("Could not locate the repository root.");

    }

}
