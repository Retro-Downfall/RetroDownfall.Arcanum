"""Pin explicit operator selection and fail-closed required TRX evidence."""
import copy
import importlib.util
import json
from pathlib import Path
import re
import tempfile
import unittest
import xml.etree.ElementTree as ET

from scripts.tests.test_ci_scope_workflow import ROOT, job, matrix_hosts, selected_inventory
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
        self.assertIn("timeout-minutes: 90", focused)
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
