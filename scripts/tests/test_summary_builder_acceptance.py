import hashlib
import unittest
import xml.etree.ElementTree as ET
from scripts.guild_workspace_summary import build_summary
from scripts.tests.test_trx_evidence import report, NS

class SummaryBuilderAcceptanceTests(unittest.TestCase):
    def payload(self, outcome="Passed"):
        root=report([("1", outcome), ("2", "NotExecuted")], [("1", "Required"), ("2", "Windows")])
        root.find(f".//{{{NS}}}TestMethod").set("name", "works")
        return ET.tostring(root)

    def test_exact_bytes_and_skips_are_accounted(self):
        payload=self.payload()
        self.assertEqual({"reportSha256":hashlib.sha256(payload).hexdigest(), "passedTests":1,
                          "requiredClasses":["Required"], "requiredCases":[]}, build_summary(payload, {"Required"}))

    def test_duplicate_selectors_are_sorted_and_unique(self):
        result=build_summary(self.payload(), ["Required", "Required"], ["Required.works", "Required.works"])
        self.assertEqual(["Required"], result["requiredClasses"])
        self.assertEqual(["Required.works"], result["requiredCases"])

    def test_skipped_required_case_cannot_supply_evidence(self):
        with self.assertRaises(ValueError):
            build_summary(self.payload("NotExecuted"), [], ["Required.works"])

    def test_missing_required_class_or_case_refuses(self):
        for classes,cases in ((["Absent"],[]),([],["Required.absent"])):
            with self.subTest(classes=classes,cases=cases), self.assertRaises(ValueError):
                build_summary(self.payload(), classes, cases)

    def test_malformed_xml_is_value_error(self):
        with self.assertRaises(ValueError):
            build_summary(b"<broken", ["Required"])

    def test_existing_validator_still_rejects_failed_results(self):
        with self.assertRaises(ValueError):
            build_summary(self.payload("Failed"), [])
