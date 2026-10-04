using System.Text.RegularExpressions;

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
    private static readonly Regex EvidenceRemovalClause = new(
        @"only (?:through|by) a release[^.]*\.",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex RebuildRequiredClause = new(
        @"`RebuildRequired` (?:appears|is reported) only[^.]*\.",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

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

    [Fact]
    public void Every_list_of_the_ways_erasure_evidence_is_removed_names_all_five()
    {
        (string File, int Minimum)[] documents =
        [
            ("Arcanum.API.md", 1),
            ("Arcanum.Command.Reference.md", 2),
            ("Arcanum.DESIGN.md", 2),
        ];

        List<string> offenders = [];

        foreach ((string file, int minimum) in documents)
        {
            MatchCollection clauses = EvidenceRemovalClause.Matches(ReadDocument(file));

            Assert.True(
                clauses.Count >= minimum,
                $"{file} was expected to carry at least {minimum} list(s) of what removes erasure evidence and carries {clauses.Count}.");

            foreach (Match clause in clauses)
            {
                foreach (string path in (string[])
                         [
                             "a release",
                             "re-creation",
                             "reset-key",
                             "restore",
                             "full installation reset",
                         ])
                {
                    if (!clause.Value.Contains(path, StringComparison.Ordinal))
                    {
                        offenders.Add($"{file}: \"{clause.Value}\" omits {path}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_rebuild_required_rule_is_documented_with_its_accelerator_condition()
    {
        // The documented condition is true only while the rule keys on the accelerator being Healthy.
        Assert.Contains(
            "snapshot.Accelerator is CovenantCapabilityState.Healthy",
            ReadSource("Infrastructure", "Covenant", "CovenantManagementService.cs"),
            StringComparison.Ordinal);

        Assert.Contains(
            "outboxCanContinue || !rebuildOwed",
            ReadSource("Infrastructure", "Data", "Covenant", "CovenantSearchHealthRule.cs"),
            StringComparison.Ordinal);

        foreach (string file in (string[])["Arcanum.API.md", "Arcanum.DESIGN.md"])
        {
            Match clause = RebuildRequiredClause.Match(ReadDocument(file));

            Assert.True(clause.Success, $"{file} lost its statement of when RebuildRequired appears.");

            Assert.Contains("accelerator is not Healthy", clause.Value, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_factory_reset_plan_counts_are_documented_as_what_a_standalone_reset_keeps()
    {
        foreach (string file in (string[])["Arcanum.API.md", "Arcanum.DESIGN.md"])
        {
            string document = ReadDocument(file);

            Assert.DoesNotContain("stays in force after the reset", document, StringComparison.Ordinal);

            Assert.DoesNotContain("will remain in force", document, StringComparison.Ordinal);

            Assert.Contains("standalone factory reset", document, StringComparison.Ordinal);
        }

        // A global or all-scope installation reset removes the key through the credential catalog.
        Assert.Contains(
            "MemoryErasureFingerprintKeyAccount",
            ReadSource("Infrastructure", "InstallationReset", "InstallationResetCredentialCatalog.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_fingerprint_preimage_is_documented_with_the_campaign_presence_byte()
    {
        Assert.Contains(
            "WriteByte(value is null ? (byte)0 : (byte)1);",
            ReadSource("Core", "Memory", "MemoryErasureDigestGrammar.cs"),
            StringComparison.Ordinal);

        string section = DocumentSection(
            ReadDocument("Arcanum.DESIGN.md"),
            "#### 21.15.2 The fingerprint grammar",
            "#### 21.15.3 The two-phase guard and its chokepoints");

        Assert.DoesNotContain(
            "the Campaign as sixteen GUID bytes when the scope is a Campaign",
            section,
            StringComparison.Ordinal);

        Assert.Contains("presence byte", section, StringComparison.Ordinal);
    }

    [Fact]
    public void The_core_contract_text_calls_the_hash_a_rendered_hash()
    {
        Assert.Contains("rendered hash", ReadDocument("Arcanum.Command.Reference.md"), StringComparison.Ordinal);

        foreach (string[] path in (string[][])
                 [
                     ["Core", "Covenant", "CovenantPublicContractInventory.cs"],
                     ["Core", "Covenant", "CovenantMutationWireContracts.cs"],
                     ["Core", "Covenant", "CovenantOperatorPreflightBody.cs"],
                 ])
        {
            Assert.DoesNotContain("compiled hash", ReadSource(path), StringComparison.Ordinal);
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
