"""Behavioral coverage for latest stable supported SDK resolution."""
import contextlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import resolve_dotnet_sdk as resolver

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/resolve_dotnet_sdk.py"


def channel(version, sdk, phase="active", feed=None):
    return {"channel-version": version, "latest-sdk": sdk,
            "support-phase": phase, "product": ".NET", "release-type": "lts",
            "latest-release": version + ".0", "latest-release-date": "2026-10-13",
            "latest-runtime": version + ".0", "security": True,
            "releases.json": feed or f"https://builds.dotnet.microsoft.com/dotnet/release-metadata/{version}/releases.json"}


class SdkSelectionTests(unittest.TestCase):
    def run_resolver(self, rows=None, raw=None, floor="10.0.401"):
        with tempfile.TemporaryDirectory() as directory:
            metadata = Path(directory) / "index.json"
            metadata.write_text(raw if raw is not None else json.dumps({"releases-index": rows}))
            policy = Path(directory) / "global.json"
            policy.write_text(json.dumps({"sdk": {"version": floor, "rollForward": "latestMajor", "allowPrerelease": False}}))
            return subprocess.run([sys.executable, str(SCRIPT), "--metadata-file", str(metadata), "--global-json", str(policy)], text=True, capture_output=True)

    def test_selects_highest_stable_supported_sdk_across_major_versions(self):
        result = self.run_resolver([channel("10.0", "10.0.402"), channel("12.0", "12.0.101"), channel("11.0", "11.0.405", "maintenance")])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), "12.0.101")

    def test_excludes_preview_and_end_of_life_channels(self):
        result = self.run_resolver([channel("10.0", "10.0.402"), channel("11.0", "11.0.100-rc.2", "go-live"), channel("12.0", "12.0.100", "eol")])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), "10.0.402")

    def test_minimum_floor_is_inclusive(self):
        result = self.run_resolver([channel("10.0", "10.0.401")])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), "10.0.401")

    def test_refuses_unavailable_supported_sdk_without_fallback(self):
        for rows in ([channel("10.0", "10.0.400")], [channel("11.0", "11.0.100-preview.1", "preview")], []):
            with self.subTest(rows=rows):
                result = self.run_resolver(rows)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("No stable supported SDK", result.stderr)
                self.assertEqual(result.stdout, "")

    def test_refuses_unknown_feed_and_malformed_candidate(self):
        for row in (channel("11.0", "11.0.100", feed="https://example.com/releases.json"), channel("11.0", "garbage"), channel("11.0", "12.0.100")):
            with self.subTest(row=row):
                result = self.run_resolver([channel("10.0", "10.0.402"), row])
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("Invalid release metadata", result.stderr)
                self.assertEqual(result.stdout, "")

    def test_refuses_malformed_oversized_and_excessive_metadata(self):
        for raw in ("{", "{}", "[]", " " * (1024 * 1024 + 1), json.dumps({"releases-index": [channel("10.0", "10.0.402")] * 129})):
            with self.subTest(size=len(raw)):
                result = self.run_resolver(raw=raw)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("metadata", result.stderr)
                self.assertEqual(result.stdout, "")

    def test_explicit_version_rejects_preview_and_old_sdk(self):
        for version in ("10.0.400", "11.0.100-preview.1", "latest", "10.0.402\nanything"):
            result = subprocess.run([sys.executable, str(SCRIPT), "--validate-version", version], cwd=ROOT, text=True, capture_output=True)
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("SDK", result.stderr)


class SdkTransportTests(unittest.TestCase):
    def test_download_refuses_oversized_response_before_parsing(self):
        with self.assertRaisesRegex(ValueError, "exceeds 1 MiB"):
            resolver.read_metadata(io.BytesIO(b" " * (1024 * 1024 + 1)))

    def test_network_timeout_is_a_clear_failure_with_no_selected_version(self):
        errors, output = io.StringIO(), io.StringIO()
        with mock.patch.object(sys, "argv", [str(SCRIPT)]), mock.patch.object(resolver, "fetch_metadata", side_effect=TimeoutError("read timed out")), contextlib.redirect_stderr(errors), contextlib.redirect_stdout(output):
            self.assertEqual(resolver.main(), 1)
        self.assertIn("SDK resolution failed", errors.getvalue())
        self.assertIn("read timed out", errors.getvalue())
        self.assertEqual(output.getvalue(), "")

    def test_network_uses_official_fixed_endpoint_and_finite_timeout(self):
        # Replace only the external transport. Resolution and response parsing remain real.
        response = mock.MagicMock()
        response.__enter__.return_value = io.BytesIO(json.dumps({"releases-index": [channel("10.0", "10.0.402")]}).encode())
        opener = mock.Mock()
        opener.open.return_value = response
        with mock.patch.object(resolver.urllib.request, "build_opener", return_value=opener):
            result = resolver.resolve(resolver.fetch_metadata(), (10, 0, 401))
        self.assertEqual(result, "10.0.402")
        opener.open.assert_called_once_with("https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json", timeout=20)

    def test_redirect_cannot_change_the_official_feed(self):
        with self.assertRaisesRegex(ValueError, "official index"):
            resolver.OfficialRedirectsOnly().redirect_request(None, None, 302, "", {}, "https://example.com/index.json")


class SdkInstallerTests(unittest.TestCase):
    def test_installer_records_actual_selection_and_refuses_runner_substitution(self):
        action = (ROOT / ".github/actions/setup-dotnet/action.yml").read_text()
        # Execute the real composite action's final shell block against a controlled dotnet boundary.
        script = action.rsplit("      run: |\n", 1)[1]
        script = "\n".join(line[8:] for line in script.splitlines())
        for actual, expected_status in (("10.0.402", 0), ("11.0.100", 1), ("10.0.401", 1)):
            with self.subTest(actual=actual), tempfile.TemporaryDirectory() as directory:
                executable = Path(directory) / "dotnet"
                executable.write_text("#!/bin/sh\necho " + actual + "\n")
                executable.chmod(0o755)
                summary = Path(directory) / "summary"
                environment = dict(os.environ, PATH=directory + os.pathsep + os.environ["PATH"], SDK_VERSION="10.0.402", GITHUB_STEP_SUMMARY=str(summary))
                result = subprocess.run(["bash", "-c", script], env=environment, text=True, capture_output=True)
                self.assertEqual(result.returncode, expected_status, result.stderr)
                if expected_status == 0:
                    self.assertIn("Selected stable .NET SDK: 10.0.402", summary.read_text())
                else:
                    self.assertIn("SDK selection mismatch", result.stderr)
                    self.assertFalse(summary.exists())


if __name__ == "__main__":
    unittest.main()
