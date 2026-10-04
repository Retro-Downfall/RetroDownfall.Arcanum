#!/usr/bin/env python3
"""Unit tests for coverage_threshold.py parser.

Coverlet emits Cobertura XML with:
- root line-rate and branch-rate attributes (fraction 0..1).
- per-class <line number="..." condition-coverage="X% (Y/Z)" /> for branch info.
These tests pin that format so parser regressions are caught by `python -m unittest`.
"""

from __future__ import annotations

import contextlib
import io
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).parent))

import coverage_threshold


class CoverageThresholdParserTests(unittest.TestCase):
    def test_default_targets_match_repository_policy(self) -> None:
        self.assertEqual(coverage_threshold.DEFAULT_LINE_TARGET, 80.0)
        self.assertEqual(coverage_threshold.DEFAULT_BRANCH_TARGET, 70.0)

        powershell = (Path(__file__).parent / "coverage_threshold.ps1").read_text(
            encoding="utf-8"
        )
        self.assertIn('COVERAGE_LINE_TARGET" -Default 80.0', powershell)
        self.assertIn('COVERAGE_BRANCH_TARGET" -Default 70.0', powershell)
        self.assertIn('"ApiKeyDigestCache" = 85.0', powershell)

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
        nested_partial: str | None = None,
        conditions: dict[str, str] | None = None,
        nested_same_line: dict[str, str] | None = None,
        line_rate: str = "1.00",
        branch_rate: str = "1.00",
        cli_line_rate: str = "0.90",
        secrets_line_rate: str = "0.90",
        omit_assembly: str | None = None,
    ) -> str:
        classes = []
        for security_type in sorted(coverage_threshold.SECURITY_TYPES):
            if security_type == omitted:
                continue

            condition = (conditions or {}).get(
                security_type,
                "50% (1/2)" if security_type == partial else "100% (2/2)",
            )
            filename = f"RetroDownfall.Arcanum/Security/{security_type}.cs"
            classes.append(
                f"""
        <class name="RetroDownfall.Arcanum.Security.{security_type}" filename="{filename}" branch-rate="1">
          <lines>
            <line number="10" condition-coverage="{condition}" />
          </lines>
        </class>"""
            )

            if security_type in (nested_same_line or {}):
                # The state machine reports the same source line as the shell with its own,
                # possibly worse, condition coverage.
                classes.append(
                    f"""
        <class name="RetroDownfall.Arcanum.Security.{security_type}/&lt;RunAsync&gt;d__7" filename="{filename}" branch-rate="0.5">
          <lines>
            <line number="10" condition-coverage="{(nested_same_line or {})[security_type]}" />
          </lines>
        </class>"""
                )

            if security_type != nested_partial:
                continue

            # Coverlet keeps the async state machine as a nested class on the same file;
            # its branches live on source lines the synchronous shell never reports.
            classes.append(
                f"""
        <class name="RetroDownfall.Arcanum.Security.{security_type}/&lt;InvokeAsync&gt;d__4" filename="{filename}" branch-rate="0.25">
          <lines>
            <line number="10" condition-coverage="100% (2/2)" />
            <line number="24" condition-coverage="25% (1/4)" />
          </lines>
        </class>"""
            )

        # The reported-only assemblies carry their own rates. The aggregate must not see them, so a
        # test can make them arbitrarily bad without moving the line and branch rates above.
        reported = []
        for name, rate in (
            ("RetroDownfall.Arcanum.Cli", cli_line_rate),
            ("RetroDownfall.Arcanum.Secrets", secrets_line_rate),
        ):
            if name == omit_assembly:
                continue

            short = name.rsplit(".", 1)[-1]
            reported.append(
                f"""
    <package name="{name}" line-rate="{rate}" branch-rate="0.50">
      <classes>
        <class name="{name}.{short}Surface" filename="{short}/{short}Surface.cs" line-rate="{rate}" branch-rate="0.50">
          <lines>
            <line number="1" hits="1" branch="False" />
            <line number="2" hits="0" branch="False" />
          </lines>
        </class>
      </classes>
    </package>"""
            )

        return f"""<?xml version="1.0" encoding="utf-8"?>
<coverage line-rate="{line_rate}" branch-rate="{branch_rate}">
  <packages>
    <package name="RetroDownfall.Arcanum.Infrastructure" line-rate="{line_rate}" branch-rate="{branch_rate}">
      <classes>
{''.join(classes)}
      </classes>
    </package>{''.join(reported)}
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

    def test_api_key_digest_cache_uses_explicit_85_percent_target(self) -> None:
        self.assertEqual(
            coverage_threshold.security_branch_target("ApiKeyDigestCache"),
            85.0,
        )
        self.assertEqual(
            coverage_threshold.security_branch_target("ApiKeyEndpointFilter"),
            100.0,
        )

        path = self._write_xml(
            self._coverage_xml(
                conditions={"ApiKeyDigestCache": "85% (17/20)"},
            )
        )

        self.assertEqual(coverage_threshold.main([str(path)]), 0)

    def test_api_key_digest_cache_below_explicit_target_fails(self) -> None:
        path = self._write_xml(
            self._coverage_xml(
                conditions={"ApiKeyDigestCache": "80% (4/5)"},
            )
        )

        self.assertEqual(coverage_threshold.main([str(path)]), 1)

    def test_powershell_api_key_digest_cache_at_85_percent_passes(self) -> None:
        pwsh = shutil.which("pwsh")
        if pwsh is None:
            self.skipTest("pwsh is not installed")

        path = self._write_xml(
            self._coverage_xml(
                conditions={"ApiKeyDigestCache": "85% (17/20)"},
            )
        )
        script = Path(__file__).parent / "coverage_threshold.ps1"

        completed = subprocess.run(
            [pwsh, "-NoProfile", "-File", str(script), str(path)],
            check=False,
            capture_output=True,
            text=True,
        )

        self.assertEqual(completed.returncode, 0, completed.stderr)

    def test_powershell_api_key_digest_cache_below_85_percent_fails(self) -> None:
        pwsh = shutil.which("pwsh")
        if pwsh is None:
            self.skipTest("pwsh is not installed")

        path = self._write_xml(
            self._coverage_xml(
                conditions={"ApiKeyDigestCache": "80% (4/5)"},
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
        self.assertIn("ApiKeyDigestCache", completed.stderr)

    def test_declaring_type_name_folds_async_state_machines(self) -> None:
        self.assertEqual(
            coverage_threshold.declaring_type_name(
                "RetroDownfall.Arcanum.Api.Security.ApiKeyEndpointFilter/<InvokeAsync>d__4"
            ),
            "ApiKeyEndpointFilter",
        )
        self.assertEqual(
            coverage_threshold.declaring_type_name(
                "RetroDownfall.Arcanum.Api.Security.ApiKeyEndpointFilter"
            ),
            "ApiKeyEndpointFilter",
        )

    def test_uncovered_branch_in_nested_state_machine_fails_the_gate(self) -> None:
        # Regression guard: matching on the substring after the last "." yielded
        # "OutboundUrlGuard/<...>d__17" and skipped every async body, so the security gate
        # only ever inspected the fully covered synchronous shell.
        xml = self._coverage_xml(
            nested_partial="OutboundUrlGuard",
        )
        path = self._write_xml(xml)

        self.assertEqual(coverage_threshold.main([str(path)]), 1)

    def test_fully_covered_nested_state_machine_passes_the_gate(self) -> None:
        xml = self._coverage_xml()
        path = self._write_xml(xml)

        self.assertEqual(coverage_threshold.main([str(path)]), 0)

    def test_same_line_reported_by_shell_and_state_machine_uses_the_worse_rate(self) -> None:
        # The synchronous shell reports line 10 fully covered; the state machine nested under the
        # same type reports the same line at 50%. Keeping the best rate per (file, line) let the
        # covered shell mask the uncovered state machine, which is exactly the async body the gate
        # exists to inspect.
        xml = self._coverage_xml(
            nested_same_line={"OutboundUrlGuard": "50% (1/2)"},
        )
        path = self._write_xml(xml)

        self.assertEqual(coverage_threshold.main([str(path)]), 1)

    def test_powershell_same_line_uses_the_worse_rate(self) -> None:
        pwsh = shutil.which("pwsh")
        if pwsh is None:
            self.skipTest("pwsh is not installed")

        path = self._write_xml(
            self._coverage_xml(
                nested_same_line={"OutboundUrlGuard": "50% (1/2)"},
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
        self.assertIn("OutboundUrlGuard", completed.stderr)

    def test_powershell_gate_rejects_uncovered_nested_state_machine(self) -> None:
        pwsh = shutil.which("pwsh")
        if pwsh is None:
            self.skipTest("pwsh is not installed")

        path = self._write_xml(
            self._coverage_xml(
                nested_partial="OutboundUrlGuard",
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
        self.assertIn("OutboundUrlGuard", completed.stderr)

    def test_platform_targets_can_be_overridden_by_valid_percentages(self) -> None:
        xml = self._coverage_xml(
            line_rate="0.80",
            branch_rate="0.70",
        )
        path = self._write_xml(xml)

        with mock.patch.dict(
            os.environ,
            {
                "COVERAGE_LINE_TARGET": "80",
                "COVERAGE_BRANCH_TARGET": "70",
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

    def _run_gate(self, xml: str) -> tuple[int, str, str]:
        path = self._write_xml(xml)
        stdout = io.StringIO()
        stderr = io.StringIO()

        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            code = coverage_threshold.main([str(path)])

        return code, stdout.getvalue(), stderr.getvalue()

    def test_cli_and_secrets_appear_in_the_report_with_their_own_rates(self) -> None:
        code, stdout, _ = self._run_gate(
            self._coverage_xml(cli_line_rate="0.81", secrets_line_rate="0.72")
        )

        self.assertEqual(code, 0)
        self.assertIn("Reported assembly RetroDownfall.Arcanum.Cli: line 81.00%", stdout)
        self.assertIn("Reported assembly RetroDownfall.Arcanum.Secrets: line 72.00%", stdout)

    def test_package_weights_count_a_line_once_across_a_shell_and_its_state_machine(self) -> None:
        package = ET.fromstring(
            """
