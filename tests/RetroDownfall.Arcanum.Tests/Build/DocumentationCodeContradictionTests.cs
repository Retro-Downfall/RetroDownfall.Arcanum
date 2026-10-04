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

    private static readonly Regex InlineCodeSpan = new(
        @"(`+)(?<code>.+?)\1",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex PascalCaseIdentifier = new(
        @"^[A-Z][A-Za-z0-9]+$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static readonly Regex Word = new(
        @"[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(30));

    /// <summary>
    /// Identifiers the testing chapter names that are not declared in the repository, each with the
    /// reason it is nonetheless the right word.
    /// </summary>
    private static readonly Dictionary<string, string> NamedButNotDeclared = new(StringComparer.Ordinal)
    {
        ["HostFactoryResolver"] = "Microsoft.Extensions.Hosting's internal entry-point resolver, named for the deadlock it explains",
        ["HostProcessToolsMarkerSlot"] = "the shared suffix of the three platform marker slot types, which the coverage run settings exclude by pattern",
    };

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

    [Fact]
    public void The_design_does_not_claim_a_runtime_native_proof_that_no_host_code_calls()
    {
        string design = ReadDocument("Arcanum.DESIGN.md");

        const string claimedCall = "SqliteNativeRuntimeValidator.ValidateAsync";

        // The behavioral and hash proof is documented as a pre-open guarantee only while some host
        // code calls it. The validator and its result type do not count as a call site.
        string sourceRoot = Path.Combine(TestRepositoryPaths.RepositoryRoot(), "src");

        bool hasProductionCallSite = Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !Path.GetFileName(path).StartsWith("SqliteNativeRuntimeValidat", StringComparison.Ordinal))
            .Any(path => File.ReadAllText(path).Contains("SqliteNativeRuntimeValidator", StringComparison.Ordinal));

        if (!hasProductionCallSite)
        {
            Assert.DoesNotContain(claimedCall, design, StringComparison.Ordinal);

            string section = DocumentSection(design, "**Runtime proof.**", "**Compatibility.**");

            Assert.Contains("build, CI, and test-time", section, StringComparison.Ordinal);

            Assert.DoesNotContain("before the Grimoire opens", section, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_native_sqlcipher_targets_do_not_describe_the_embedded_manifest_as_a_runtime_input()
    {
        string targets = File
            .ReadAllText(
                Path.Combine(
                    TestRepositoryPaths.RepositoryRoot(),
                    "src",
                    "RetroDownfall.Arcanum.NativeSqlCipher",
                    "buildTransitive",
                    "RetroDownfall.Arcanum.NativeSqlCipher.targets"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.DoesNotContain("The runtime validator compares", targets, StringComparison.Ordinal);

        Assert.DoesNotContain("rather than being read from a", targets, StringComparison.Ordinal);

        Assert.Contains("test-time", targets, StringComparison.Ordinal);
    }

    [Fact]
    public void The_design_places_cli_diagnostics_in_the_report_but_outside_the_aggregate_and_names_the_real_ci_runner()
    {
        string root = TestRepositoryPaths.RepositoryRoot();

        string runsettings = File.ReadAllText(
            Path.Combine(root, "tests", "RetroDownfall.Arcanum.Tests", "coverage.runsettings"));

        // The claim below holds while the Include filter instruments Cli (so its checks are in the report)
        // and the gate script removes it from the aggregate.
        Assert.Contains("[RetroDownfall.Arcanum.Cli]*", runsettings, StringComparison.Ordinal);

        string design = ReadDocument("Arcanum.DESIGN.md");

        string placement = DocumentSection(design, "**Placement.**", "The twelve original checks");

        Assert.DoesNotContain("All of those are inside the coverage denominator", placement, StringComparison.Ordinal);

        Assert.Contains("outside", placement, StringComparison.Ordinal);

        Assert.Contains("removed from the aggregate", placement, StringComparison.Ordinal);

        string workflow = File
            .ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        string job = workflow[workflow.IndexOf("\n  build-test:", StringComparison.Ordinal)..];

        Match runner = Regex.Match(job, @"\n    runs-on: (?<label>\S+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));

        Assert.True(runner.Success, "ci.yml's build-test job names no runner.");

        string header = Assert.Single(
            design.Split('\n'),
            static line => line.StartsWith("| Post-exclusion metric", StringComparison.Ordinal));

        Assert.Contains($"`{runner.Groups["label"].Value}`", header, StringComparison.Ordinal);

        Assert.DoesNotContain("macOS 14", header, StringComparison.Ordinal);
    }

    [Fact]
    public void The_engineering_house_style_states_the_same_blank_line_rule_as_agents_md()
    {
        string agents = File
            .ReadAllText(Path.Combine(TestRepositoryPaths.RepositoryRoot(), "AGENTS.md"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        const string rule = "never immediately after an opening or before a closing parenthesis, bracket, or brace";

        Assert.Contains(rule, agents, StringComparison.Ordinal);

        string engineering = ReadDocument("Arcanum.Engineering.md");

        string section = DocumentSection(engineering, "### 7. C# house style", "> **Note on org-wide rules:**");

        // The older wording ("one blank line after each line ... curly braces do not require blank
        // lines around them") told contributors the opposite of what the formatter and AGENTS.md rule
        // 4 enforce around delimiters.
        Assert.Contains(rule, section, StringComparison.Ordinal);

        Assert.DoesNotContain("Curly braces do not require blank lines around them", section, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>SessionTurnClaimStore</c> is registered but nothing in production resolves it, and the design
    /// says so instead of listing it as the owner of durable Session turn claims. The claim stays true
    /// only while the declaration, the store, and the registration are the only files naming the
    /// coordinator; the day a turn path consumes it this fails, and the section has to be rewritten
    /// with the wiring rather than left saying "unconsumed".
    /// </summary>
    [Fact]
    public void The_session_turn_claim_coordinator_is_documented_as_installed_but_unconsumed()
    {
        string section = DocumentSection(
            ReadDocument("Arcanum.DESIGN.md"),
            "### 10.18 ",
            "### 10.19 ");

        Assert.Contains("installed but unconsumed", section, StringComparison.Ordinal);

        Assert.DoesNotContain("| Durable Session turn claims |", section, StringComparison.Ordinal);

        string[] namingFiles =
        [
            .. Directory
                .EnumerateFiles(
                    Path.Combine(TestRepositoryPaths.RepositoryRoot(), "src"),
                    "*.cs",
                    SearchOption.AllDirectories)
                .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                .Where(static path => File
                    .ReadAllText(path)
                    .Contains("ISessionTurnClaimCoordinator", StringComparison.Ordinal))
                .Select(static path => Path.GetFileName(path))
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(
            [
                "ISessionTurnClaimCoordinator.cs",
                "ServiceCollectionExtensions.cs",
                "SessionTurnClaimStore.cs",
            ],
            namingFiles);
    }

    /// <summary>
    /// The diagnostic MCP invocation route and the Scalar reference UI are both gated, and the API
    /// reference says so where a reader meets the route, not only in a design section. The claims hold
    /// while the route refuses outside the Development edition and Scalar maps only on its feature flag.
    /// </summary>
    [Fact]
    public void The_api_reference_says_the_diagnostic_mcp_route_and_scalar_are_gated()
    {
        Assert.Contains(
            "edition != ArcanumEdition.Development",
            ReadSource("Api", "Mcp", "DiagnosticMcpInvocationEndpoints.cs"),
            StringComparison.Ordinal);

        Assert.Contains(
            "ErrorCodes.Mcp.DiagnosticDisabled",
            ReadSource("Api", "Mcp", "DiagnosticMcpInvocationEndpoints.cs"),
            StringComparison.Ordinal);

        Assert.Contains(
            "\"Arcanum:Features:ScalarUi\"",
            ReadSource("Api", "ApiBootstrapper.cs"),
            StringComparison.Ordinal);

        string[] lines = ReadDocument("Arcanum.API.md").Split('\n');

        string diagnosticRoute = Assert.Single(
            lines,
            static line => line.StartsWith("| POST | `/api/mcp/tools/invoke`", StringComparison.Ordinal));

        Assert.Contains("Development edition only", diagnosticRoute, StringComparison.Ordinal);

        Assert.Contains("`Mcp.DiagnosticDisabled`", diagnosticRoute, StringComparison.Ordinal);

        string scalarRoute = Assert.Single(
            lines,
            static line => line.StartsWith("| `GET /api/openapi/v1.json`", StringComparison.Ordinal));

        Assert.Contains("`Arcanum:Features:ScalarUi`", scalarRoute, StringComparison.Ordinal);

        Assert.Contains("default false", scalarRoute, StringComparison.Ordinal);
    }

    /// <summary>
    /// The testing chapter names only identifiers that occur in the code, tests, scripts or workflows.
    /// </summary>
    /// <remarks>
    /// <para>The chapter is where a contributor learns how the CLI is tested, and it kept naming a
    /// command-application type the CLI stopped using when it moved to System.CommandLine and a test
    /// class that was never written. Both read as established vocabulary, so a reader searched for them
    /// and concluded the harness was missing rather than the sentence stale.</para>
    /// <para>The check is deliberately a word-occurrence one: an identifier a reader can search for has
    /// to occur somewhere a search finds it. It cannot tell a type from a method or an enum member, and
    /// it does not need to, because the failure it prevents is a name that matches nothing at all. This
    /// file is left out of the corpus, since it would otherwise supply every name it tests for.</para>
    /// </remarks>
    [Fact]
    public void Design_names_only_types_that_exist()
    {
        string design = ReadDocument("Arcanum.DESIGN.md");

        string chapter = DocumentSection(design, "## 13. Testing strategy", "\n## 14. ");

        HashSet<string> named = new(StringComparer.Ordinal);

        bool fenced = false;

        foreach (string line in chapter.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                fenced = !fenced;

                continue;
            }

            if (fenced)
            {
                continue;
            }

            foreach (Match span in InlineCodeSpan.Matches(line))
            {
                string code = span.Groups["code"].Value.Trim();

                if (PascalCaseIdentifier.IsMatch(code))
                {
                    named.Add(code);
                }
            }
        }

        Assert.True(named.Count > 100, $"Only {named.Count} identifiers were read from the testing chapter.");

        foreach (string allowed in NamedButNotDeclared.Keys)
        {
            Assert.Contains(allowed, named);
        }

        HashSet<string> missing = new(named, StringComparer.Ordinal);

        missing.ExceptWith(NamedButNotDeclared.Keys);

        string root = TestRepositoryPaths.RepositoryRoot();

        string self = Path.GetFullPath(Path.Combine(root, "tests", "RetroDownfall.Arcanum.Tests", "Build", "DocumentationCodeContradictionTests.cs"));

        HashSet<string>.AlternateLookup<ReadOnlySpan<char>> lookup = missing.GetAlternateLookup<ReadOnlySpan<char>>();

        foreach (string path in CorpusFiles(root))
        {
            if (missing.Count == 0)
            {
                break;
            }

            if (string.Equals(Path.GetFullPath(path), self, StringComparison.Ordinal))
            {
                continue;
            }

            string text = File.ReadAllText(path);

            foreach (ValueMatch match in Word.EnumerateMatches(text))
            {
                _ = lookup.Remove(text.AsSpan(match.Index, match.Length));
            }
        }

        Assert.True(
            missing.Count == 0,
            "The testing chapter of docs/Arcanum.DESIGN.md names identifiers that occur nowhere in the repository:\n"
                + string.Join('\n', missing.Order(StringComparer.Ordinal)));
    }

    private static IEnumerable<string> CorpusFiles(string root)
    {
        string[] extensions = [".cs", ".csproj", ".props", ".targets", ".runsettings", ".sh", ".py", ".yml", ".yaml"];

        string[] directories = ["src", "tests", "scripts", ".github"];

        foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            if (extensions.Contains(Path.GetExtension(file), StringComparer.Ordinal))
            {
                yield return file;
            }
        }

        foreach (string directory in directories)
        {
            string path = Path.Combine(root, directory);

            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(root, file);

                string[] segments = relative.Split(Path.DirectorySeparatorChar);

                if (segments.Contains("bin", StringComparer.Ordinal)
                    || segments.Contains("obj", StringComparer.Ordinal)
                    || segments.Contains("node_modules", StringComparer.Ordinal)
                    || !extensions.Contains(Path.GetExtension(file), StringComparer.Ordinal))
                {
                    continue;
                }

                yield return file;
            }
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
