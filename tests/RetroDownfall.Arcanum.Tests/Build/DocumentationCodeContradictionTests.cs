using System.Text.RegularExpressions;

using RetroDownfall.Arcanum.Api.Intelligence;
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

        // A turn stages Covenant material per provider round through its captured ambient set.
        Assert.Contains(
            "streamTurnAmbients.StageCovenantRound(",
            ReadSource("Api", "Intelligence", "WizardIntelligenceProvider.cs"),
            StringComparison.Ordinal);

        Assert.Contains(
            "CovenantToolStagingAmbient.Push(",
            ReadSource("Api", "Intelligence", "TurnAmbientSet.cs"),
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
        if (!NativeRuntimeValidatorHasProductionCallSite())
        {
            Assert.DoesNotContain(claimedCall, design, StringComparison.Ordinal);

            string section = DocumentSection(design, "**Runtime proof.**", "**Compatibility.**");

            Assert.Contains("build, CI, and test-time", section, StringComparison.Ordinal);

            Assert.DoesNotContain("before the Grimoire opens", section, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The validator's own type documentation is the first thing a reader of the class sees, and it
    /// used to say the proof runs "before the Grimoire is opened" and that a mismatch makes the
    /// Grimoire unavailable. No host code calls it, so that is a guarantee the tree does not provide.
    /// </summary>
    [Fact]
    public void The_native_runtime_validator_type_documentation_does_not_claim_a_pre_open_guarantee()
    {
        if (NativeRuntimeValidatorHasProductionCallSite())
        {
            return;
        }

        string source = ReadSource("Infrastructure", "Data", "SqliteNativeRuntimeValidator.cs");

        string typeDocumentation = source[..source.IndexOf("internal sealed class SqliteNativeRuntimeValidator", StringComparison.Ordinal)];

        Assert.DoesNotContain("before the Grimoire is opened", typeDocumentation, StringComparison.Ordinal);

        Assert.DoesNotContain("the Grimoire is unavailable", typeDocumentation, StringComparison.Ordinal);

        Assert.Contains("test-time", typeDocumentation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only the test classes whose source calls the validator exercise it. The delivery test hashes the
    /// delivered file against the on-disk manifest and never constructs the validator, so naming it
    /// beside the validator tells a reader a proof ran that did not.
    /// </summary>
    [Fact]
    public void The_validator_is_documented_as_exercised_only_by_the_test_classes_that_call_it()
    {
        string testDirectory = Path.Combine(
            TestRepositoryPaths.RepositoryRoot(),
            "tests",
            "RetroDownfall.Arcanum.Tests",
            "NativeSqlCipher");

        string[] testClasses =
        [
            .. Directory
                .EnumerateFiles(testDirectory, "*Tests.cs")
                .Select(static path => Path.GetFileNameWithoutExtension(path)),
        ];

        string[] callers =
        [
            .. testClasses.Where(name => File
                .ReadAllText(Path.Combine(testDirectory, name + ".cs"))
                .Contains("SqliteNativeRuntimeValidator", StringComparison.Ordinal)),
        ];

        Assert.NotEmpty(callers);

        string[] nonCallers = [.. testClasses.Except(callers, StringComparer.Ordinal)];

        string design = ReadDocument("Arcanum.DESIGN.md");

        string section = DocumentSection(design, "**Runtime proof.**", "**Compatibility.**");

        string targets = File
            .ReadAllText(
                Path.Combine(
                    TestRepositoryPaths.RepositoryRoot(),
                    "src",
                    "RetroDownfall.Arcanum.NativeSqlCipher",
                    "buildTransitive",
                    "RetroDownfall.Arcanum.NativeSqlCipher.targets"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        (string Origin, string Claim)[] claims =
        [
            ("DESIGN section 5.4 runtime proof", Regex.Match(section, @"exercised by ([^,.;]*)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)).Groups[1].Value),

            ("NativeSqlCipher targets comment", Regex.Match(targets, @"validator\s+\(([^)]*)\)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)).Groups[1].Value),
        ];

        foreach ((string origin, string claim) in claims)
        {
            Assert.True(claim.Length > 0, $"{origin} no longer says which tests exercise the validator.");

            foreach (string caller in callers)
            {
                Assert.True(
                    claim.Contains(caller, StringComparison.Ordinal),
                    $"{origin} omits {caller}, which calls the validator: {claim}");
            }

            foreach (string nonCaller in nonCallers)
            {
                Assert.True(
                    !claim.Contains(nonCaller, StringComparison.Ordinal),
                    $"{origin} names {nonCaller} as exercising the validator, but its source never calls it.");
            }
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

    /// <summary>
    /// The testing section's description of <c>HostProjectFeatureSwitchTests</c> is where a reader
    /// learns what stops a managed fallback. It named only the Native AOT and ReadyToRun checks and
    /// left out the publish guard, which is what rejects a publish with no runtime identifier.
    /// </summary>
    [Fact]
    public void The_design_describes_the_publish_guard_that_host_project_feature_switch_tests_pin()
    {
        string design = ReadDocument("Arcanum.DESIGN.md");

        string bullet = design
            .Split('\n')
            .Single(static line => line.StartsWith("- `HostProjectFeatureSwitchTests`", StringComparison.Ordinal));

        Assert.Contains("ARC0001", bullet, StringComparison.Ordinal);

        Assert.Contains("ArcanumDevPublish", bullet, StringComparison.Ordinal);

        Assert.Contains("RID-qualified", bullet, StringComparison.Ordinal);
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
    /// <remarks>
    /// Two other documents read as if the claim were in use and are held to the same fact: the OATH
    /// issue table must not list durable turn claims as landed behaviour without saying nothing consumes
    /// them, and the buffered-finalization step takes its exclusive pre-request revision from the begin
    /// preflight, which is what <c>GrimoireTurnWriter</c> reads, not from a durable claim.
    /// </remarks>
    [Fact]
    public void The_session_turn_claim_coordinator_is_documented_as_installed_but_unconsumed()
    {
        string section = DocumentSection(
            ReadDocument("Arcanum.DESIGN.md"),
            "### 10.18 ",
            "### 10.19 ");

        Assert.Contains("installed but unconsumed", section, StringComparison.Ordinal);

        Assert.DoesNotContain("| Durable Session turn claims |", section, StringComparison.Ordinal);

        // Comment-free text, so a doc comment that merely mentions the coordinator is not a consumer, and
        // repository-relative paths, so two files that share a name cannot alias one another.
        string[] namingFiles =
        [
            .. ProductionSourceInventory
                .Sources()
                .Where(static source => source.Text.Contains("ISessionTurnClaimCoordinator", StringComparison.Ordinal))
                .Select(static source => source.RelativePath)
                .Order(StringComparer.Ordinal),
        ];

        Assert.Equal(
            [
                "src/RetroDownfall.Arcanum.Core/Storage/ISessionTurnClaimCoordinator.cs",
                "src/RetroDownfall.Arcanum.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs",
                "src/RetroDownfall.Arcanum.Infrastructure/Repositories/SessionTurnClaimStore.cs",
            ],
            namingFiles);

        string oathRow = Assert.Single(
            ReadDocument("Arcanum.OATH.md").Split('\n'),
            static line => line.StartsWith("| **#89** |", StringComparison.Ordinal));

        Assert.DoesNotContain("durable Session turn claims,", oathRow, StringComparison.Ordinal);

        Assert.Contains("no turn path consumes them yet", oathRow, StringComparison.Ordinal);

        string design = ReadDocument("Arcanum.DESIGN.md");

        Assert.DoesNotContain("the turn claim's exclusive pre-request history revision", design, StringComparison.Ordinal);

        Assert.Contains("the begin preflight's exclusive pre-request history revision", design, StringComparison.Ordinal);
    }

    /// <summary>
    /// The headers-first, capped-reader guarantee is stated on the bullet of the type that makes the request,
    /// not on the multiplexer's, which neither sends nor drains anything.
    /// </summary>
    /// <remarks>
    /// Inserting the multiplexer's bullet into the error-sanitization list once carried the sentence away from
    /// the dispatcher's bullet and onto its own, so the dispatcher stopped stating a guarantee only it
    /// provides and the multiplexer claimed one it cannot. The source side of the pair is the claim's anchor:
    /// <c>ResponseHeadersRead</c> and the capped drain live in the dispatcher and nowhere in the multiplexer.
    /// </remarks>
    [Fact]
    public void The_commlink_capped_reader_guarantee_is_stated_on_the_dispatcher_bullet_not_the_multiplexer_bullet()
    {
        string dispatcherSource = ReadSource("Infrastructure", "CommLink", "WebhookCommLinkDispatcher.cs");

        string multiplexerSource = ReadSource("Infrastructure", "CommLink", "CommLinkMultiplexer.cs");

        Assert.Contains("HttpCompletionOption.ResponseHeadersRead", dispatcherSource, StringComparison.Ordinal);

        Assert.Contains("HttpResponseBodyDrainer.DrainAsync", dispatcherSource, StringComparison.Ordinal);

        Assert.DoesNotContain("ResponseHeadersRead", multiplexerSource, StringComparison.Ordinal);

        Assert.DoesNotContain("HttpResponseBodyDrainer", multiplexerSource, StringComparison.Ordinal);

        string[] lines = ReadDocument("Arcanum.DESIGN.md").Split('\n');

        string dispatcherBullet = Assert.Single(
            lines,
            static line => line.StartsWith("- **`WebhookCommLinkDispatcher`** — outbound webhook exceptions", StringComparison.Ordinal));

        string multiplexerBullet = Assert.Single(
            lines,
            static line => line.StartsWith("- **`CommLinkMultiplexer`** — a sink that throws", StringComparison.Ordinal));

        Assert.Contains("`ResponseHeadersRead`", dispatcherBullet, StringComparison.Ordinal);

        Assert.Contains("existing capped reader", dispatcherBullet, StringComparison.Ordinal);

        Assert.Contains("cannot force full-body buffering", dispatcherBullet, StringComparison.Ordinal);

        Assert.DoesNotContain("ResponseHeadersRead", multiplexerBullet, StringComparison.Ordinal);

        Assert.DoesNotContain("capped reader", multiplexerBullet, StringComparison.Ordinal);

        Assert.DoesNotContain("full-body buffering", multiplexerBullet, StringComparison.Ordinal);
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

        // The paragraph that says both are registered on the keyed group states the gate too, so it does
        // not read as if Scalar were mapped unconditionally beside the always-mapped OpenAPI document.
        string keyedGroup = Assert.Single(
            lines,
            static line => line.Contains("`MapScalarApiReference`", StringComparison.Ordinal));

        Assert.Contains("`MapOpenApi`", keyedGroup, StringComparison.Ordinal);

        Assert.Contains("`Arcanum:Features:ScalarUi`", keyedGroup, StringComparison.Ordinal);

        Assert.DoesNotContain("are registered on the same keyed group", keyedGroup, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>WebResearch.UnsupportedOperation</c> is what the native web workflows answer when
    /// <c>Arcanum:Features:WebBrowsing</c> is off, and that is the cause an operator meets first, so the
    /// catalog row names it beside the provider causes.
    /// </summary>
    [Fact]
    public void The_api_catalog_says_WebResearch_UnsupportedOperation_also_means_the_web_workflows_are_off()
    {
        string workflow = ReadSource("Api", "Intelligence", "WebResearchWorkflowService.cs");

        Assert.Contains("Native web workflows are disabled. Enable Arcanum:Features:WebBrowsing.", workflow, StringComparison.Ordinal);

        string row = Assert.Single(
            ReadDocument("Arcanum.API.md").Split('\n'),
            static line => line.StartsWith("| `Lexicon.CurationUnavailable`; `WebResearch.MissingCredential`", StringComparison.Ordinal));

        Assert.Contains("`Arcanum:Features:WebBrowsing`", row, StringComparison.Ordinal);

        Assert.Contains("an unavailable provider or static reader", row, StringComparison.Ordinal);
    }

    /// <summary>
    /// The A2A server routes are mapped when <c>Arcanum:Features:A2AServer</c> is true, and the documents
    /// say that flag alone gates them. The Conclave flag is derived from it, so a document that lists
    /// both as conditions sends an operator to set a flag the host sets for them.
    /// </summary>
    [Fact]
    public void The_a2a_server_routes_are_documented_as_gated_by_the_A2AServer_flag_alone()
    {
        // The three source facts the sentences below rest on.
        string defaults = ReadSource("Core", "Configuration", "ArcanumRuntimeDefaults.cs");

        Assert.Contains("Enabled = features.Conclave || a2a.Enabled,", defaults, StringComparison.Ordinal);

        Assert.Contains("Enabled = serverEnabled || clientEnabled,", defaults, StringComparison.Ordinal);

        string mapping = ReadSource("Api", "A2A", "A2AServerEndpoints.cs");

        Assert.Contains("!startupSettings.ResolveConclave().Enabled || !a2a.Enabled || !a2a.ServerEnabled", mapping, StringComparison.Ordinal);

        string[] apiLines = ReadDocument("Arcanum.API.md").Split('\n');

        string[] routeRows =
        [
            .. apiLines.Where(static line =>
                line.StartsWith("| — | `/api/conclave/a2a/*`", StringComparison.Ordinal)
                || line.StartsWith("| `GET /api/conclave/a2a/agent-card`", StringComparison.Ordinal)
                || line.StartsWith("| `POST /api/conclave/a2a`", StringComparison.Ordinal)),
        ];

        Assert.Equal(3, routeRows.Length);

        foreach (string row in routeRows)
        {
            Assert.DoesNotContain("`Arcanum:Features:Conclave` and `Arcanum:Features:A2AServer` are true", row, StringComparison.Ordinal);

            Assert.DoesNotContain("`Arcanum:Features:Conclave && Arcanum:Features:A2AServer`", row, StringComparison.Ordinal);

            Assert.Contains("`Arcanum:Features:A2AServer`", row, StringComparison.Ordinal);
        }

        Assert.Contains("which derives `Arcanum:Features:Conclave`", routeRows[0], StringComparison.Ordinal);

        Assert.Contains("which derives `Arcanum:Features:Conclave`", routeRows[1], StringComparison.Ordinal);

        string design = ReadDocument("Arcanum.DESIGN.md");

        Assert.DoesNotContain("The **only** gates are `Arcanum:Features:Conclave` plus", design, StringComparison.Ordinal);

        Assert.Contains("either of which derives `Arcanum:Features:Conclave`", design, StringComparison.Ordinal);
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

    /// <summary>
    /// The documents name the command framework the CLI uses, System.CommandLine, and not the one it
    /// left. The testing chapter check above reads one chapter; this one reads every governed document,
    /// because the stale vocabulary lived in the CLI composition and parsing sections, and it named a
    /// type (<c>RepeatableOptionMerger</c>) that exists nowhere in the source.
    /// </summary>
    [Fact]
    public void The_documents_do_not_name_the_command_framework_the_cli_left()
    {
        string root = TestRepositoryPaths.RepositoryRoot();

        string[] documents =
        [
            Path.Combine(root, "README.md"),
            Path.Combine(root, "AGENTS.md"),
            .. Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.TopDirectoryOnly),
        ];

        Regex retired = new(
            @"\bCAF\b|ConsoleAppFramework|RepeatableOptionMerger",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));

        List<string> offenders = [];

        foreach (string document in documents)
        {
            string[] lines = File.ReadAllText(document).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

            for (int index = 0; index < lines.Length; index++)
            {
                Match match = retired.Match(lines[index]);

                if (match.Success)
                {
                    offenders.Add($"{Path.GetRelativePath(root, document).Replace(Path.DirectorySeparatorChar, '/')}:{index + 1}: {match.Value}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A document names the retired command framework or a type that exists nowhere:\n" + string.Join('\n', offenders));

        // The sentences that replaced them name the behavior the run and watch tests pin.
        string design = ReadDocument("Arcanum.DESIGN.md");

        Assert.Contains("A repeated flag accumulates into its array-valued option", design, StringComparison.Ordinal);

        Assert.Contains("System.CommandLine command tree", design, StringComparison.Ordinal);
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

    [Fact]
    public void The_context_preview_billing_text_says_each_embedding_is_ledgered_and_only_the_model_calls_are_not()
    {
        // The claims below are true only while these five source facts hold.
        Assert.Contains(
            ".EmbedAsync(userPrompt,",
            Compact(ReadSource("Api", "Intelligence", "SemanticSpellRouter.cs")),
            StringComparison.Ordinal);

        Assert.Contains(
            ".EmbedBatchAsync(descriptions,",
            Compact(ReadSource("Infrastructure", "Weave", "SpellWeaveCache.cs")),
            StringComparison.Ordinal);

        string weave = ReadSource("Api", "Intelligence", "WeaveService.cs");

        Assert.Contains("TurnAccountingHandle? accounting = TurnAccountingAmbient.Current;", weave, StringComparison.Ordinal);

        Assert.Contains("surface: \"embedding\"", weave, StringComparison.Ordinal);

        Assert.Contains("BillableOperationType.Embedding", weave, StringComparison.Ordinal);

        // The preview publishes no ambient accounting, so every embedding takes the owning branch above.
        Assert.DoesNotContain(
            "TurnAccountingAmbient",
            ReadSource("Api", "Intelligence", "WizardIntelligenceProvider.ContextPreview.cs"),
            StringComparison.Ordinal);

        // The model-backed auxiliary calls report to the response and then have nothing to record into.
        Assert.Contains(
            "if (TurnAccountingAmbient.Current is not TurnAccountingHandle accounting)",
            ReadSource("Api", "Intelligence", "WizardIntelligenceProvider.cs"),
            StringComparison.Ordinal);

        string bullet = DocumentSection(
            ReadDocument("Arcanum.DESIGN.md"),
            "- **Billable boundary.**",
            "- **Usage authority.**");

        // Semantic Spell routing embeds the prompt (and, on a cache miss, the catalog) through the same
        // WeaveService call as the query embedding, so each opens its own run and is ledgered.
        Assert.DoesNotContain("writes no turn run", bullet, StringComparison.Ordinal);

        Assert.DoesNotContain("routing and extraction usage is reported in the response but not recorded in the ledger", bullet, StringComparison.Ordinal);

        Assert.Contains("Every embedding it makes", bullet, StringComparison.Ordinal);

        Assert.Contains("opens, reserves, settles, and closes its own short `embedding` run", bullet, StringComparison.Ordinal);

        // What is not ledgered is the model-backed pair, named by their purposes.
        Assert.Contains("`routing`", bullet, StringComparison.Ordinal);

        Assert.Contains("`lexicon`", bullet, StringComparison.Ordinal);

        Assert.Contains("`TryRecordAuxiliaryUsageAsync`", bullet, StringComparison.Ordinal);
    }

    /// <summary>
    /// The design's closed non-billable set is the set <see cref="NonBillableSurfaces"/> declares, and the
    /// context preview, which makes real auxiliary calls with retrieval enabled, is not in it. A route
    /// added to the list without a word in the design would let a reader conclude it is billable, and one
    /// that reached a provider for tokens would be an unledgered spend.
    /// </summary>
    [Fact]
    public void The_design_billable_boundary_names_every_POST_surface_in_the_closed_non_billable_list()
    {
        string[] surfaces =
        [
            .. typeof(NonBillableSurfaces)
                .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                .Where(static field => field is { IsLiteral: true } && field.FieldType == typeof(string))
                .Select(static field => (string)field.GetRawConstantValue()!)
                .Order(StringComparer.Ordinal),
        ];

        Assert.NotEmpty(surfaces);

        Assert.DoesNotContain(surfaces, static surface => surface.Contains("context/inspect", StringComparison.Ordinal));

        Assert.DoesNotContain(surfaces, static surface => surface.Contains("/ping", StringComparison.Ordinal));

        string bullet = DocumentSection(
            ReadDocument("Arcanum.DESIGN.md"),
            "- **Billable boundary.**",
            "- **Usage authority.**");

        foreach (string surface in surfaces.Where(static surface => surface.StartsWith("POST ", StringComparison.Ordinal)))
        {
            Assert.Contains($"`{surface}`", bullet, StringComparison.Ordinal);
        }

        Assert.Contains("`POST /api/intelligence/context/inspect` is not in it", bullet, StringComparison.Ordinal);
    }

    /// <summary>
    /// The no-progress paragraph in the chat-loop document names the window the detector compares a
    /// completed round against. The behavior half runs the detector: a round recurs inside the window and
    /// not after it has been pushed out.
    /// </summary>
    [Fact]
    public void The_chat_loop_document_states_the_window_the_progress_detector_compares_against()
    {
        static IReadOnlyList<ToolLoopProgressEntry> Round(int index) =>
            [new ToolLoopProgressEntry("read_file_chunk", $"{{\"relativePath\":\"f{index}.txt\"}}", $"result {index}")];

        ToolLoopProgressDetector withinWindow = new();

        Assert.False(withinWindow.ObserveCompletedRound(Round(0)));

        for (int index = 1; index <= 7; index++)
        {
            Assert.False(withinWindow.ObserveCompletedRound(Round(index)));
        }

        // Seven other rounds later it is still one of the last eight.
        Assert.True(withinWindow.ObserveCompletedRound(Round(0)));

        ToolLoopProgressDetector beyondWindow = new();

        Assert.False(beyondWindow.ObserveCompletedRound(Round(0)));

        for (int index = 1; index <= 8; index++)
        {
            Assert.False(beyondWindow.ObserveCompletedRound(Round(index)));
        }

        // Eight other rounds later it has left the window.
        Assert.False(beyondWindow.ObserveCompletedRound(Round(0)));

        string paragraph = DocumentSection(
            ReadDocument("Arcanum.CHAT-LOOP.md"),
            "Progress is state, not elapsed time.",
            "Buffered and streaming paths use the same semantic loop");

        Assert.Contains("the last eight rounds", paragraph, StringComparison.Ordinal);

        Assert.Contains("`ToolLoopProgressDetector`", paragraph, StringComparison.Ordinal);
    }

    private static string Compact(string source) =>
        string.Concat(source.Where(static character => !char.IsWhiteSpace(character)));

    /// <summary>
    /// <c>ServeProcessLauncher</c> spawns the host in the launching command's own process group, so the
    /// terminal signals it only while that command is the terminal's foreground job. After
    /// <c>run</c> or <c>ask</c> has returned the host is still up (job control: the terminal signals its
    /// foreground group and the shell, and the shell no longer lists the finished command), so a
    /// sentence that says Ctrl+C or closing the terminal ends an auto-launched host, or that an
    /// implicit invocation never leaves a listener behind, is false for the common case and
    /// security-relevant. The sentences are true only while the launcher adds no detach step, which is
    /// the source half of the pairing. The design says this is the job-control rule and that the
    /// repository ships no probe of it, because an unreproducible "observed with a probe" is not
    /// evidence a reader can check.
    /// </summary>
    [Fact]
    public void An_auto_launched_host_is_not_documented_as_ending_with_its_terminal()
    {
        string design = ReadDocument("Arcanum.DESIGN.md");

        string commands = ReadDocument("Arcanum.Command.Reference.md");

        string launcher = ReadSource("Cli", "Services", "ServeProcessLauncher.cs");

        Assert.DoesNotContain("setsid(", launcher, StringComparison.OrdinalIgnoreCase);

        Assert.DoesNotContain("DETACHED_PROCESS", launcher, StringComparison.Ordinal);

        Assert.DoesNotContain("CREATE_NEW_PROCESS_GROUP", launcher, StringComparison.Ordinal);

        string[] staleSentences =
        [
            "until its terminal goes away",
            "never leaves a long-lived loopback listener",
            "so Ctrl+C or closing the terminal ends it",
            "Ctrl+C or closing that terminal therefore ends the host",
        ];

        foreach (string stale in staleSentences)
        {
            Assert.DoesNotContain(stale, design, StringComparison.Ordinal);

            Assert.DoesNotContain(stale, commands, StringComparison.Ordinal);
        }

        // The true statement carries the condition that makes it true.
        Assert.Contains("only while that process group is the terminal's foreground job", design, StringComparison.Ordinal);

        Assert.Contains("keeps listening until it is stopped with `arcanum serve quit`", design, StringComparison.Ordinal);

        Assert.Contains("only while that command is still the terminal's foreground job", commands, StringComparison.Ordinal);

        Assert.Contains("keeps running until `arcanum serve quit`", commands, StringComparison.Ordinal);

        Assert.DoesNotContain("pseudo-terminal probe", design, StringComparison.Ordinal);

        Assert.Contains("no probe of it ships here", design, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>CreateOwnerOnlyTempFile</c> makes a temp file owner-only from the create on every platform: a
    /// create mode on Unix and, on Windows, a protected current-user-only security descriptor supplied in
    /// the create call. The design says so once for every site. A sentence that says the helper
    /// "hardens nothing at creation" on Windows, or that a site narrows for itself because it does not,
    /// is false for as long as the helper builds the descriptor, which is the source half of the
    /// pairing.
    /// </summary>
    [Fact]
    public void The_cli_temp_file_posture_is_documented_for_every_platform()
    {
        string design = ReadDocument("Arcanum.DESIGN.md");

        string helper = File
            .ReadAllText(Path.Combine(
                TestRepositoryPaths.RepositoryRoot(),
                "src",
                "RetroDownfall.Arcanum.Infrastructure",
                "Security",
                "SecureFilePermissions.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        // The claim is true only while the helper supplies the Windows descriptor in the create call.
        Assert.Contains("CreateWindowsOwnerOnlyTempFile", helper, StringComparison.Ordinal);

        Assert.Contains("security.SetAccessRuleProtection(", helper, StringComparison.Ordinal);

        Assert.DoesNotContain("On Windows that helper hardens nothing at creation", design, StringComparison.Ordinal);

        Assert.DoesNotContain("keeps the ACL it inherits from the destination directory", design, StringComparison.Ordinal);

        Assert.Contains("a protected current-user-only ACL on Windows", design, StringComparison.Ordinal);

        Assert.Contains("`attachment export` staging file", design, StringComparison.Ordinal);
    }

    /// <summary>
    /// The CLI renders no Markdown: the final answer is the raw stream, and Spectre carries only
    /// diagnostics. The renderer and the swap that erased and repainted a streamed answer were deleted, so
    /// a reference that still describes them sends a reader looking for code that is not there. The
    /// source half is that the renderer's file does not exist; the document half is that no reference
    /// names it or the renderer's swap and Live-region rules.
    /// </summary>
    [Fact]
    public void The_cli_is_not_documented_as_having_a_markdown_renderer_it_no_longer_has()
    {
        string cliSource = Path.Combine(TestRepositoryPaths.RepositoryRoot(), "src", "RetroDownfall.Arcanum.Cli");

        Assert.Empty(Directory.EnumerateFiles(cliSource, "MarkdigSpectreRenderer.cs", SearchOption.AllDirectories));

        string[] references =
        [
            "Arcanum.DESIGN.md",
            "Arcanum.DEBUGGING.Human.md",
            "Arcanum.Engineering.md",
            "Arcanum.CHAT-LOOP.md",
            "Arcanum.Design.Human.md",
            "Arcanum.Command.Reference.md",
        ];

        string[] staleFragments =
        [
            "MarkdigSpectreRenderer",
            "Markdown swap renderer",
            "Spectre swap renderer",
            "end-of-turn Markdown render",
            "The Live region owns the viewport",
        ];

        foreach (string reference in references)
        {
            string text = ReadDocument(reference);

            foreach (string stale in staleFragments)
            {
                Assert.True(
                    !text.Contains(stale, StringComparison.Ordinal),
                    $"{reference} still describes a renderer the CLI no longer has: {stale}");
            }
        }
    }

    private static bool NativeRuntimeValidatorHasProductionCallSite()
    {
        string sourceRoot = Path.Combine(TestRepositoryPaths.RepositoryRoot(), "src");

        return Directory
            .EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path => !Path.GetFileName(path).StartsWith("SqliteNativeRuntimeValidat", StringComparison.Ordinal))
            .Any(path => File.ReadAllText(path).Contains("SqliteNativeRuntimeValidator", StringComparison.Ordinal));
    }

    /// <summary>
    /// A kept-closed schema repair does not stop startup, so no text may borrow the consequence a
    /// restore's kept-closed verdict has.
    /// </summary>
    /// <remarks>
    /// The bootstrapper's schema-repair arm logs one warning and goes on to publish readiness: the
    /// process serves with the adopted owner's Covenant admission shut, or, when only the one-shot
    /// post-disposition finalizer failed after the gate reopened, with the gate open and the journal alone
    /// still active. The restore arm is the one that throws. A sentence that says the schema-repair
    /// verdict withholds readiness, or that its process "never serves", is the restore's sentence and is
    /// false here, and the finalizer-only case is the one a reader acts on.
    /// </remarks>
    [Fact]
    public void A_kept_closed_schema_repair_is_not_documented_as_stopping_startup()
    {
        string bootstrapper = ReadSource("Infrastructure", "Hosting", "GrimoireDatabaseBootstrapper.cs");

        string repairArm = DocumentSection(
            bootstrapper,
            "if (recovered.Value is CovenantSchemaRepairStartupRecoveryOutcome.KeptClosed)",
            "return new ProtectedMaintenanceRecovery(gate, adoptedErasureOwner);");

        Assert.Contains("Log.Warning(", repairArm, StringComparison.Ordinal);

        Assert.DoesNotContain("throw", repairArm, StringComparison.Ordinal);

        string restoreArm = DocumentSection(
            bootstrapper,
            "recovered.Value is BackupRestoreStartupRecoveryOutcome.KeptClosed",
            "private static BackupRestoreRecovery? TryCreateRestoreRecovery(");

        Assert.Contains("throw new GrimoireDatabaseUnavailableException", restoreArm, StringComparison.Ordinal);

        // The remarks wrap, so a sentence is read with its line breaks and comment markers folded to a space.
        string recovery = Regex.Replace(
            ReadSource("Infrastructure", "Covenant", "CovenantSchemaRepairStartupRecovery.cs"),
            @"\s*\n\s*///\s?",
            " ",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));

        string paragraph = Assert.Single(
            ReadDocument("Arcanum.DESIGN.md").Split('\n'),
            static line => line.StartsWith("**Schema repair is journal-first and narrow.**", StringComparison.Ordinal));

        foreach (string stale in (string[])
                 [
                     "never serves it",
                     "stops at this verdict",
                     "readiness must not be published",
                     "blocks bootstrap",
                     "so startup stays closed",
                 ])
        {
            Assert.DoesNotContain(stale, recovery, StringComparison.Ordinal);

            Assert.DoesNotContain(stale, paragraph, StringComparison.Ordinal);
        }

        Assert.Contains("does not stop", recovery, StringComparison.Ordinal);

        Assert.Contains("does not stop startup", paragraph, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gate's class remarks describe the revocation-fault log as the fixed message it is, not as a
    /// count.
    /// </summary>
    /// <remarks>
    /// <c>RequestRevocation</c> once logged how many callbacks faulted and now logs one fixed template
    /// (reading the count meant classifying a framework collection property in the hosted-producer
    /// analyzer). The remarks kept saying the fault was "logged by count only", which is the opposite of a
    /// template with no placeholder.
    /// </remarks>
    [Fact]
    public void The_gate_remarks_describe_the_revocation_fault_log_as_the_fixed_message_it_is()
    {
        string source = ReadSource("Infrastructure", "Covenant", "CovenantOperationGate.cs");

        string log = DocumentSection(source, "_gate._logger.LogWarning(", ");");

        Assert.DoesNotContain("{", log, StringComparison.Ordinal);

        // The remarks wrap, so a sentence is read with its line breaks and comment markers folded to a space.
        string gate = Regex.Replace(
            source,
            @"\s*\n\s*///\s?",
            " ",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(5));

        foreach (string stale in (string[])["by count only", "logs is that count"])
        {
            Assert.DoesNotContain(stale, gate, StringComparison.Ordinal);
        }

        Assert.Contains("fixed message", gate, StringComparison.Ordinal);
    }

    /// <summary>
    /// An ordinary installation reset plans under the stopped-host maintenance lock and asks the host
    /// for its plan only on the external-remediation arm, so no document may tell an operator that an
    /// ordinary global or all reset must reach the running host and rebind its plan first.
    /// </summary>
    [Fact]
    public void Ordinary_factory_reset_is_documented_as_planned_under_the_stopped_host_lock()
    {
        string command = ReadSource("Cli", "Commands", "InstallationFactoryResetCommand.cs");

        Assert.Contains("PlanUnderStoppedHostLockAsync", command, StringComparison.Ordinal);

        Assert.Matches(
            @"if \(externalRemediation is not null\s+&& plan\.Scope is InstallationResetScope\.Global",
            command);

        string reference = ReadDocument("Arcanum.Command.Reference.md");

        Assert.DoesNotContain("Global/all rebind the authenticated online plan", reference, StringComparison.Ordinal);

        Assert.DoesNotContain("Global/all first obtains the authenticated host plan", reference, StringComparison.Ordinal);

        Assert.Contains("maintenance-lock contention", reference, StringComparison.Ordinal);

        foreach ((string document, string stale) in (ValueTuple<string, string>[])[
            ("Arcanum.Design.Human.md", "the command must reach the authenticated host"),
            ("Arcanum.Design.Human.md", "After confirmation, global/all creates a typed handoff"),
            ("Arcanum.Engineering.md", "the CLI must reach the authenticated host"),
            ("Arcanum.Engineering.md", "global/all apply re-plans and creates a typed handoff"),
            ("Arcanum.API.md", "A global/all installation reset obtains this authenticated online plan"),
            ("Arcanum.DESIGN.md", "Global/all first build the local inventory, ask the authenticated running host"),
            ("Arcanum.DESIGN.md", "After confirmation, `PrepareAsync` re-plans"),
            ("Arcanum.CHAT-LOOP.md", "apply first asks the running authenticated host to publish"),
            ("Arcanum.OATH.md", "binds the authenticated host's exact current factory-plan identity"),
            ("Arcanum.OATH.md", "After confirmation the CLI sends a typed handoff in memory"),
        ])
        {
            Assert.DoesNotContain(stale, ReadDocument(document), StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The design says the current CLI mints no new installation-reset host handoff and only rebuilds
    /// one from an authenticated active record, so no contract or CLI source may keep a factory for a
    /// fresh handoff that nothing calls.
    /// </summary>
    [Fact]
    public void The_cli_keeps_no_factory_for_a_fresh_installation_reset_host_handoff()
    {
        Assert.Contains("the current CLI mints no new one", ReadDocument("Arcanum.DESIGN.md"), StringComparison.Ordinal);

        foreach (string source in (string[])[
            ReadSource("Core", "DataLifecycle", "InstallationResetContracts.cs"),
            ReadSource("Cli", "Commands", "InstallationResetApplyBoundary.cs"),
            ReadSource("Infrastructure", "InstallationReset", "InstallationResetService.cs"),
        ])
        {
            Assert.DoesNotContain("CreateHostHandoff", source, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A failed accounting settlement keeps a batch <c>in_progress</c> for durable recovery, so the API
    /// contract has to name it among the failures that do not end the batch as <c>failed</c>.
    /// </summary>
    [Fact]
    public void The_batch_status_row_names_a_failed_settlement_as_kept_for_recovery()
    {
        string service = ReadSource("Api", "Intelligence", "BatchProcessingService.cs");

        Assert.Contains("!state.AccountingSettlementFailed", service, StringComparison.Ordinal);

        string row = ReadDocument("Arcanum.API.md")
            .Split('\n')
            .Single(static line => line.StartsWith("| GET | `/v1/batches/{id}` |", StringComparison.Ordinal));

        Assert.Contains("failed accounting settlement", row, StringComparison.Ordinal);
    }

    /// <summary>
    /// The SSE consumer discards an event over its runaway guard, which the server can reach, so the
    /// paragraph must not open by promising a cap the server could never reach.
    /// </summary>
    [Fact]
    public void The_sse_consumer_paragraph_does_not_promise_an_unreachable_cap()
    {
        string api = ReadDocument("Arcanum.API.md");

        Assert.DoesNotContain("without a client-only payload cap that the server could reach", api, StringComparison.Ordinal);

        Assert.Contains("An event over the limit is discarded", api, StringComparison.Ordinal);

        string debugging = ReadDocument("Arcanum.DEBUGGING.Human.md");

        Assert.DoesNotContain("reassembled without a client-only size cap", debugging, StringComparison.Ordinal);

        Assert.Contains("runaway guard", debugging, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>config</c> and <c>completion</c> both call the host; they are outside the exit-code-3 rule
    /// because they fall back locally, not because they never ask the host.
    /// </summary>
    [Fact]
    public void The_exit_code_table_does_not_say_config_or_completion_never_ask_the_host()
    {
        Assert.Contains(
            ".UpdateConfigurationAsync(settings, cancellationToken)",
            ReadSource("Cli", "Services", "ConfigurationCommandService.cs"),
            StringComparison.Ordinal);

        Assert.Contains(
            "apiClient.GetModelsAsync",
            ReadSource("Cli", "Services", "CliCompletionResolver.cs"),
            StringComparison.Ordinal);

        string row = ReadDocument("Arcanum.Command.Reference.md")
            .Split('\n')
            .Single(static line => line.StartsWith("| `3` |", StringComparison.Ordinal));

        Assert.DoesNotContain("`key`, `config` or `completion`", row, StringComparison.Ordinal);

        Assert.Contains("`config`", row, StringComparison.Ordinal);

        Assert.Contains("`completion`", row, StringComparison.Ordinal);
    }

    /// <summary>
    /// With retrieval on, the context preview opens embedding runs and writes ledger rows, so neither
    /// the human design summary nor the provider's own documentation may call it read-only.
    /// </summary>
    [Fact]
    public void The_context_preview_is_not_called_read_only()
    {
        Assert.Contains(
            "The preview therefore does write run rows and ledger rows",
            ReadDocument("Arcanum.DESIGN.md"),
            StringComparison.Ordinal);

        Assert.DoesNotContain("A dry, read-only pre-inference plan", ReadDocument("Arcanum.Design.Human.md"), StringComparison.Ordinal);

        Assert.DoesNotContain("or read-only preview run", ReadDocument("Arcanum.DESIGN.md"), StringComparison.Ordinal);

        Assert.DoesNotContain(
            "the read-only context preview",
            ReadSource("Api", "Intelligence", "WizardIntelligenceProvider.cs"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The turn engine records three termination reasons; the design table may not name terminal
    /// reasons the engine has no value for.
    /// </summary>
    [Fact]
    public void The_terminal_reason_table_names_only_reasons_the_engine_records()
    {
        string enums = ReadSource("Api", "Intelligence", "TurnEngine", "TurnEnums.cs");

        string declaration = DocumentSection(enums, "internal enum TurnTerminationReason", "}");

        string[] members = [.. Regex
            .Matches(declaration, @"^\s+(?<name>[A-Z][A-Za-z]+) = \d+,", RegexOptions.Multiline, TimeSpan.FromSeconds(5))
            .Select(static match => match.Groups["name"].Value)];

        Assert.Equal(new[] { "Completed", "ProviderFailure", "Cancelled" }, members);

        string design = ReadDocument("Arcanum.DESIGN.md");

        foreach (string removed in (string[])["`explicit_budget`", "`provider_or_context_boundary`", "`safety_or_integrity_boundary`", "`client_tool_forwarded`"])
        {
            Assert.DoesNotContain(removed, design, StringComparison.Ordinal);
        }

        string loop = DocumentSection(design, "There is no arbitrary model-call", "#### 10.7.4 Ward/Sanctum");

        foreach (string member in members)
        {
            Assert.Contains($"`{member}`", loop, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The turn execution facade has two methods since the OpenAI SSE projection was removed.
    /// </summary>
    [Fact]
    public void The_design_counts_the_turn_execution_facade_methods_correctly()
    {
        string facade = ReadSource("Api", "Intelligence", "TurnEngine", "ITurnExecutionFacade.cs");

        Assert.Equal(2, Regex.Matches(facade, @"\bExecute\w+Async\(", RegexOptions.None, TimeSpan.FromSeconds(5)).Count);

        string design = ReadDocument("Arcanum.DESIGN.md");

        Assert.DoesNotContain("all three `ITurnExecutionFacade` methods", design, StringComparison.Ordinal);

        Assert.Contains("both `ITurnExecutionFacade` methods", design, StringComparison.Ordinal);
    }

    /// <summary>
    /// The Compendium launcher looks for the development project in the executable's directory and
    /// every ancestor, so the configuration reference may not say it is only found beside the executable.
    /// </summary>
    [Fact]
    public void The_development_launcher_is_documented_as_searching_ancestors()
    {
        Assert.Contains(
            "Directory.GetParent(directory)",
            ReadSource("Core", "Desktop", "CompendiumLauncher.cs"),
            StringComparison.Ordinal);

        string compendium = ReadDocument("Compendium.README.md");

        Assert.DoesNotContain("found beside the executable", compendium, StringComparison.Ordinal);

        Assert.Contains("each of its ancestors", compendium, StringComparison.Ordinal);
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
