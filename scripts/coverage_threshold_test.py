#!/usr/bin/env python3
"""Unit tests for coverage_threshold.py parser.

Coverlet emits Cobertura XML with:
- root line-rate and branch-rate attributes (fraction 0..1).
- per-class <line number="..." condition-coverage="X% (Y/Z)" /> for branch info.
These tests pin that format so parser regressions are caught by `python -m unittest`.
"""

import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))

import coverage_threshold


class CoverageThresholdParserTests(unittest.TestCase):
    def _write_xml(self, content: str) -> Path:
        path = Path(tempfile.mktemp(suffix=".cobertura.xml"))
        path.write_text(content, encoding="utf-8")
        self.addCleanup(path.unlink, missing_ok=True)
        return path

    def _coverage_xml(
        self,
        *,
        omitted: str | None = None,
        partial: str | None = None,
        line_rate: str = "1.00",
        branch_rate: str = "1.00",
    ) -> str:
        classes = []
        for security_type in sorted(coverage_threshold.SECURITY_TYPES):
            if security_type == omitted:
                continue

            condition = (
                "50% (1/2)"
                if security_type == partial
                else "100% (2/2)"
            )
            classes.append(
                f"""
        <class name="RetroDownfall.Arcanum.Security.{security_type}">
          <lines>
            <line number="10" condition-coverage="{condition}" />
          </lines>
        </class>"""
            )

        return f"""<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="{line_rate}" branch-rate="{branch_rate}">
  <packages>
    <package>
      <classes>
{''.join(classes)}
      </classes>
    </package>
  </packages>
</coverage>
"""

    def test_overall_rates_parsed_from_root_attributes(self) -> None:
        xml = self._coverage_xml(
            line_rate="0.88",
            branch_rate="0.77",
        )
        path = self._write_xml(xml)
        self.assertEqual(coverage_threshold.main([str(path)]), 0)

    def test_security_type_branch_coverage_parsed_from_condition_coverage(self) -> None:
        xml = self._coverage_xml()
        path = self._write_xml(xml)
        self.assertEqual(coverage_threshold.main([str(path)]), 0)

    def test_security_type_branch_coverage_failure_reported(self) -> None:
        xml = self._coverage_xml(
            partial="ApiKeyEndpointFilter",
        )
        path = self._write_xml(xml)
        self.assertEqual(coverage_threshold.main([str(path)]), 1)

    def test_missing_required_security_type_is_reported(self) -> None:
        xml = self._coverage_xml(
            omitted="SecureFileReader",
        )
        path = self._write_xml(xml)

        self.assertEqual(
            coverage_threshold.main([str(path)]),
            1,
        )

    def test_powershell_gate_rejects_missing_required_security_type(self) -> None:
        pwsh = shutil.which("pwsh")
        if pwsh is None:
            self.skipTest("pwsh is not installed")

        path = self._write_xml(
            self._coverage_xml(
                omitted="IdentityOwnedFileSystemCleanup",
            )
        )
        script = Path(__file__).parent / "coverage_threshold.ps1"

        completed = subprocess.run(
            [pwsh, "-NoProfile", "-File", str(script), str(path)],
            check=False,
            capture_output=True,
            text=True,
        )

        self.assertEqual(completed.returncode, 1)

    def test_security_type_lists_are_in_parity(self) -> None:
        powershell = (Path(__file__).parent / "coverage_threshold.ps1").read_text(
            encoding="utf-8"
        )
        security_block = powershell.split("$securityTypes", 1)[1].split("),", 1)[0]
        powershell_types = set(re.findall(r'"([A-Za-z][A-Za-z0-9]+)"', security_block))

        self.assertEqual(coverage_threshold.SECURITY_TYPES, powershell_types)
        self.assertIn("TrustedMcpWorkspaceStore", coverage_threshold.SECURITY_TYPES)
        self.assertIn("SecureFileReader", coverage_threshold.SECURITY_TYPES)
        self.assertIn(
            "IdentityOwnedFileSystemCleanup",
            coverage_threshold.SECURITY_TYPES,
        )


if __name__ == "__main__":
    unittest.main()
