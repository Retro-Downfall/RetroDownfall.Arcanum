"""Pin explicit operator selection and fail-closed required TRX evidence."""
import copy
import importlib.util
import json
from pathlib import Path
import re
import tempfile
import unittest
import xml.etree.ElementTree as ET

from scripts.tests.test_ci_scope_workflow import ROOT, WORKFLOW, job, step, matrix_hosts, selected_inventory
from scripts.tests.trx_fixture import NS, append_summary

MANIFEST = ROOT / "tests/RetroDownfall.Arcanum.Tests/TestData/Contracts/Arcanum.GuildOperatorQualification.json"
TOOL = ROOT / "scripts/guild_operator_qualification.py"
PREFIX = "RetroDownfall.Arcanum.Tests."


def authored_methods(path):
    source = path.read_text()
    namespace = re.search(r"namespace ([\w.]+);", source).group(1)
    owner = re.search(r"public (?:sealed )?(?:partial )?class (\w+)", source).group(1)
    pattern = r"^\s*\[(?:Skippable)?(?:Fact|Theory)(?:\([^\]]*\))?\][\s\S]*?\bpublic\s+(?:async\s+)?(?:Task(?:<[^>]+>)?|void)\s+(\w+)\s*\("
    return {namespace + "." + owner + "." + name for name in re.findall(pattern, source, re.M)}


def authored_case_rows(path):
    source = path.read_text()
    namespace = re.search(r"namespace ([\w.]+);", source).group(1)
    owner = re.search(r"public (?:sealed )?(?:partial )?class (\w+)", source).group(1)
    pattern = r"(^\s*\[(?:Skippable)?(Fact|Theory)(?:\([^\]]*\))?\][\s\S]*?)\bpublic\s+(?:async\s+)?(?:Task(?:<[^>]+>)?|void)\s+(\w+)\s*\("
    result = {}
    for attributes, kind, method in re.findall(pattern, source, re.M):
        count = len(re.findall(r"^\s*\[InlineData\(", attributes, re.M)) if kind == "Theory" else 1
        if count == 0 or "MemberData" in attributes or "ClassData" in attributes:
            raise AssertionError("Review dynamic theory row inventory explicitly: " + method)
        result[namespace + "." + owner + "." + method] = count
    return result


def authored_scheduler_displays(path):
    source = path.read_text()
    namespace = re.search(r"namespace ([\w.]+);", source).group(1)
    owner = re.search(r"public (?:sealed )?(?:partial )?class (\w+)", source).group(1)
    pattern = r"(^\s*\[(?:Skippable)?(Fact|Theory)(?:\([^\]]*\))?\][\s\S]*?)\bpublic\s+(?:async\s+)?(?:Task(?:<[^>]+>)?|void)\s+(\w+)\s*\(([^)]*)\)"
    result = {}
    for attributes, kind, method, parameters in re.findall(pattern, source, re.M):
        case = namespace + "." + owner + "." + method
        names = [parameter.strip().split()[-1] for parameter in parameters.split(",") if parameter.strip()]
        displays = []
        if kind == "Fact":
            displays.append(case)
        else:
            for inline in re.findall(r"^\s*\[InlineData\((.*?)\)\]", attributes, re.M):
                values = json.loads("[" + inline + "]")
                if len(values) != len(names): raise AssertionError("Scheduler parameter inventory mismatch")
                rendered = []
                for name, value in zip(names, values):
                    text = str(value) if isinstance(value, bool) else json.dumps(value, ensure_ascii=False)
                    if isinstance(value, str) and len(value) > 50:
                        # xUnit 2.9.3 ArgumentFormatter: these reviewed plain ASCII values
                        # keep the first 50 characters, then append three middle dots outside quotes.
                        if any(ord(char) < 32 or ord(char) > 126 or char in '\\"' for char in value):
                            raise AssertionError("Review escaped or Unicode long argument display explicitly: " + method)
                        text = json.dumps(value[:50], ensure_ascii=False) + "···"
                    rendered.append(name + ": " + text)
                displays.append(case + "(" + ", ".join(rendered) + ")")
        result[case] = sorted(displays)
    return result


