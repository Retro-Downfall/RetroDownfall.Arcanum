import unittest
from scripts.trx_evidence import TrxEvidence
from scripts.tests.test_trx_evidence import report, NS

class TrxIdentityRequiredTests(unittest.TestCase):
    def test_result_without_definition_is_rejected_even_beside_valid_result(self):
        for outcome in ("Passed", "NotExecuted"):
            with self.subTest(outcome=outcome), self.assertRaises(ValueError):
                TrxEvidence(report([("1", "Passed"), ("orphan", outcome)], [("1", "Required")]))

    def test_result_with_definition_but_no_method_is_rejected(self):
        root = report([("1", "Passed")], [("1", "Required")])
        unit = root.find(f".//{{{NS}}}UnitTest")
        unit.remove(unit.find(f"{{{NS}}}TestMethod"))
        with self.assertRaises(ValueError):
            TrxEvidence(root)
