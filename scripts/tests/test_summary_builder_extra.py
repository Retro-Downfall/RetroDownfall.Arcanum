"""Extra coverage for the focused CI summary builder's own contract.

The shared ``TrxEvidence`` index rejects outcomes outside Passed / NotExecuted
anywhere in a report, so these tests exercise ``build_summary``'s
own responsibilities: the exact byte-derived digest, the pass count, and
multi-class / multi-case qualification while a *non-required* class carries
platform skips.
"""
import hashlib
import unittest
import xml.etree.ElementTree as ET

from scripts.guild_workspace_summary import build_summary
from scripts.tests.test_trx_evidence import report, NS


class SummaryBuilderExtraTests(unittest.TestCase):
    def test_report_sha_and_pass_count_match_the_raw_bytes(self):
        payload = report([("1", "Passed"), ("2", "Passed")],
                         [("1", "Required"), ("2", "Required")])
        for index, unit_test in enumerate(payload.findall(f".//{{{NS}}}TestMethod"), start=1):
            unit_test.set("name", str(index))
        raw = ET.tostring(payload)
        result = build_summary(raw, ["Required"], ["Required.1", "Required.2"])
        self.assertEqual(result["reportSha256"], hashlib.sha256(raw).hexdigest())
        self.assertEqual(result["passedTests"], 2)

    def test_platform_skips_on_non_required_class_are_tolerated(self):
        # ``Required`` and ``Windows`` contribute only passed rows; the extra
        # ``Linux`` class is skipped and never required, exactly as a focused
        # run must tolerate platform drops.
        payload = report(
            [("1", "Passed"), ("2", "Passed"), ("3", "Passed"),
             ("4", "NotExecuted"), ("5", "NotExecuted")],
            [("1", "Required"), ("2", "Windows"), ("3", "Linux"),
             ("4", "Linux"), ("5", "Linux")],
        )
        for unit_test in payload.findall(f".//{{{NS}}}TestMethod"):
            unit_test.set("name", "works")
        raw = ET.tostring(payload)
        result = build_summary(raw, ["Required", "Windows"],
                               ["Required.works", "Windows.works"])
        self.assertEqual(result["requiredClasses"], ["Required", "Windows"])
        self.assertEqual(result["requiredCases"], ["Required.works", "Windows.works"])
        # The two platform skips never execute, so passed counts only the three rows.
        self.assertEqual(result["passedTests"], 3)


if __name__ == "__main__":
    unittest.main()