class GuildOperatorQualificationTests(unittest.TestCase):
    def tool(self):
        self.assertTrue(TOOL.is_file(), "The operator profile needs an executable selection/evidence contract")
        spec = importlib.util.spec_from_file_location("scripts.guild_operator_qualification", TOOL)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        return module

    def manifest(self):
        self.assertTrue(MANIFEST.is_file(), "The operator witness manifest is missing")
        return self.tool().load_manifest(MANIFEST)[0]

    def fixture_manifest(self):
        manifest = copy.deepcopy(self.manifest())
        manifest["projects"]["core"] = {"requiredClasses": [PREFIX + "Fixture.Required"],
            "requiredCases": [PREFIX + "Fixture.Other.required_case"], "caseSources": [],
            "requiredCaseRows": {PREFIX + "Fixture.Other.required_case": 1}, "requiredCaseDisplays": {}}
        manifest["platformCases"] = {"macos": [], "windows": []}
        manifest["allowedSkips"] = {"macos": [], "windows": []}
        return manifest

    def report(self, rows, outcome="Completed"):
        root = ET.Element("TestRun", xmlns=NS)
        definitions = ET.SubElement(root, "TestDefinitions")
        results = ET.SubElement(root, "Results")
        for index, (owner, method, status, *display) in enumerate(rows):
            identity = str(index)
            unit = ET.SubElement(definitions, "UnitTest", id=identity)
            ET.SubElement(unit, "TestMethod", className=owner, name=method)
            ET.SubElement(results, "UnitTestResult", testId=identity, outcome=status,
                testName=display[0] if display else owner + "." + method)
        append_summary(root, [row[2] for row in rows]).set("outcome", outcome)
        return ET.tostring(root)

    def test_operator_dispatch_selects_only_its_requested_platform_and_support(self):
        support = {("scope", None), ("sdk", None), ("native-sqlcipher-assets", None), ("static-analysis", None)}
        for platform, hosts in (("both", ["macos-26", "windows-latest"]), ("macos", ["macos-26"]),
                                ("windows", ["windows-latest"]), ("", ["macos-26", "windows-latest"])):
            with self.subTest(platform=platform):
                selected, _ = selected_inventory("workflow_dispatch", "guild-operator", platform)
                self.assertEqual(support | {("guild-operator", host) for host in hosts}, set(selected))
        focused = job("guild-operator")
        self.assertIn("timeout-minutes: ${{ inputs.operator-native-aot && matrix.os == 'windows-latest' && 150 || 90 }}", focused)
        self.assertIn("run: ./scripts/qualify-guild-operator.sh", focused)
        self.assertIn("if: always()", focused)
        self.assertNotIn("continue-on-error", focused)
        self.assertEqual(matrix_hosts(job("guild-execution"), "both"), matrix_hosts(focused, "both"))

    def test_required_suites_include_shared_protocol_and_complete_store_recovery_families(self):
        required = set(self.manifest()["projects"]["core"]["requiredClasses"])
        for name in ("Cli.WatchSseParserTests", "Cli.WatchSseTransportTests", "Cli.WatchCommandTests",
                     "Cli.CliJsonContextCoverageTests", "Api.Serialization.ArcanumJsonContextCompletenessTests",
                     "Api.Tower.CampaignEndpointTests", "Guild.GuildQuestStoreTests", "Guild.GuildPortalStoreTests",
                     "Guild.GuildQuestRecoveryTests", "Cli.GuildCommandTests", "Api.GuildEndpointTests",
                     "Data.ExactUsdTextTests"):
            self.assertIn(PREFIX + name, required)

    def test_every_port_scheduler_and_declared_partial_method_is_a_required_case(self):
        manifest = self.manifest()["projects"]["core"]
        sources = set(manifest["caseSources"])
        directory = ROOT / "tests/RetroDownfall.Arcanum.Tests/Guild"
        affected = {str(path.relative_to(ROOT)) for path in directory.glob("GuildLocalHostPolicyTests.*.cs")
                    if "GuildLocalPort" in path.read_text() or "Scheduler" in path.name or ".Port" in path.name}
        self.assertTrue(affected)
        self.assertLessEqual(affected, sources)
        self.assertIn("tests/RetroDownfall.Arcanum.Tests/Guild/GuildLocalHostPolicyTests.SchedulerRecovery.cs", sources)
        declared = {case for source in sources for case in authored_methods(ROOT / source)}
        self.assertLessEqual(declared, set(manifest["requiredCases"]))
        for source in sources:
            for case, rows in authored_case_rows(ROOT / source).items():
                self.assertEqual(rows, manifest["requiredCaseRows"].get(case), case)
            if "Scheduler" in Path(source).name:
                for case, displays in authored_scheduler_displays(ROOT / source).items():
                    self.assertEqual(displays, manifest["requiredCaseDisplays"].get(case), case)
        self.assertIn(PREFIX + "Guild.GuildLocalHostPolicyTests.Local_scheduler_recovers_committed_start_and_coalesces_wakes_before_rebinding_live_work", declared)
        self.assertIn(PREFIX + "Guild.GuildLocalHostPolicyTests.Scheduler_retries_a_failed_pre_dispatch_commit_without_a_stale_operation_lease", declared)

    def test_every_named_witness_resolves_to_a_current_authored_test_method(self):
        manifest = self.manifest()
        authored = set()
        for path in (ROOT / "tests").rglob("*.cs"):
            source = path.read_text()
            if re.search(r"namespace ([\w.]+);", source) and re.search(r"public (?:sealed )?(?:partial )?class (\w+)", source):
                authored.update(authored_methods(path))
        required = {case for project in manifest["projects"].values() for case in project["requiredCases"]}
        required.update(case for cases in manifest["platformCases"].values() for case in cases)
        self.assertEqual([], sorted(required - authored), "Named qualification witnesses must follow semantic test renames")

    def test_filter_selects_complete_classes_and_every_required_case_without_inherited_wildcards(self):
        module = self.tool()
        manifest = self.manifest()
        selection = module.make_filter(manifest, "core", "macos")
        for name in manifest["projects"]["core"]["requiredClasses"]:
            self.assertIn("FullyQualifiedName~" + name + ".", selection)
        for name in manifest["projects"]["core"]["requiredCases"] + manifest["platformCases"]["macos"]:
            self.assertIn("FullyQualifiedName=" + name, selection)
        for broad in ("FullyQualifiedName~Guild|", "FullyQualifiedName~RetroDownfall.Arcanum.Tests.Mcp|",
                      "FullyQualifiedName~WizardIntelligenceProviderTests", "FullyQualifiedName~DataRetention|"):
            self.assertNotIn(broad, selection)
        self.assertNotIn("FullyQualifiedName=" + PREFIX + "Guild.GuildWorkspaceLeaseHostLockTests.Windows_retained_no_delete_sharing_prevents_lock_leaf_replacement", selection)
        self.assertIn("Category!=Perf", selection)

    def test_evidence_requires_every_suite_and_every_case_and_rejects_partial_runs(self):
        module = self.tool()
        manifest = self.fixture_manifest()
        required = (PREFIX + "Fixture.Required", "passes", "Passed")
        case = (PREFIX + "Fixture.Other", "required_case", "Passed")
        self.assertEqual(2, module.validate_evidence(self.report([required, case]), manifest, "core", "macos")["passedTests"])
        for rows, outcome in (([case], "Completed"), ([required], "Completed"),
                ([required, case, (PREFIX + "Fixture.Other", "required_case", "NotExecuted")], "Completed"),
                ([required, case, (PREFIX + "Fixture.Other", "failure", "Failed")], "Completed"),
                ([required, case], "Aborted")):
            with self.subTest(rows=rows, outcome=outcome), self.assertRaises(ValueError):
                module.validate_evidence(self.report(rows, outcome), manifest, "core", "macos")

    def test_a_completed_report_cannot_omit_an_expanded_scheduler_variant(self):
        module = self.tool()
        manifest = self.fixture_manifest()
        manifest["projects"]["core"]["requiredCaseRows"][PREFIX + "Fixture.Other.required_case"] = 2
        rows = [(PREFIX + "Fixture.Required", "passes", "Passed"),
                (PREFIX + "Fixture.Other", "required_case", "Passed")]
        with self.assertRaises(ValueError):
            module.validate_evidence(self.report(rows), manifest, "core", "macos")
        rows.append((PREFIX + "Fixture.Other", "required_case", "Passed"))
        self.assertEqual(3, module.validate_evidence(self.report(rows), manifest, "core", "macos")["passedTests"])

    def test_duplicate_scenario_cannot_replace_a_missing_distinct_scheduler_variant(self):
        module = self.tool()
        manifest = self.fixture_manifest()
        case = PREFIX + "Fixture.Other.required_case"
        first, second = case + '(scenario: "first")', case + '(scenario: "second")'
        manifest["projects"]["core"]["requiredCaseRows"][case] = 2
        manifest["projects"]["core"]["requiredCaseDisplays"][case] = [first, second]
        rows = [(PREFIX + "Fixture.Required", "passes", "Passed"),
                (PREFIX + "Fixture.Other", "required_case", "Passed", first),
                (PREFIX + "Fixture.Other", "required_case", "Passed", first)]
        with self.assertRaises(ValueError):
            module.validate_evidence(self.report(rows), manifest, "core", "macos")
        rows[-1] = (PREFIX + "Fixture.Other", "required_case", "Passed", second)
        self.assertEqual(3, module.validate_evidence(self.report(rows), manifest, "core", "macos")["passedTests"])

    def test_recorded_runsettings_preserve_the_complete_filter_without_shell_truncation(self):
        module = self.tool()
        selection = module.make_filter(self.manifest(), "core", "windows")
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "operator.runsettings"
            module.write_runsettings(path, selection)
            self.assertEqual(selection, ET.parse(path).findtext("RunConfiguration/TestCaseFilter"))
            self.assertEqual([path], list(Path(temporary).iterdir()))
        script = (ROOT / "scripts/qualify-guild-operator.sh").read_text()
        self.assertIn('--settings "$results/core.runsettings"', script)
        self.assertIn('--settings "$results/compendium.runsettings"', script)
        self.assertIn('--settings-output "$results/core.runsettings"', script)

    def test_only_exact_declared_platform_skips_are_admitted(self):
        module = self.tool()
        manifest = self.fixture_manifest()
        display = PREFIX + 'Fixture.Required.platform_case(shape: "symlink")'
        manifest["allowedSkips"]["windows"] = [display]
        rows = [(PREFIX + "Fixture.Required", "passes", "Passed"),
                (PREFIX + "Fixture.Other", "required_case", "Passed"),
                (PREFIX + "Fixture.Required", "platform_case", "NotExecuted", display)]
        self.assertEqual(2, module.validate_evidence(self.report(rows), manifest, "core", "windows")["passedTests"])
        with self.assertRaises(ValueError):
            module.validate_evidence(self.report(rows), manifest, "core", "macos")
        rows[-1] = (PREFIX + "Fixture.Other", "forged_identity", "NotExecuted", display)
        with self.assertRaises(ValueError):
            module.validate_evidence(self.report(rows), manifest, "core", "windows")

    def test_completion_permissions_witness_cannot_skip_on_either_platform(self):
        module = self.tool()
        manifest = self.fixture_manifest()
        manifest["allowedSkips"] = self.manifest()["allowedSkips"]
        method = "Completion_install_leaves_an_existing_target_directory_permissions_alone"
        display = PREFIX + "Cli.CliCompletionTests." + method
        rows = [(PREFIX + "Fixture.Required", "passes", "Passed"),
                (PREFIX + "Fixture.Other", "required_case", "Passed")]
        for platform in ("windows", "macos"):
            with self.subTest(platform=platform):
                passed = rows + [(PREFIX + "Cli.CliCompletionTests", method, "Passed", display)]
                self.assertEqual(3, module.validate_evidence(self.report(passed), manifest, "core", platform)["passedTests"])
                skipped = rows + [(PREFIX + "Cli.CliCompletionTests", method, "NotExecuted", display)]
                with self.assertRaisesRegex(ValueError, "unexpected skipped operator witness"):
                    module.validate_evidence(self.report(skipped), manifest, "core", platform)

    def test_allowlisted_fact_skip_refuses_forged_displays_and_definitions(self):
        module = self.tool()
        manifest = self.fixture_manifest()
        display = PREFIX + "Fixture.Required.unix_permissions"
        manifest["allowedSkips"]["windows"] = [display]
        passed = [(PREFIX + "Fixture.Required", "passes", "Passed"),
                  (PREFIX + "Fixture.Other", "required_case", "Passed")]
        for skipped in ((PREFIX + "Fixture.Other", "unix_permissions", "NotExecuted", display),
                        (PREFIX + "Fixture.Required", "other_method", "NotExecuted", display),
                        (PREFIX + "Fixture.Required", "unix_permissions", "NotExecuted", display + "Other"),
                        (PREFIX + "Fixture.Required", "unix_permissions", "NotExecuted", display + '(shape: "forged")')):
            with self.subTest(skipped=skipped), self.assertRaisesRegex(ValueError, "unexpected skipped operator witness"):
                module.validate_evidence(self.report(passed + [skipped]), manifest, "core", "windows")
        manifest["allowedSkips"]["windows"] = []
        with self.assertRaisesRegex(ValueError, "unexpected skipped operator witness"):
            module.validate_evidence(self.report(passed + [(PREFIX + "Fixture.Required", "unix_permissions", "NotExecuted", display)]),
                                     manifest, "core", "windows")

    def test_allowlisted_fact_skip_cannot_replace_a_mandatory_case(self):
        module = self.tool()
        manifest = self.fixture_manifest()
        display = PREFIX + "Fixture.Other.required_case"
        manifest["allowedSkips"]["windows"] = [display]
        rows = [(PREFIX + "Fixture.Required", "passes", "Passed"),
                (PREFIX + "Fixture.Other", "required_case", "NotExecuted", display)]
        with self.assertRaisesRegex(ValueError, "required case.*non-passing"):
            module.validate_evidence(self.report(rows), manifest, "core", "windows")

    def test_manifest_refuses_duplicate_selectors_unknown_fields_and_filter_injection(self):
        module = self.tool()
        original = self.fixture_manifest()
        for mutation in ("duplicate", "unknown", "injection"):
            manifest = copy.deepcopy(original)
            if mutation == "duplicate": manifest["projects"]["core"]["requiredClasses"] *= 2
            elif mutation == "unknown": manifest["extra"] = True
            else: manifest["projects"]["core"]["requiredCases"] = ["Fixture.Test|FullyQualifiedName~Everything"]
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temporary:
                path = Path(temporary) / "manifest.json"
                path.write_text(json.dumps(manifest))
                with self.assertRaises(ValueError): module.load_manifest(path)

    def test_long_ascii_inline_argument_uses_xunit_quoted_prefix_and_middle_dots(self):
        with tempfile.TemporaryDirectory() as temporary:
            source = Path(temporary) / "LongArgument.cs"
            source.write_text('''namespace Fixture;
public sealed class LongArgumentTests
{
    [Theory]
    [InlineData("Unhandled exception. System.IO.IOException: Failed to bind to address http://127.0.0.1:1234: address already in use.", true)]
    [InlineData("Peer credential unavailable", false)]
    public void Classifies(string output, bool expected) { }
}
''')
            case = "Fixture.LongArgumentTests.Classifies"
            self.assertEqual({case: [
                case + '(output: "Peer credential unavailable", expected: False)',
                case + '(output: "Unhandled exception. System.IO.IOException: Failed"···, expected: True)',
            ]}, authored_scheduler_displays(source))

    def test_spend_refresh_partials_and_runtime_contracts_are_explicit_witnesses(self):
        declaration = self.manifest()["projects"]["core"]
        sources = set(declaration["caseSources"])
        directory = ROOT / "tests/RetroDownfall.Arcanum.Tests/Guild"
        affected = {str(path.relative_to(ROOT)) for path in directory.glob("GuildLocalHostPolicyTests.Refresh*.cs")
                    if authored_methods(path)}
        affected.update("tests/RetroDownfall.Arcanum.Tests/Guild/GuildLocalHostPolicyTests." + name + ".cs"
                        for name in ("SpendCorrections", "SpendRefreshAfterRevocation"))
        affected.update("tests/RetroDownfall.Arcanum.Tests/Data/DataRetentionServiceTests." + name + ".cs"
                        for name in ("GuildRefreshPins", "GuildSpendInventory", "GuildCleanup"))
        affected.update("tests/RetroDownfall.Arcanum.Tests/Operations/HostedGrimoireProducerInventoryTests." + name + ".cs"
                        for name in ("RefreshTrackerSelector", "NestedHeartbeatAdmission", "ActualGuildHeartbeatFinally"))
        self.assertLessEqual(affected, sources)
        for source in sorted(affected):
            for case, displays in authored_scheduler_displays(ROOT / source).items():
                self.assertEqual(displays, declaration["requiredCaseDisplays"].get(case), case)
        for name in (
            "Api.GrimoireMaintenanceLiveGuildRefreshTests",
            "Api.GuildRefreshRetryEndpointTests",
            "Backup.BackupRestoreRefreshRecoveryTests",
            "Configuration.GuildUsageReconciliationConfigurationTests",
            "Data.ExactUsdTextAccumulationTests",
            "Data.Schema.GuildRefreshNormalizedIndexTests",
            "Data.Schema.GuildSpendRefreshIndexTests",
            "Data.Schema.GuildSpendSchemaTests",
            "Guild.GuildOpenRouterSpendParserTests",
            "Guild.GuildSpendCorrectionPolicyTests",
            "Guild.GuildSpendRefreshConfigurationTests",
            "Guild.GuildSpendRefreshTrackerCleanupScopeTests",
            "Guild.GuildSpendRefreshTrackerOperationTests",
            "Guild.GuildSpendSerializationTests",
            "Operations.FakeLongRunningOperationStoreTests",
            "Operations.LongRunningOperationRecoveryAdmissionTests",
            "Operations.LongRunningOperationRecoveryRegistryTests",
            "Operations.RecoveryHandlerCoverageTests",
            "Operations.RecoveryHandlerServingCompositionTests",
            "Security.GuildSpendRefreshCredentialMutationTests",
        ):
            self.assertIn(PREFIX + name, declaration["requiredClasses"])
        for name in ("Api.GrimoireMaintenanceLiveGuildRefreshTests.Factory_reset_waits_for_real_terminal_refresh_body_disposal_and_heartbeat",
                     "Api.GrimoireMaintenanceLiveGuildRefreshTests.Factory_reset_no_refresh_control_with_same_capture_fixture_reaches_commit",
                     "Build.AiReflectionProductionClosureTests.Every_compiled_production_project_is_free_of_unsafe_ai_reflection_entry_points",
                     "Data.UtcInstantPersistenceBoundaryTests.Every_direct_SQL_instant_parameter_is_canonical_at_its_binding_site"):
            self.assertEqual(1, declaration["requiredCaseRows"].get(PREFIX + name))
        compendium = self.manifest()["projects"]["compendium"]
        for name in ("FamiliarProviderEditorTests.Guild_source_survives_ordinary_edits_but_is_cleared_when_switching_to_a_Familiar",
                     "GenericSettingsSectionViewTests.Existing_Guild_reconciliation_configuration_is_editable_in_the_provider_page"):
            self.assertEqual(1, compendium["requiredCaseRows"].get("RetroDownfall.Compendium.Ux.Tests.Compendium." + name))

    def test_operator_shell_records_clean_ci_candidate_before_and_after_evidence(self):
        script = (ROOT / "scripts/qualify-guild-operator.sh").read_text()
        self.assertIn("git rev-parse 'HEAD^{tree}' > \"$results/tree.txt\"", script)
        self.assertEqual(2, script.count('if [[ "${GITHUB_ACTIONS:-}" == true ]]; then'))
        self.assertIn('test "$(cat "$results/commit.txt")" = "$GITHUB_SHA"', script)
        self.assertIn('test ! -s "$results/worktree-status.txt"', script)
        self.assertIn('test "$(git rev-parse HEAD)" = "$GITHUB_SHA"', script)
        self.assertIn('test ! -s "$results/worktree-status-after.txt"', script)

    def test_windows_native_gates_are_opt_in_and_reuse_the_existing_fail_closed_scripts(self):
        workflow = WORKFLOW.read_text()
        declaration = re.search(r"^      operator-native-aot:\n(.*?)(?=^      [^ ]|^permissions:)", workflow, re.M | re.S)
        self.assertIsNotNone(declaration)
        self.assertRegex(declaration.group(1), r"(?m)^        type: boolean$")
        self.assertRegex(declaration.group(1), r"(?m)^        default: false$")
        focused = job("guild-operator")
        names = (
            "Install ripgrep for optional Windows native gates",
            "Audit optional win-x64 Native AOT diagnostics",
            "Verify optional Guild Native AOT graph (win-x64)",
            "Verify optional win-x64 shipping executable and provider-contract smoke",
            "Record completed optional Windows native candidate",
        )
        for name in names:
            body = step(focused, name)
            self.assertIn("if: inputs.operator-native-aot && runner.os == 'Windows'", body)
            self.assertNotIn("continue-on-error", body)
        for name in re.findall(r"^  ([\w-]+):", workflow.split("\njobs:\n", 1)[1], re.M):
            if name != "guild-operator":
                self.assertNotIn("inputs.operator-native-aot", job(name), name)
        self.assertIn("choco install ripgrep --version=15.2.0 --yes --no-progress", step(focused, names[0]))
        self.assertIn("rg --version", step(focused, names[0]))
        commands = ("./scripts/verify-aot-il-warnings.sh win-x64",
                    "./scripts/verify-guild-aot.sh --rid win-x64",
                    "./scripts/verify-shipping-publish.sh --rid win-x64")
        for name, command in zip(names[1:4], commands):
            body = step(focused, name)
            self.assertIn("set -euo pipefail", body)
            self.assertIn(command + " 2>&1 | tee ", body)
        positions = [focused.index("- name: " + name) for name in names]
        self.assertEqual(sorted(positions), positions)
        self.assertLess(focused.index("run: ./scripts/qualify-guild-operator.sh"), positions[0])
        self.assertLess(positions[-1], focused.index("- name: Upload focused evidence"))
        self.assertIn('test "$(git rev-parse HEAD)" = "$GITHUB_SHA"', step(focused, names[-1]))
        self.assertIn('test ! -s "$results/worktree-status-after.txt"', step(focused, names[-1]))
        self.assertIn('"$results/completed.txt"', step(focused, names[-1]))

    def test_shell_harness_uses_distinct_evidence_and_manifest_for_each_project(self):
        path = ROOT / "scripts/qualify-guild-operator.sh"
        self.assertTrue(path.is_file())
        script = path.read_text()
        self.assertIn(".tmp/guild-operator-qualification", script)
        self.assertIn("guild_operator_qualification.py filter --project core", script)
        self.assertIn("guild_operator_qualification.py filter --project compendium", script)
        self.assertIn("guild_operator_qualification.py validate --project core", script)
        self.assertIn("guild_operator_qualification.py validate --project compendium", script)
        self.assertEqual(2, script.count("--blame-hang-timeout 15m --blame-hang-dump-type none"))
        self.assertIn("set -euo pipefail", script)
        self.assertIn("git rev-parse HEAD", script)
        self.assertIn("git status --porcelain", script)
        self.assertIn("dotnet --info", script)
