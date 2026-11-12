"""Execute A2A profile selection and the fail-closed evidence boundary."""
import copy
import json
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

from scripts import guild_operator_qualification as qualification
from scripts.tests.test_ci_scope_workflow import ROOT, selected_inventory
from scripts.tests.test_guild_operator_qualification import authored_methods, authored_case_rows
from scripts.tests import test_guild_operator_qualification as operator_tests

MANIFEST = ROOT / "tests/RetroDownfall.Arcanum.Tests/TestData/Contracts/Arcanum.GuildA2AQualification.json"
PREFIX = "RetroDownfall.Arcanum.Tests."


class GuildA2AQualificationTests(unittest.TestCase):
    def manifest(self):
        self.assertTrue(MANIFEST.is_file(), "A2A needs an explicit witness inventory")
        return qualification.load_manifest(MANIFEST)[0]

    def test_dispatch_runs_one_macos_qualification_with_only_support_jobs(self):
        expected = {("scope", None), ("sdk", None), ("native-sqlcipher-assets", None),
                    ("static-analysis", None), ("guild-a2a", None)}
        for platform in ("both", "macos", "windows", ""):
            with self.subTest(platform=platform):
                selected, _ = selected_inventory("workflow_dispatch", "guild-a2a", platform)
                self.assertEqual(expected, set(selected))

    def test_every_a2a_source_method_including_guild_owned_partials_is_required(self):
        declaration = self.manifest()["projects"]["core"]
        sources = set(declaration["caseSources"])
        for path in (ROOT / "tests/RetroDownfall.Arcanum.Tests/A2A").glob("*.cs"):
            self.assertIn(str(path.relative_to(ROOT)), sources)
        for source in sources:
            for case in authored_methods(ROOT / source):
                self.assertIn(case, declaration["requiredCases"], source)
                self.assertGreaterEqual(declaration["requiredCaseRows"].get(case, 0), 1, case)
            if "MemberData" not in (ROOT / source).read_text():
                for case, count in authored_case_rows(ROOT / source).items():
                    self.assertEqual(count, declaration["requiredCaseRows"].get(case), case)
        self.assertTrue(any(case.startswith(PREFIX + "Guild.GuildLocalHostPolicyTests.")
                            for case in declaration["requiredCases"]))

    def test_selects_security_remote_recovery_and_wire_boundary_guards(self):
        declaration = self.manifest()["projects"]["core"]
        for owner in ("Security.A2APeerAuthenticationTests", "Security.OutboundUrlGuardTests",
                      "Security.OutboundUrlGuardEgressConnectTests", "Guild.GuildInboundPeerRegistryTests",
                      "Guild.GuildInboundPeerDenialJournalTests", "Guild.GuildRemoteCardReaderTests",
                      "Guild.GuildRemoteInvocationIntentTests", "Guild.GuildQuestRecoveryTests",
                      "Api.Serialization.ArcanumJsonContextCompletenessTests", "Build.ArcanumConventionTests"):
            self.assertIn(PREFIX + owner, declaration["requiredClasses"])
        self.assertNotIn(PREFIX + "Guild.GuildLocalHostPolicyTests", declaration["requiredClasses"])
        selection = qualification.make_filter(self.manifest(), "core", "macos")
        self.assertIn("FullyQualifiedName=" + PREFIX + "Guild.GuildLocalHostPolicyTests.", selection)

    def test_completed_evidence_requires_every_case_and_distinct_theory_row_without_skips(self):
        manifest = copy.deepcopy(self.manifest())
        case = PREFIX + "A2A.Required.accepts_wire"
        manifest["projects"]["core"] = {"requiredClasses": [PREFIX + "A2A.Required"],
            "requiredCases": [case], "caseSources": [], "requiredCaseRows": {case: 2},
            "requiredCaseDisplays": {case: [case + '(path: "first")', case + '(path: "second")']}}
        fixture = operator_tests.GuildOperatorQualificationTests()
        rows = [(PREFIX + "A2A.Required", "accepts_wire", "Passed", case + '(path: "first")'),
                (PREFIX + "A2A.Required", "accepts_wire", "Passed", case + '(path: "second")')]
        summary = qualification.validate_evidence(fixture.report(rows), manifest, "core", "macos")
        self.assertEqual("guild-a2a", summary["qualification"])
        self.assertEqual(2, summary["passedTests"])
        self.assertTrue(summary["omissions"])
        for evidence in (rows[:1], [rows[0], rows[0]],
                         [rows[0], (*rows[1][:2], "NotExecuted", rows[1][3])]):
            with self.subTest(rows=evidence), self.assertRaises(ValueError):
                qualification.validate_evidence(fixture.report(evidence), manifest, "core", "macos")
        with self.assertRaises(ValueError):
            qualification.validate_evidence(fixture.report(rows, outcome="Aborted"), manifest, "core", "macos")

    def test_duplicate_row_cannot_fill_an_omitted_variant_without_display_inventory(self):
        manifest = copy.deepcopy(self.manifest())
        case = PREFIX + "A2A.Required.accepts_wire"
        manifest["projects"]["core"] = {"requiredClasses": [PREFIX + "A2A.Required"],
            "requiredCases": [case], "caseSources": [], "requiredCaseRows": {case: 2}, "requiredCaseDisplays": {}}
        fixture = operator_tests.GuildOperatorQualificationTests()
        rows = [(PREFIX + "A2A.Required", "accepts_wire", "Passed", case + '(path: "first")')] * 2
        with self.assertRaises(ValueError):
            qualification.validate_evidence(fixture.report(rows), manifest, "core", "macos")

    def test_paired_sdk_versions_are_reachable_from_the_solution(self):
        projects = [(ROOT / element.attrib["Path"]).resolve()
                    for element in ET.parse(ROOT / "RetroDownfall.Arcanum.slnx").iter("Project")]
        visited, versions = set(), {"A2A": set(), "A2A.AspNetCore": set()}
        while projects:
            project = projects.pop()
            if project in visited: continue
            visited.add(project)
            tree = ET.parse(project)
            projects.extend((project.parent / item.attrib["Include"].replace(chr(92), "/")).resolve()
                            for item in tree.iter("ProjectReference"))
            for item in tree.iter("PackageReference"):
                package = item.attrib.get("Include")
                if package in versions:
                    version = item.attrib.get("Version") or item.findtext("Version")
                    self.assertTrue(version, package + " must have an explicit reviewed version")
                    versions[package].add(version)
        self.assertEqual(1, len(versions["A2A"]))
        self.assertEqual(versions["A2A"], versions["A2A.AspNetCore"])

    def test_manifest_refuses_unknown_profile_or_incomplete_case_inventory(self):
        original = self.manifest()
        for mutation in ("unknown-profile", "empty-cases", "missing-row"):
            manifest = copy.deepcopy(original)
            if mutation == "unknown-profile": manifest["profile"] = "guild-everything"
            elif mutation == "empty-cases": manifest["projects"]["core"]["requiredCases"] = []
            else: manifest["projects"]["core"]["requiredCaseRows"].pop(next(iter(manifest["projects"]["core"]["requiredCaseRows"])))
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as temporary:
                path = Path(temporary) / "manifest.json"
                path.write_text(json.dumps(manifest))
                with self.assertRaises(ValueError): qualification.load_manifest(path)


if __name__ == "__main__":
    unittest.main()
