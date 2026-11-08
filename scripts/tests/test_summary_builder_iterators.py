import unittest
from scripts.guild_workspace_summary import build_summary
from scripts.tests import test_summary_builder_acceptance as fixtures

class SummaryBuilderIteratorTests(unittest.TestCase):
    def test_report_preserves_the_selectors_validated_from_one_use_iterators(self):
        payload=fixtures.SummaryBuilderAcceptanceTests().payload()
        summary=build_summary(payload, iter(["Required"]), iter(["Required.works"]))
        self.assertEqual(["Required"], summary["requiredClasses"])
        self.assertEqual(["Required.works"], summary["requiredCases"])
