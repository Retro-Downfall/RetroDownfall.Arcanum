"""Protect the narrow, explicitly dispatched native Windows qualification lane."""

import re
from pathlib import Path
import unittest


ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github/workflows/qualify-windows-native-pair.yml"
ENTRYPOINT = ROOT / ".github/workflows/build-windows.yml"


class WindowsNativePairWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(WORKFLOW.is_file(), "The manual paired Windows qualification workflow is missing")
        self.source = WORKFLOW.read_text()

    def test_only_explicit_dispatch_starts_one_bounded_windows_matrix(self):
        triggers = self.source.split("on:\n", 1)[1].split("\npermissions:", 1)[0]
        self.assertEqual(["workflow_dispatch", "workflow_call"], re.findall(r"^  ([\w_]+):", triggers, re.M))
        jobs = self.source.split("\njobs:\n", 1)[1]
        self.assertEqual(["qualify"], re.findall(r"^  ([\w-]+):", jobs, re.M))
        self.assertIn("fromJSON('[\"win-x64\",\"win-arm64\"]')", jobs)
        self.assertIn("matrix.rid == 'win-arm64' && 'windows-11-arm' || 'windows-latest'", jobs)
        self.assertIn("fail-fast: false", jobs)
        self.assertNotIn("continue-on-error", self.source)
        self.assertNotIn("fromJSON(format", self.source)
        self.assertIn("QUALIFICATION_RID: ${{ inputs.rid }}", self.source)
        self.assertIn("@('both', 'win-x64', 'win-arm64') -notcontains $env:QUALIFICATION_RID", self.source)

    def test_existing_entrypoint_routes_pairing_without_running_packaging_jobs(self):
        source = ENTRYPOINT.read_text()

        def job(name):
            match = re.search(r"^  " + re.escape(name) + r":\n(.*?)(?=^  \S|\Z)", source, re.M | re.S)
            self.assertIsNotNone(match, "Missing Windows workflow job: " + name)
            return match.group(1)

        pair = job("paired-qualification")
        self.assertIn("if: inputs.baseline-sha != ''", pair)
        self.assertIn("uses: ./.github/workflows/qualify-windows-native-pair.yml", pair)
        self.assertIn("baseline-sha: ${{ inputs.baseline-sha }}", pair)
        self.assertIn("sdk-version: ${{ inputs.sdk-version || '10.0.403' }}", pair)
        self.assertIn("rid: both", pair)
        for name in ("sdk", "native-sqlcipher-asset", "package"):
            self.assertIn("if: inputs.baseline-sha == ''", job(name))

    def test_exact_sdk_is_shared_by_setup_and_qualification(self):
        self.assertIn("sdk-version:", self.source)
        self.assertIn('default: "10.0.403"', self.source)
        self.assertIn("uses: ./.github/actions/setup-dotnet", self.source)
        self.assertIn("sdk-version: ${{ inputs.sdk-version }}", self.source)
        self.assertIn("SDK_VERSION: ${{ inputs.sdk-version }}", self.source)
        self.assertIn('--sdk-version "$env:SDK_VERSION"', self.source)

    def test_candidate_is_the_dispatched_commit_and_helper_survives_baseline_checkout(self):
        self.assertIn("fetch-depth: 0", self.source)
        self.assertIn("CANDIDATE_SHA: ${{ github.sha }}", self.source)
        self.assertIn("BASELINE_SHA: ${{ inputs.baseline-sha }}", self.source)
        self.assertIn("Join-Path $env:RUNNER_TEMP", self.source)
        self.assertIn("Copy-Item -LiteralPath 'scripts/qualify_windows_native_pair.py'", self.source)
        self.assertIn('--baseline-sha "$env:BASELINE_SHA"', self.source)
        self.assertIn('--candidate-sha "$env:CANDIDATE_SHA"', self.source)
        self.assertIn('if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }', self.source)

    def test_dispatch_values_are_data_not_interpolated_shell_source(self):
        for block in re.findall(r"(?m)^        run: \|\n((?:          .*\n|\n)*)", self.source):
            self.assertNotIn("${{ inputs.", block)
            self.assertNotIn("${{ github.", block)

    def test_failure_evidence_is_uploaded_without_write_authority(self):
        self.assertIn("permissions:\n  contents: read", self.source)
        self.assertIn("if: always()", self.source)
        self.assertIn("uses: actions/upload-artifact@", self.source)
        self.assertIn("${{ runner.temp }}/arcanum-native-pair/${{ matrix.rid }}", self.source)
        self.assertIn("if-no-files-found: error", self.source)
        self.assertNotIn("gh workflow run", self.source)
        self.assertNotIn("ci.yml", self.source)
        self.assertNotIn("verify-native-sqlcipher.yml", self.source)


if __name__ == "__main__":
    unittest.main()
