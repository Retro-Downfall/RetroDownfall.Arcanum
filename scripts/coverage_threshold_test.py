#!/usr/bin/env python3
"""Unit tests for coverage_threshold.py parser.

Coverlet emits Cobertura XML with:
- root line-rate and branch-rate attributes (fraction 0..1).
- per-class <line number="..." condition-coverage="X% (Y/Z)" /> for branch info.
These tests pin that format so parser regressions are caught by `python -m unittest`.
"""

import os
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).parent))

import coverage_threshold


class CoverageThresholdParserTests(unittest.TestCase):
    def _write_xml(self, content: str) -> Path:
        path = Path(tempfile.mktemp(suffix=".cobertura.xml"))
        path.write_text(content, encoding="utf-8")
        self.addCleanup(path.unlink, missing_ok=True)
        return path

    def test_overall_rates_parsed_from_root_attributes(self) -> None:
        xml = """<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="0.88" branch-rate="0.77">
  <packages />
</coverage>
"""
        path = self._write_xml(xml)
        self.assertEqual(coverage_threshold.main([str(path)]), 0)

    def test_security_type_branch_coverage_parsed_from_condition_coverage(self) -> None:
        xml = """<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="1.00" branch-rate="1.00">
  <packages>
    <package>
      <classes>
        <class name="RetroDownfall.Arcanum.Security.ApiKeyEndpointFilter">
          <lines>
            <line number="10" condition-coverage="100% (2/2)" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>
"""
        path = self._write_xml(xml)
        self.assertEqual(coverage_threshold.main([str(path)]), 0)

    def test_security_type_branch_coverage_failure_reported(self) -> None:
        xml = """<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="1.00" branch-rate="1.00">
  <packages>
    <package>
      <classes>
        <class name="RetroDownfall.Arcanum.Security.ApiKeyEndpointFilter">
          <lines>
            <line number="10" condition-coverage="50% (1/2)" />
          </lines>
        </class>
      </classes>
    </package>
  </packages>
</coverage>
"""
        path = self._write_xml(xml)
        self.assertEqual(coverage_threshold.main([str(path)]), 1)

    def test_platform_targets_can_be_overridden_by_valid_percentages(self) -> None:
        xml = """<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="0.83" branch-rate="0.7345">
  <packages />
</coverage>
"""
        path = self._write_xml(xml)

        with mock.patch.dict(
            os.environ,
            {
                "COVERAGE_LINE_TARGET": "83",
                "COVERAGE_BRANCH_TARGET": "73",
            },
        ):
            self.assertEqual(coverage_threshold.main([str(path)]), 0)

    def test_invalid_platform_target_fails_closed(self) -> None:
        xml = """<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="1.00" branch-rate="1.00">
  <packages />
</coverage>
"""
        path = self._write_xml(xml)

        with mock.patch.dict(
            os.environ,
            {"COVERAGE_LINE_TARGET": "not-a-percentage"},
        ):
            self.assertEqual(coverage_threshold.main([str(path)]), 2)


if __name__ == "__main__":
    unittest.main()
