using RetroDownfall.Arcanum.Tests.Support;

namespace RetroDownfall.Arcanum.Tests.Build;

/// <summary>
/// A sentence in a reference document does not say the opposite of the source it describes.
/// </summary>
/// <remarks>
/// <para>Each test pairs a claim the documents make with the line of source that makes it true or
/// false, so a failure is a contradiction between two files and not a spelling rule over one. They
/// exist because the failure they prevent is silent: a "nothing publishes" or "no route is mapped"
/// sentence stays readable and plausible after the code beside it changes, and a reader who trusts
/// it concludes the surface is unreachable or the evidence complete.</para>
/// <para>The assertions are deliberately narrow. Each names the stale sentence by a distinctive
/// fragment and the true one by the term it has to carry, so rewording the surrounding prose does
/// not fail them.</para>
/// </remarks>
public sealed class DocumentationCodeContradictionTests
{
    [Fact]
    public void A_committed_entry_erase_is_documented_as_republishing_canonical_mutation()
    {
        string design = ReadDocument("Arcanum.DESIGN.md");

        string section = DocumentSection(
            design,
            "#### 21.15.7 Covenant entry erasure",
            "#### 21.15.8 Lifecycle and restore");

        string service = ReadSource("Infrastructure", "Covenant", "CovenantEntryErasureService.cs");

        // The claim is true only while the service republishes after its COMMIT.
        Assert.Contains("CovenantHealthTransition.CanonicalMutation", service, StringComparison.Ordinal);

        Assert.DoesNotContain("publishes no availability", section, StringComparison.Ordinal);

        Assert.DoesNotContain("publishes no availability", service, StringComparison.Ordinal);

        Assert.Contains("republishes `CanonicalMutation`", section, StringComparison.Ordinal);
    }

    [Fact]
    public void The_design_does_not_call_a_mapped_or_registered_covenant_surface_absent()
    {
        string bootstrapper = ReadSource("Api", "ApiBootstrapper.cs");

        Assert.Contains("MapCovenantMutationEndpoints()", bootstrapper, StringComparison.Ordinal);

        Assert.Contains("MapCovenantInspectionEndpoints()", bootstrapper, StringComparison.Ordinal);

        Assert.Contains(
            "memory.Add(BuildCovenant(sp));",
            ReadSource("Cli", "Infrastructure", "CliCommandTree.Memory.cs"),
            StringComparison.Ordinal);

        Assert.Contains(
            "CovenantToolStagingAmbient.Push(",
            ReadSource("Api", "Intelligence", "WizardIntelligenceProvider.cs"),
            StringComparison.Ordinal);

        string design = ReadDocument("Arcanum.DESIGN.md");

        foreach (string stale in (string[])
                 [
                     "No route is mapped, no command is registered, no port has an implementation",
                     "No dedicated Covenant management route is mapped",
                     "There is no Covenant management route, no mutation route, and no command",
                     "Agent-originated mutation is not yet live",
                     "no turn mints a tool capability",
                     "CovenantMemoryEndpoints",
                     "and their dedicated CLI are not.**",
                 ])
        {
            Assert.DoesNotContain(stale, design, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(
            "management routes, the Campaign-path and Session-binding administration surfaces, and the `arcanum memory covenant` commands are **not** included",
            ReadDocument("Compendium.README.md"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_design_states_the_covenant_surfaces_that_are_still_absent()
    {
        // These two facts are what the absent paragraphs rest on; if either changes, the paragraphs are stale.
        Assert.DoesNotContain(
            "ICovenantMaintenanceService",
            ReadSource("Infrastructure", "DependencyInjection", "ServiceCollectionExtensions.cs"),
            StringComparison.Ordinal);

        Assert.DoesNotContain(
            "\"doctor\"",
            ReadSource("Cli", "Infrastructure", "CliCommandTree.Covenant.cs"),
            StringComparison.Ordinal);

        string design = ReadDocument("Arcanum.DESIGN.md");

        foreach ((string start, string end) in (ValueTuple<string, string>[])
                 [
                     ("### 10.17 Covenant maintenance and protected-erasure recovery", "### 10.18 Covenant operator surfaces, configuration, and the pre-binding authority boundary"),
                     ("### 10.18 Covenant operator surfaces, configuration, and the pre-binding authority boundary", "### 10.19 Covenant backup, restore, and protected transfer"),
                 ])
        {
            string section = DocumentSection(design, start, end);

            Assert.Contains("registered in no container", section, StringComparison.Ordinal);
        }
    }

    private static string ReadDocument(string fileName) =>
        File
            .ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "docs", fileName))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string ReadSource(params string[] relative) =>
        File
            .ReadAllText(
                Path.Combine(
                    [
                        TestRepositoryPaths.RepositoryRoot(),
                        "src",
                        $"RetroDownfall.Arcanum.{relative[0]}",
                        .. relative[1..],
                    ]))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string DocumentSection(
        string document,
        string startHeading,
        string endHeading)
    {
        int start = document.IndexOf(startHeading, StringComparison.Ordinal);

        Assert.True(start >= 0, $"Missing documentation heading: {startHeading}");

        int end = document.IndexOf(endHeading, start, StringComparison.Ordinal);

        Assert.True(end > start, $"Missing documentation heading after {startHeading}: {endHeading}");

        return document[start..end];
    }
}
