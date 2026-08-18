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

    /// <summary>
    /// coverage.sh is not the only lane that runs the suite, and the budgets are machine-load
    /// sensitive by design — DESIGN §13.8 states outright that the harness is not a gate. A lane that
    /// runs them unfiltered turns a shared runner's scheduling noise into a red check that nothing
    /// about the code can fix.
    /// </summary>
    [Fact]
    public void Ci_test_lanes_cannot_select_the_perf_category()
    {

        string workflow = File
            .ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "ci.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("`\n", " ", StringComparison.Ordinal)
            .Replace("\\\n", " ", StringComparison.Ordinal);

        List<string> offenders = [];

        foreach (string line in workflow.Split('\n'))
        {

            int start = line.IndexOf("dotnet test", StringComparison.Ordinal);

            if (start < 0)
            {

                continue;

            }

            string command = line[start..];

            if (!command.Contains("RetroDownfall.Arcanum.Tests.csproj", StringComparison.Ordinal))
            {

                continue;

            }

            // Either the category is excluded outright, or the lane names the tests it wants — an
            // explicit FullyQualifiedName allow list cannot reach the perf class by accident.
            if (command.Contains($"Category!={PerfCategory}", StringComparison.Ordinal)
                || command.Contains("FullyQualifiedName~", StringComparison.Ordinal))
            {

                continue;

            }

            offenders.Add(command.Trim());

        }

        Assert.True(
            offenders.Count == 0,
            "A CI lane runs the Arcanum suite without excluding the wall-clock baselines, so a busy "
            + "shared runner reds the lane for reasons unrelated to any change. Add "
            + $"--filter \"Category!={PerfCategory}\":"
            + global::System.Environment.NewLine
            + string.Join(global::System.Environment.NewLine, offenders));

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
