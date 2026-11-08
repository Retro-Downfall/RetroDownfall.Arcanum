"""Edge cases for required named cases, additive to the acceptance suite."""
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
import xml.etree.ElementTree as ET
from scripts.tests.trx_fixture import NS, append_summary
from scripts.validate_guild_workspace_evidence import validate


class RequiredCaseExtraTests(unittest.TestCase):
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

    def test_repeated_identical_case_definitions_are_admitted_without_duplicate_rows(self):
        """Identical definitions are fine; the case matches one underlying result row."""
        path = self.report([("1", "Passed")],
                           [("1", "Required", "wanted"), ("1", "Required", "wanted")])
        # A single Pass result row, not two, keeps the passed count honest.
        self.assertEqual(1, validate(path, {"Required"}, required_cases={"Required.wanted"}))

    def test_required_case_fails_when_any_row_of_that_case_is_skipped(self):
        """The same named case with one skipped data row refuses even though one passed."""
        path = self.report([("1", "Passed"), ("1", "NotExecuted"), ("2", "Passed")],
                           [("1", "Required", "wanted"), ("2", "Other", "works")])
        with self.assertRaises(ValueError):
            validate(path, {"Other"}, required_cases={"Required.wanted"})

    def test_case_and_class_contracts_pass_together(self):
        path = self.report([("1", "Passed"), ("2", "Passed"), ("3", "NotExecuted")],
                           [("1", "Required", "wanted"), ("2", "Other", "works"), ("3", "Windows", "only")])
        # A class-only contract and a named contract coexist and both must hold.
        self.assertEqual(2,
                         validate(path, {"Required", "Other"}, required_cases={"Required.wanted"}))

    def test_cli_reports_failing_case_after_a_passing_class(self):
        path = self.report([("1", "Passed"), ("2", "Passed"), ("1", "NotExecuted")],
                           [("1", "Required", "wanted"), ("2", "Other", "works")])
        command = [sys.executable, "scripts/validate_guild_workspace_evidence.py", str(path), "Other",
                   "--required-case", "Required.wanted"]
        result = subprocess.run(command, text=True, capture_output=True, check=False)
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertEqual("", result.stdout)
        self.assertIn("Required.wanted", result.stderr)


if __name__ == "__main__":
    unittest.main()
