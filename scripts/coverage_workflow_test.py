"""Execute the coverage entry point against a controlled dotnet boundary."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]


class CoverageWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="arcanum-coverage-contract-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        scripts = self.root / "scripts"
        scripts.mkdir()
        shutil.copy2(ROOT / "scripts/coverage.sh", scripts / "coverage.sh")
        shutil.copy2(
            ROOT / "scripts/hosted_producer_analysis_runner.py",
            scripts / "hosted_producer_analysis_runner.py",
        )
        (scripts / "coverage_threshold.py").write_text(
            "import os, sys\nsys.exit(int(os.environ.get('THRESHOLD_EXIT', '0')))\n"
        )
        self.log = self.root / "calls.jsonl"
        executable = self.root / "dotnet"
        executable.write_text(
            "#!/usr/bin/env python3\n"
            "import json, os, pathlib, sys, xml.etree.ElementTree as ET\n"
            "args = sys.argv[1:]\n"
            "with open(os.environ['CALL_LOG'], 'a') as log:\n"
            "    log.write(json.dumps(args) + '\\n')\n"
            "if args[0] == 'test':\n"
            "    covered = any(a.startswith('--collect:') for a in args)\n"
            "    listing = '--list-tests' in args\n"
            "    if listing:\n"
            "        print('The following Tests are available:')\n"
            "        if not os.environ.get('EMPTY_ANALYSIS'):\n"
            "            selected = args[args.index('--filter') + 1]\n"
            "            if selected == 'Category=HostedProducerProductionAnalysis':\n"
            "                print('    Example.Tests.Production')\n"
            "            else:\n"
            "                print('    Example.Tests.Fixture(value: 1)')\n"
            "                print('    Example.Tests.Fixture(value: 2)')\n"
            "        sys.exit(0)\n"
            "    code = int(os.environ.get('COVERAGE_EXIT' if covered else 'ANALYSIS_EXIT', '0'))\n"
            "    if code: sys.exit(code)\n"
            "    if covered and not os.environ.get('NO_REPORT'):\n"
            "        dest = pathlib.Path(args[args.index('--results-directory') + 1])\n"
            "        dest.mkdir(parents=True, exist_ok=True)\n"
            "        (dest / 'coverage.cobertura.xml').write_text('<coverage/>')\n"
            "    if not covered:\n"
            "        settings = pathlib.Path(args[args.index('--settings') + 1])\n"
            "        selected = ET.parse(settings).getroot().findtext('./RunConfiguration/TestCaseFilter')\n"
            "        outcome = 'NotExecuted' if os.environ.get('SKIP_ANALYSIS') else 'Passed'\n"
            "        label = 'Skipped' if outcome == 'NotExecuted' else 'Passed'\n"
            "        results = []\n"
            "        if 'Example.Tests.Production' in selected:\n"
            "            print(f'  {label} Example.Tests.Production [1 ms]')\n"
            "            results.append('Example.Tests.Production')\n"
            "        if 'Example.Tests.Fixture' in selected:\n"
            "            print(f'  {label} Example.Tests.Fixture(value: 1) [1 ms]')\n"
            "            print(f'  {label} Example.Tests.Fixture(value: 2) [1 ms]')\n"
            "            results.extend(['Example.Tests.Fixture(value: 1)', 'Example.Tests.Fixture(value: 2)'])\n"
            "        dest = pathlib.Path(args[args.index('--results-directory') + 1])\n"
            "        dest.mkdir(parents=True, exist_ok=True)\n"
            "        trx = next(a for a in args if a.startswith('trx;LogFileName='))\n"
            "        body = ''.join(f'<UnitTestResult testName=\"{name}\" outcome=\"{outcome}\" />' for name in results)\n"
            "        (dest / trx.split('=', 1)[1]).write_text(f'<TestRun><Results>{body}</Results></TestRun>')\n"
        )
        executable.chmod(0o755)
        self.environment = dict(
            os.environ,
            PATH=str(self.root) + os.pathsep + os.environ["PATH"],
            CALL_LOG=str(self.log),
            ARCANUM_ANALYSIS_JOBS="2",
        )

    def run_gate(self, *arguments, **environment):
        completed = subprocess.run(
            ["bash", str(self.root / "scripts/coverage.sh"), *arguments],
            env=dict(self.environment, **environment),
            capture_output=True,
            text=True,
            timeout=20,
        )
        calls = [json.loads(line) for line in self.log.read_text().splitlines()] if self.log.exists() else []
        return completed, [call for call in calls if call[0] == "test"]

    def test_delivery_runs_disjoint_coverage_and_required_analysis(self):
        result, calls = self.run_gate("--threshold")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(5, len(calls))
        covered = calls[0]
        discovery = [call for call in calls if "--list-tests" in call]
        analysis = [
            call
            for call in calls[1:]
            if "--list-tests" not in call
        ]
        self.assertIn("Category!=Perf&Category!=HostedProducerAnalysis", covered)
        self.assertIn("--collect:XPlat Code Coverage", covered)
        self.assertEqual(2, len(discovery))
        self.assertIn("Category=HostedProducerProductionAnalysis", discovery[0])
        self.assertIn(
            "Category=HostedProducerAnalysis&Category!=HostedProducerProductionAnalysis",
            discovery[1],
        )
        self.assertEqual(2, len(analysis))
        self.assertTrue(all("--logger" in call for call in analysis))
        self.assertTrue(all("--settings" in call for call in analysis))
        self.assertTrue(all("--filter" not in call for call in analysis))
        self.assertTrue(
            all(
                not any(arg.startswith("--collect") for arg in call)
                for call in analysis
            )
        )

    def test_analysis_failure_fails_delivery(self):
        result, calls = self.run_gate("--threshold", ANALYSIS_EXIT="17")
        self.assertEqual(17, result.returncode)
        self.assertEqual(5, len(calls))

    def test_skipped_analysis_fails_delivery(self):
        result, calls = self.run_gate("--threshold", SKIP_ANALYSIS="1")

        self.assertNotEqual(0, result.returncode)

        self.assertEqual(5, len(calls))

        self.assertIn("only 0 passed", result.stderr)

    def test_coverage_failure_does_not_run_analysis(self):
        result, calls = self.run_gate(COVERAGE_EXIT="13")
        self.assertEqual(13, result.returncode)
        self.assertEqual(1, len(calls))

    def test_empty_analysis_selection_fails_delivery(self):
        result, calls = self.run_gate(EMPTY_ANALYSIS="1")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(2, len(calls))

    def test_threshold_failure_does_not_run_analysis(self):
        result, calls = self.run_gate("--threshold", THRESHOLD_EXIT="11")
        self.assertEqual(11, result.returncode)
        self.assertEqual(1, len(calls))

    def test_feature_gate_explicitly_reports_incomplete_qualification(self):
        result, calls = self.run_gate("--feature", "--threshold")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(1, len(calls))
        self.assertIn("Category!=HostedProducerAnalysis", calls[0][calls[0].index("--filter") + 1])
        self.assertIn("not delivery qualification", result.stdout)

    def test_missing_report_fails_closed(self):
        result, calls = self.run_gate(NO_REPORT="1")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(1, len(calls))
        self.assertIn("no cobertura report", result.stderr)

    def test_unknown_option_never_starts_tests(self):
        result, calls = self.run_gate("--skip-analysis")
        self.assertNotEqual(0, result.returncode)
        self.assertEqual([], calls)


if __name__ == "__main__":
    unittest.main()
