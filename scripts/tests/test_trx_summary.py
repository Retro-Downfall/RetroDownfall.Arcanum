"""Reject incomplete or aborted test runs even when their individual rows passed."""
import unittest
import xml.etree.ElementTree as ET

from scripts.trx_evidence import validate_summary

NS = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"
COUNTERS = dict(total="2", executed="1", passed="1", failed="0", error="0", timeout="0", aborted="0",
                inconclusive="0", passedButRunAborted="0", notRunnable="0", notExecuted="1", disconnected="0",
                warning="0", completed="0", inProgress="0", pending="0")


def summary(outcome="Completed", **overrides):
    root = ET.Element(f"{{{NS}}}TestRun")
    section = ET.SubElement(root, f"{{{NS}}}ResultSummary", outcome=outcome)
    ET.SubElement(section, f"{{{NS}}}Counters", {**COUNTERS, **overrides})
    return root


class TrxSummaryTests(unittest.TestCase):
    def test_duplicate_resultsummary_children_raise(self):
        root = summary()
        ET.SubElement(root, f"{{{NS}}}ResultSummary", outcome="Completed")
        with self.assertRaises(ValueError):
            validate_summary(root, 2, 1, 1)

    def test_duplicate_counters_children_raise(self):
        root = summary()
        section = root.find(f"{{{NS}}}ResultSummary")
        ET.SubElement(section, f"{{{NS}}}Counters", dict(COUNTERS))
        with self.assertRaises(ValueError):
            validate_summary(root, 2, 1, 1)

    def test_completed_run_matches_pass_and_platform_skip_counts(self):
        self.assertIsNone(validate_summary(summary(), 2, 1, 1))

    def test_vstest_can_report_zero_notexecuted_despite_platform_skip_rows(self):
        self.assertIsNone(validate_summary(summary(notExecuted="0"), 2, 1, 1))

    def test_aborted_or_unknown_run_cannot_be_admitted(self):
        for outcome in ("Aborted", "Failed", "InProgress", "", "Unknown"):
            with self.subTest(outcome=outcome), self.assertRaises(ValueError):
                validate_summary(summary(outcome), 2, 1, 1)

    def test_summary_and_counters_are_required(self):
        for target in ("ResultSummary", "Counters"):
            root = summary()
            element = root.find(f".//{{{NS}}}{target}")
            parent = root if target == "ResultSummary" else root.find(f"{{{NS}}}ResultSummary")
            parent.remove(element)
            with self.subTest(target=target), self.assertRaises(ValueError):
                validate_summary(root, 2, 1, 1)

    def test_counts_must_match_rows(self):
        for name in ("total", "executed", "passed", "notExecuted"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                validate_summary(summary(**{name: "9"}), 2, 1, 1)

    def test_failure_or_incomplete_counter_cannot_hide_behind_completed_outcome(self):
        for name in ("failed", "error", "timeout", "aborted", "inconclusive", "passedButRunAborted",
                     "notRunnable", "disconnected", "warning", "inProgress", "pending"):
            with self.subTest(name=name), self.assertRaises(ValueError):
                validate_summary(summary(**{name: "1"}), 2, 1, 1)

    def test_counter_format_is_ascii_nonnegative_integer_and_all_fields_are_present(self):
        for value in ("-1", "+1", "1.0", " 1", "١", "", "nan"):
            with self.subTest(value=value), self.assertRaises(ValueError):
                validate_summary(summary(passed=value), 2, 1, 1)
        root = summary()
        del root.find(f".//{{{NS}}}Counters").attrib["aborted"]
        with self.assertRaises(ValueError):
            validate_summary(root, 2, 1, 1)


if __name__ == "__main__":
    unittest.main()