<package name="RetroDownfall.Arcanum.Cli" line-rate="0.5" branch-rate="0.5">
  <classes>
    <class name="Ns.Runner" filename="Runner.cs">
      <lines>
        <line number="10" hits="0" branch="True" condition-coverage="50% (1/2)" />
        <line number="11" hits="0" branch="False" />
      </lines>
    </class>
    <class name="Ns.Runner/&lt;RunAsync&gt;d__3" filename="Runner.cs">
      <lines>
        <line number="10" hits="4" branch="True" condition-coverage="100% (4/4)" />
      </lines>
    </class>
  </classes>
</package>"""
        )

        stats = coverage_threshold.PackageStats(package)

        self.assertEqual(stats.valid_lines, 2)
        self.assertEqual(stats.covered_lines, 1)
        self.assertEqual(stats.valid_branches, 4)
        self.assertEqual(stats.line_rate, 50.0)

    def test_a_poor_cli_does_not_move_the_aggregate_floors(self) -> None:
        # A Cli at 5% would sink an aggregate that included it; the aggregate is held to the other
        # assemblies only, and the Cli to its own floor (lowered here so only the aggregate is on test).
        with mock.patch.dict(os.environ, {"COVERAGE_CLI_LINE_TARGET": "0"}):
            code, stdout, stderr = self._run_gate(
                self._coverage_xml(line_rate="0.88", branch_rate="0.77", cli_line_rate="0.05")
            )

        self.assertEqual(code, 0, stderr)
        self.assertIn("Overall line coverage:   88.00%", stdout)
        self.assertIn("Overall branch coverage: 77.00%", stdout)

    def test_cli_below_its_own_floor_fails_the_gate(self) -> None:
        with mock.patch.dict(os.environ, {"COVERAGE_CLI_LINE_TARGET": "60"}):
            code, _, stderr = self._run_gate(self._coverage_xml(cli_line_rate="0.50"))

        self.assertEqual(code, 1)
        self.assertIn("assembly RetroDownfall.Arcanum.Cli: line coverage 50.00% < 60%", stderr)

    def test_secrets_below_its_own_floor_fails_the_gate(self) -> None:
        with mock.patch.dict(os.environ, {"COVERAGE_SECRETS_LINE_TARGET": "80"}):
            code, _, stderr = self._run_gate(self._coverage_xml(secrets_line_rate="0.50"))

        self.assertEqual(code, 1)
        self.assertIn("assembly RetroDownfall.Arcanum.Secrets: line coverage 50.00% < 80%", stderr)

    def test_a_report_without_the_cli_assembly_fails_closed(self) -> None:
        code, _, stderr = self._run_gate(
            self._coverage_xml(omit_assembly="RetroDownfall.Arcanum.Cli")
        )

        self.assertEqual(code, 1)
        self.assertIn(
            "required assembly RetroDownfall.Arcanum.Cli is absent from the coverage report",
            stderr,
        )

    def test_invalid_assembly_floor_fails_closed(self) -> None:
        with mock.patch.dict(os.environ, {"COVERAGE_CLI_LINE_TARGET": "101"}):
            code, _, stderr = self._run_gate(self._coverage_xml())

        self.assertEqual(code, 2)
        self.assertIn("COVERAGE_CLI_LINE_TARGET", stderr)

    def test_runsettings_instrument_the_cli_and_secrets_and_exclude_only_interactive_and_platform_code(
        self,
    ) -> None:
        settings = ET.parse(
            Path(__file__).parent.parent
            / "tests"
            / "RetroDownfall.Arcanum.Tests"
            / "coverage.runsettings"
        ).getroot()
        include = settings.findtext(".//Include") or ""
        exclude = settings.findtext(".//Exclude") or ""
        exclude_by_file = settings.findtext(".//ExcludeByFile") or ""

        for assembly in (
            "RetroDownfall.Arcanum.Core",
            "RetroDownfall.Arcanum.Infrastructure",
            "RetroDownfall.Arcanum.Api",
            *coverage_threshold.REPORTED_ASSEMBLY_LINE_FLOORS,
        ):
            self.assertIn(f"[{assembly}]*", include.split(","))

        self.assertNotIn("RetroDownfall.Arcanum.Api.DevHost", include)

        # The Terminal.Gui Command Center is interactive, and every Terminal.Gui reference is in it.
        self.assertIn("**/RetroDownfall.Arcanum.Cli/CommandCenter/**/*.cs", exclude_by_file.split(","))

        # Platform-exclusive credential stores and marker slots cannot be reached on every lane. Each
        # excluded name has to be a real type, or a rename silently re-admits it to the report.
        secrets_security = (
            Path(__file__).parent.parent
            / "src"
            / "RetroDownfall.Arcanum.Secrets"
            / "Security"
        )
        platform_types = {
            "WindowsOsCredentialStore",
            "MacOsCredentialStore",
            "LinuxOsCredentialStore",
            "WindowsHostProcessToolsMarkerSlot",
            "MacOsHostProcessToolsMarkerSlot",
            "LinuxHostProcessToolsMarkerSlot",
        }

        for type_name in sorted(platform_types):
            self.assertIn(f"[RetroDownfall.Arcanum.Secrets]*{type_name}*", exclude.split(","))
            self.assertTrue(
                (secrets_security / f"{type_name}.cs").is_file(),
                f"{type_name}.cs is excluded by type but no longer exists",
            )

    def _run_powershell_gate(self, xml: str, env: dict[str, str] | None = None):
        pwsh = shutil.which("pwsh")
        if pwsh is None:
            self.skipTest("pwsh is not installed")

        path = self._write_xml(xml)
        script = Path(__file__).parent / "coverage_threshold.ps1"

        return subprocess.run(
            [pwsh, "-NoProfile", "-File", str(script), str(path)],
            check=False,
            capture_output=True,
            text=True,
            env={**os.environ, **(env or {})},
        )

    def test_powershell_cli_appears_in_the_report_and_does_not_move_the_aggregate(self) -> None:
        completed = self._run_powershell_gate(
            self._coverage_xml(line_rate="0.88", branch_rate="0.77", cli_line_rate="0.05"),
            {"COVERAGE_CLI_LINE_TARGET": "0"},
        )

        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertIn("Overall line coverage:   88.00%", completed.stdout)
        self.assertIn("Reported assembly RetroDownfall.Arcanum.Cli: line 5.00%", completed.stdout)

    def test_powershell_cli_below_its_own_floor_fails_the_gate(self) -> None:
        completed = self._run_powershell_gate(
            self._coverage_xml(cli_line_rate="0.50"),
            {"COVERAGE_CLI_LINE_TARGET": "60"},
        )

        self.assertEqual(completed.returncode, 1)
        self.assertIn("assembly RetroDownfall.Arcanum.Cli: line coverage 50.00% < 60%", completed.stderr)

    def test_powershell_report_without_the_cli_assembly_fails_closed(self) -> None:
        completed = self._run_powershell_gate(
            self._coverage_xml(omit_assembly="RetroDownfall.Arcanum.Cli")
        )

        self.assertEqual(completed.returncode, 1)
        self.assertIn(
            "required assembly RetroDownfall.Arcanum.Cli is absent from the coverage report",
            completed.stderr,
        )

    def test_reported_assembly_floors_are_in_parity(self) -> None:
        powershell = (Path(__file__).parent / "coverage_threshold.ps1").read_text(
            encoding="utf-8"
        )

        for name, (env_name, default) in coverage_threshold.REPORTED_ASSEMBLY_LINE_FLOORS.items():
            self.assertIn(f'"{name}"', powershell)
            self.assertIn(
                f'Resolve-CoverageTarget -Name "{env_name}" -Default {default:.1f}',
                powershell,
            )

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
        # WorkspacePathPolicy decides whether a path escapes the workspace root, which is the
        # containment check every workspace tool leans on. It lives in Infrastructure and is
        # therefore already inside the coverage denominator; only the 100% list omitted it,
        # and that is the one list where an omission is completely silent.
        self.assertIn("WorkspacePathPolicy", coverage_threshold.SECURITY_TYPES)


if __name__ == "__main__":
    unittest.main()
