"""Acceptance tests for required named cases in focused CI evidence."""
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
import xml.etree.ElementTree as ET
from scripts.tests.trx_fixture import NS, append_summary
from scripts.validate_guild_workspace_evidence import validate


class RequiredCasesTests(unittest.TestCase):
    def report(self, rows, definitions):
        root = ET.Element(f"{{{NS}}}TestRun")
        declared = ET.SubElement(root, f"{{{NS}}}TestDefinitions")
        for identity, name, method in definitions:
            unit = ET.SubElement(declared, f"{{{NS}}}UnitTest", id=identity)
            attributes = {"className": name}
            if method is not None:
                attributes["name"] = method
            ET.SubElement(unit, f"{{{NS}}}TestMethod", attributes)
        results = ET.SubElement(root, f"{{{NS}}}Results")
        for identity, outcome in rows:
            ET.SubElement(results, f"{{{NS}}}UnitTestResult", testId=identity, outcome=outcome)
        append_summary(root, [outcome for _, outcome in rows])
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        path = Path(folder.name) / "evidence.trx"
        ET.ElementTree(root).write(path)
        return path

    def test_named_case_accepts_all_data_rows_and_unrelated_platform_skip(self):
        path = self.report([("1", "Passed"), ("2", "Passed"), ("3", "NotExecuted")],
                           [("1", "Required", "wanted"), ("2", "Required", "wanted"), ("3", "Windows", "only")])
        self.assertEqual(2, validate(path, {"Required"}, required_cases={"Required.wanted"}))

    def test_absent_case_is_not_satisfied_by_a_different_method_in_the_same_class(self):
        path = self.report([("1", "Passed")], [("1", "Required", "wanted_extra")])
        with self.assertRaises(ValueError):
            validate(path, {"Required"}, required_cases={"Required.wanted"})

    def test_one_skipped_data_row_refuses_required_case_even_if_another_passed(self):
        path = self.report([("1", "Passed"), ("2", "NotExecuted"), ("3", "Passed")],
                           [("1", "Required", "wanted"), ("2", "Required", "wanted"), ("3", "Other", "works")])
        with self.assertRaises(ValueError):
            validate(path, {"Other"}, required_cases={"Required.wanted"})

    def test_missing_method_name_cannot_supply_named_case_evidence(self):
        path = self.report([("1", "Passed")], [("1", "Required", None)])
        with self.assertRaises(ValueError):
            validate(path, {"Required"}, required_cases={"Required.wanted"})

    def test_conflicting_method_names_for_one_id_are_ambiguous(self):
        path = self.report([("1", "Passed")], [("1", "Required", "wanted"), ("1", "Required", "other")])
        with self.assertRaises(ValueError):
            validate(path, {"Required"}, required_cases={"Required.wanted"})

    def test_cli_required_case_is_repeatable_and_unknown_case_returns_validation_failure(self):
        path = self.report([("1", "Passed"), ("2", "Passed")],
                           [("1", "Required", "wanted"), ("2", "Required", "second")])
        command = [sys.executable, "scripts/validate_guild_workspace_evidence.py", str(path), "Required",
                   "--required-case", "Required.wanted", "--required-case", "Required.second"]
        good = subprocess.run(command, text=True, capture_output=True, check=False)
        self.assertEqual(0, good.returncode, good.stderr)
        bad = subprocess.run(command + ["--required-case", "Required.absent"], text=True, capture_output=True, check=False)
        self.assertEqual(1, bad.returncode, bad.stderr)
        self.assertEqual("", bad.stdout)
        self.assertIn("Required.absent", bad.stderr)


if __name__ == "__main__":
    unittest.main()
