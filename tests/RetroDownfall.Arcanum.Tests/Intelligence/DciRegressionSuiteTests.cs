using RetroDownfall.Arcanum.Tests.NativeSqlCipher;

namespace RetroDownfall.Arcanum.Tests.Intelligence;

/// <summary>
/// Gives the DCI regression suite an executable definition, so "the DCI regression suite passes
/// unchanged" is a claim a machine can settle rather than a file list someone assembles by hand.
/// </summary>
/// <remarks>
/// <para>These assertions deliberately stay outside the <c>Suite=Dci</c> filter. A membership guard
/// that carried the trait would satisfy its own non-empty assertion, which is the exact failure being
/// guarded against: a <c>dotnet test --filter</c> that selects nothing still prints <c>Passed!</c>,
/// so a suite whose members have all silently lost the trait is indistinguishable from a green
/// one.</para>
/// </remarks>
public sealed class DciRegressionSuiteTests
{

    private const string SuiteTrait = "Suite";

    private const string DciSuite = "Dci";

    /// <summary>The classes that go red when the rendered DCI document, its stable/volatile segmentation, or its per-source token attribution changes.</summary>
    private static readonly string[] DeclaredMembers =
    [
        "ModelTokenEstimatorTests",

        "PromptCachePlannerTests",

        "SystemPromptBuilderResonanceTests",

        "SystemPromptBuilderTests",

        "SystemPromptBuilderUntrustedFenceTests",

        "SystemPromptCovenantPlacementTests",

        "SystemPromptTokenAttributionTests",
    ];

    [Fact]
    public void The_dci_suite_filter_selects_exactly_the_classes_the_roster_names()
    {

        string[] selected = typeof(DciRegressionSuiteTests).Assembly
            .GetTypes()
            .Where(static type => CarriesDciTrait(type))
            .Select(static type => type.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            selected.Length > 0,
            $"No class carries [Trait(\"{SuiteTrait}\", \"{DciSuite}\")], so --filter \"{SuiteTrait}={DciSuite}\" "
                + "selects nothing and reports Passed! without running a single golden.");

        Assert.Equal(DeclaredMembers, selected);

    }

    /// <summary>
    /// A trait nobody runs is no better than no trait. The Oath names the suite as the proof that a
    /// gated feature left today's bytes alone, so the invocation that runs it belongs beside the claim.
    /// </summary>
    [Fact]
    public void The_oath_records_the_invocation_that_runs_the_dci_suite()
    {

        string oath = File.ReadAllText(
            Path.Combine(NativeSqlCipherTestPaths.RepositoryRoot(), "docs", "Arcanum.OATH.md"));

        Assert.Contains(
            $"--filter \"{SuiteTrait}={DciSuite}\"",
            oath,
            StringComparison.Ordinal);

        Assert.Contains(
            $"[Trait(\"{SuiteTrait}\", \"{DciSuite}\")]",
            oath,
            StringComparison.Ordinal);

    }

    private static bool CarriesDciTrait(Type type) =>
        type.GetCustomAttributesData()
            .Where(static attribute => attribute.AttributeType == typeof(TraitAttribute))
            .Any(static attribute =>
                attribute.ConstructorArguments.Count == 2
                && attribute.ConstructorArguments[0].Value as string == SuiteTrait
                && attribute.ConstructorArguments[1].Value as string == DciSuite);

}
