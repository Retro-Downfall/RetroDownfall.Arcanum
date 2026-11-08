"""Exercise focused CI evidence admission with real TRX documents."""
import contextlib
import io
from pathlib import Path
import tempfile
import unittest
import xml.etree.ElementTree as ET

from scripts.validate_guild_workspace_evidence import main, validate
from scripts.tests.trx_fixture import append_summary

NS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"


class EvidenceTests(unittest.TestCase):
    def report(self, rows):
        root = ET.Element("TestRun", xmlns=NS)
        definitions = ET.SubElement(root, "TestDefinitions")
        results = ET.SubElement(root, "Results")
        for index, (name, outcome) in enumerate(rows):
            identity = str(index)
            unit = ET.SubElement(definitions, "UnitTest", id=identity)
            ET.SubElement(unit, "TestMethod", className=name)
            ET.SubElement(results, "UnitTestResult", testId=identity, outcome=outcome)
        append_summary(root, [outcome for _, outcome in rows])
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        path = Path(folder.name) / "evidence.trx"
        ET.ElementTree(root).write(path)
        return path

    def test_all_required_results_pass(self):
        self.assertEqual(2, validate(self.report([("Required", "Passed"), ("Required", "Passed")]), {"Required"}))

    def test_missing_required_class_refuses(self):
        with self.assertRaises(ValueError):
            validate(self.report([("Other", "Passed")]), {"Required"})

    def test_skipped_required_result_refuses_even_when_another_passes(self):
        with self.assertRaises(ValueError):
            validate(self.report([("Required", "Passed"), ("Required", "NotExecuted")]), {"Required"})

    def test_unrelated_platform_skip_does_not_count_as_pass(self):
        self.assertEqual(1, validate(self.report([("Required", "Passed"), ("Other", "NotExecuted")]), {"Required"}))

    def test_failure_or_unknown_outcome_anywhere_refuses(self):
        for outcome in ("Failed", "Error", "Timeout", "Aborted", ""):
            with self.subTest(outcome=outcome), self.assertRaises(ValueError):
                validate(self.report([("Required", "Passed"), ("Other", outcome)]), {"Required"})

    def test_duplicate_id_with_conflicting_class_refuses(self):
        path = self.report([("Required", "Passed"), ("Other", "Passed")])
        tree = ET.parse(path)
        tests = tree.getroot().findall(f".//{{{NS}}}UnitTest")
        same_id = tests[0].get("id")
        tests[1].set("id", same_id)
        tree.write(path)
        with self.assertRaises(ValueError):
            validate(path, {"Required"})

    def test_aborted_run_refuses_even_when_every_result_row_passed(self):
        path = self.report([("Required", "Passed")])
        tree = ET.parse(path)
        tree.getroot().find(f"{{{NS}}}ResultSummary").set("outcome", "Aborted")
        tree.write(path)
        with self.assertRaises(ValueError):
            validate(path, {"Required"})

    def test_empty_report_refuses(self):
        with self.assertRaises(ValueError):
            validate(self.report([]), {"Required"})

    def test_malformed_xml_and_missing_file_refuse(self):
        path = self.report([])
        path.write_text("not XML")
        with self.assertRaises(ValueError):
            validate(path, {"Required"})
        path.unlink()
        with self.assertRaises(ValueError):
            validate(path, {"Required"})

    def test_cli_reports_failure_on_stderr_and_nonzero_status(self):
        path = self.report([("Required", "Failed")])
        output, error = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(output), contextlib.redirect_stderr(error):
            self.assertEqual(1, main([str(path), "Required"]))
        self.assertEqual("", output.getvalue())
        self.assertTrue(error.getvalue())


if __name__ == "__main__":
    unittest.main()
