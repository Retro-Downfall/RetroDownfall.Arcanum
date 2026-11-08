"""Exercise the CI authority and pin conservative documentation-only job wiring."""

import re
from pathlib import Path
import subprocess
import unittest


ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github/workflows/ci.yml"
WORKERS = (
    "hosted-producer-primary-analysis", "hosted-producer-additional-analysis",
    "hosted-producer-fixture-analysis", "hosted-producer-fixture-build-analysis",
    "hosted-producer-serve-host-analysis",
)


def job(name):
    source = WORKFLOW.read_text()
    match = re.search(r"^  " + re.escape(name) + r":\n(.*?)(?=^  \S|\Z)", source, re.M | re.S)
    if match is None:
        raise AssertionError("Missing CI job: " + name)
    return match.group(1)


def step(body, name):
    match = re.search(r"^      - name: " + re.escape(name) + r"\n(.*?)(?=^      - name:|\Z)", body, re.M | re.S)
    if match is None:
        raise AssertionError("Missing CI step: " + name)
    return match.group(1)


class CiScopeWorkflowTests(unittest.TestCase):
    def test_focused_guild_profile_is_explicit_and_does_not_claim_full_authority(self):
        source = WORKFLOW.read_text()
        self.assertIn("default: full", source)
        focused = job("guild-memory")
        self.assertIn("if: github.event_name == 'workflow_dispatch' && inputs.qualification == 'guild-memory'", focused)
        self.assertIn("run: ./scripts/qualify-guild-memory.sh", focused)
        self.assertNotIn("continue-on-error", focused)
        for name in ("build-test", *WORKERS, "hosted-producer-analysis", "windows-suite",
                     "windows-arm64-suite", "aot-il", "macos-workspace-check"):
            with self.subTest(job=name):
                self.assertRegex(job(name), r"(?m)^    if: .*inputs\.qualification != 'guild-memory'")
        self.assertNotRegex(job("static-analysis"), r"(?m)^    if:")

    def test_scope_uses_event_shas_and_full_history_without_shell_interpolation(self):
        body = job("scope")
        self.assertIn("fetch-depth: 0", body)
        self.assertIn("full: ${{ steps.classify.outputs.full }}", body)
        for binding in (
            "CI_EVENT: ${{ github.event_name }}",
            "CI_BASE: ${{ github.event.pull_request.base.sha }}",
            "CI_PR_HEAD: ${{ github.event.pull_request.head.sha }}",
        ):
            self.assertIn(binding, body)
        command = step(body, "Classify tested change")
        self.assertIn('python3 scripts/ci_scope.py --event "$CI_EVENT"', command)
        self.assertIn('--base "$CI_BASE" --head "$GITHUB_SHA" --pr-head "$CI_PR_HEAD"', command)
        self.assertIn('>> "$GITHUB_OUTPUT"', command)
        self.assertNotIn("continue-on-error", body)

    def test_native_and_producer_jobs_only_skip_on_verified_documentation_scope(self):
        for name in (*WORKERS, "windows-suite", "windows-arm64-suite", "aot-il", "macos-workspace-check"):
            with self.subTest(job=name):
                body = job(name)
                self.assertRegex(body, r"(?m)^    needs: \[[^\n]*\bscope\b")
                self.assertRegex(body, r"(?m)^    if: needs\.scope\.outputs\.full == 'true'")
                self.assertNotIn("continue-on-error", body)
        for name, rid in (("windows-suite", "win-x64"), ("windows-arm64-suite", "win-arm64")):
            self.assertIn("needs.native-sqlcipher-assets.outputs." + rid + " == 'verified'", job(name))

    def test_existing_required_build_job_owns_documentation_success(self):
        body = job("build-test")
        self.assertIn("name: Build, test, coverage", body)
        self.assertIn("needs: [sdk, scope]", body)
        self.assertIn("if: inputs.qualification != 'guild-memory'", body)
        docs = step(body, "Test documentation contracts")
        self.assertIn("if: needs.scope.outputs.full == 'false'", docs)
        self.assertIn("set -euo pipefail", docs)
        self.assertIn("dotnet test tests/RetroDownfall.Arcanum.Tests", docs)
        self.assertIn("FullyQualifiedName~Documentation", docs)
        self.assertIn("Markdown_inventory_table_has_exact_json_row_parity", docs)
        self.assertIn("Every_stated_limit_has_a_pin_and_the_design_still_states_it", docs)
        self.assertIn("if: needs.scope.outputs.full == 'true'", step(body, "Arcanum tests + coverage thresholds"))
        self.assertIn("if: needs.scope.outputs.full == 'true'", step(body, "Test the macOS signing helpers"))
        desktops = step(body, "Test Compendium and The Forge")
        self.assertNotIn("if:", desktops)

    def run_authority(self, full="true", scope="success", build="success", workers=None):
        body = job("hosted-producer-analysis")
        code = step(body, "Require successful selected qualification").split("run: |\n", 1)[1]
        code = "\n".join(line[10:] for line in code.splitlines() if line.startswith("          "))
        results = {"scope.outputs.full": full, "scope.result": scope, "build-test.result": build}
        results.update({name + ".result": value for name, value in zip(WORKERS, workers or ["success"] * 5)})
        code = re.sub(r"\$\{\{ needs\.([\w.-]+) \}\}", lambda match: results[match.group(1)], code)
        self.assertNotIn("${{", code)
        return subprocess.run(["bash", "-c", code], capture_output=True, text=True)

    def test_authority_accepts_full_evidence_or_successful_docs_with_all_workers_skipped(self):
        self.assertEqual(0, self.run_authority().returncode)
        self.assertEqual(0, self.run_authority(full="false", workers=["skipped"] * 5).returncode)

    def test_authority_refuses_missing_classification_failed_docs_and_missing_full_workers(self):
        for full in ("true", "false", ""):
            workers = ["skipped" if full == "false" else "success"] * 5
            for result in ("failure", "cancelled", "skipped", ""):
                with self.subTest(full=full, result=result):
                    self.assertNotEqual(0, self.run_authority(full=full, scope=result, workers=workers).returncode)
                    self.assertNotEqual(0, self.run_authority(full=full, build=result, workers=workers).returncode)
            for index in range(5):
                damaged = workers.copy()
                damaged[index] = "success" if full == "false" else "skipped"
                self.assertNotEqual(0, self.run_authority(full=full, workers=damaged).returncode)
        self.assertNotEqual(0, self.run_authority(full="").returncode)

    def test_source_receipt_consumers_remain_full_only(self):
        body = job("hosted-producer-analysis")
        self.assertIn("    if: always()", body)
        self.assertIn("      - scope", body)
        self.assertIn("      - build-test", body)
        for name in (
            "Download primary source analysis evidence", "Download additional source analysis evidence",
            "Download fixture source analysis evidence", "Download fixture build source analysis evidence",
            "Download serve host source analysis evidence", "Reconcile complete source analysis evidence",
        ):
            self.assertIn("if: needs.scope.outputs.full == 'true'", step(body, name))
        self.assertIn("if: always() && needs.scope.outputs.full == 'true'", step(body, "Upload source analysis authority"))


if __name__ == "__main__":
    unittest.main()
