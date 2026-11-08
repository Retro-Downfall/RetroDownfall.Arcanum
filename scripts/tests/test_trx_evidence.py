"""Regression tests for the bounded TRX result index used by focused CI."""
import unittest
import xml.etree.ElementTree as ET

from scripts.trx_evidence import TrxEvidence
from scripts.tests.trx_fixture import append_summary

NS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"


def report(results, definitions):
    root = ET.Element(f"{{{NS}}}TestRun")
    records = ET.SubElement(root, f"{{{NS}}}Results")
    for identity, outcome in results:
        attributes = {"testId": identity}
        if outcome is not None:
            attributes["outcome"] = outcome
        ET.SubElement(records, f"{{{NS}}}UnitTestResult", attributes)
    declared = ET.SubElement(root, f"{{{NS}}}TestDefinitions")
    for identity, name in definitions:
        unit = ET.SubElement(declared, f"{{{NS}}}UnitTest", id=identity)
        ET.SubElement(unit, f"{{{NS}}}TestMethod", className=name)
    append_summary(root, [outcome for _, outcome in results])
    return root


class TrxEvidenceTests(unittest.TestCase):
    def test_multiple_results_and_platform_skip_keep_their_class_and_count(self):
        evidence = TrxEvidence(report([("1", "Passed"), ("1", "Passed"), ("2", "NotExecuted")],
                                      [("1", "Required"), ("2", "Windows")]))
        self.assertEqual(("Passed", "Passed"), evidence.outcomes_for("Required"))
        self.assertEqual(("NotExecuted",), evidence.outcomes_for("Windows"))
        self.assertEqual((), evidence.outcomes_for("Absent"))
        self.assertEqual(2, evidence.passed_count)

    def test_invalid_outcomes_refuse_before_required_class_selection(self):
        for outcome in ("Failed", "Error", "Aborted", "Timeout", "", None):
            with self.subTest(outcome=outcome), self.assertRaises(ValueError):
                TrxEvidence(report([("1", "Passed"), ("2", outcome)], [("1", "Required")]))

    def test_missing_id_or_class_cannot_supply_required_evidence(self):
        for tag, attribute in (("UnitTest", "id"), ("TestMethod", "className"), ("UnitTestResult", "testId")):
            with self.subTest(tag=tag), self.assertRaises(ValueError):
                root = report([("1", "Passed")], [("1", "Required")])
                root.find(f".//{{{NS}}}{tag}").attrib.pop(attribute)
                TrxEvidence(root)

    def test_empty_report_refuses(self):
        with self.assertRaises(ValueError):
            TrxEvidence(report([], []))

    def test_conflicting_definition_for_same_identity_refuses(self):
        with self.assertRaises(ValueError):
            TrxEvidence(report([("1", "Passed")], [("1", "Required"), ("1", "Other")]))

    def test_identical_repeated_definition_does_not_duplicate_results(self):
        evidence = TrxEvidence(report([("1", "Passed")], [("1", "Required"), ("1", "Required")]))
        self.assertEqual(("Passed",), evidence.outcomes_for("Required"))
        self.assertEqual(1, evidence.passed_count)


if __name__ == "__main__":
    unittest.main()
