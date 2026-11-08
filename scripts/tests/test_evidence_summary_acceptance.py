import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from unittest.mock import patch
from scripts import validate_guild_workspace_evidence as validator
from scripts.tests.test_trx_evidence import report

class EvidenceSummaryAcceptanceTests(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.root = Path(self.folder.name)
        self.path = self.root / "run.trx"
        self.payload = ET.tostring(report([("1", "Passed"), ("2", "NotExecuted")], [("1", "Required"), ("2", "Windows")]))
        self.path.write_bytes(self.payload)

    def run_cli(self, output):
        return subprocess.run([sys.executable, "scripts/validate_guild_workspace_evidence.py", str(self.path),
            "Required", "--summary-json", str(output)], text=True, capture_output=True, check=False)

    def test_summary_uses_exact_report_bytes_and_only_passed_count(self):
        summary = validator.validate_report(self.path, {"Required"})
        self.assertEqual({"reportSha256": hashlib.sha256(self.payload).hexdigest(), "passedTests": 1,
                          "requiredClasses": ["Required"], "requiredCases": []}, summary)
        self.assertEqual(1, validator.validate(self.path, {"Required"}))

    def test_validated_snapshot_is_read_once(self):
        with patch.object(Path, "read_bytes", return_value=self.payload) as read:
            summary = validator.validate_report(self.path, {"Required"})
        read.assert_called_once()
        self.assertEqual(hashlib.sha256(self.payload).hexdigest(), summary["reportSha256"])

    def test_cli_writes_json_only_after_success(self):
        output = self.root / "summary.json"
        result = self.run_cli(output)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(1, json.loads(output.read_text())["passedTests"])
        self.assertEqual("", result.stderr)

    def test_invalid_report_preserves_existing_summary_and_writes_no_stdout(self):
        self.path.write_text("<broken")
        output = self.root / "summary.json"
        output.write_text("existing")
        result = self.run_cli(output)
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertEqual("existing", output.read_text())
        self.assertEqual("", result.stdout)

    def test_summary_write_failure_is_a_clean_validation_failure(self):
        result = self.run_cli(self.root / "missing" / "summary.json")
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertEqual("", result.stdout)
        self.assertNotIn("Traceback", result.stderr)

    def test_summary_must_not_overwrite_input_report(self):
        result = self.run_cli(self.path)
        self.assertEqual(1, result.returncode, result.stderr)
        self.assertEqual(self.payload, self.path.read_bytes())
        self.assertEqual("", result.stdout)

    def test_atomic_publication_failure_preserves_previous_summary(self):
        output = self.root / "summary.json"
        output.write_text("previous")
        before = set(self.root.iterdir())
        with patch("os.replace", side_effect=OSError("simulated publication failure")):
            status = validator.main([str(self.path), "Required", "--summary-json", str(output)])
        self.assertEqual(1, status)
        self.assertEqual("previous", output.read_text())
        self.assertEqual(before, set(self.root.iterdir()))
