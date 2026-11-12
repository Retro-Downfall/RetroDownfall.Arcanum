import json
import hashlib
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / 'scripts/guild_performance_qualification.py'
NS = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
CASES = [
    ('GuildQuestStoreTests', 'Performance_quest_issue_replay_tree_and_cancel_frontier(count: 32)'),
    ('GuildQuestStoreTests', 'Performance_quest_issue_replay_tree_and_cancel_frontier(count: 128)'),
    ('GuildPortalStoreTests', 'Performance_circle_publish_claim_materialize_and_replay(count: 24)'),
    ('GuildPortalStoreTests', 'Performance_circle_publish_claim_materialize_and_replay(count: 96)'),
    ('GuildQuestStoreTests', 'Performance_budget_sibling_allocation_contention'),
    ('GuildWorkspaceLeaseBrokerTests', 'Performance_disjoint_workspace_leases_concurrently'),
    ('Api.GuildCapacityTests', 'Performance_native_delegation_roundtrip'),
]
WORKLOADS = [(name, count) for count in (32, 128) for name in
             ('quest-child-issue', 'quest-root-idempotent-replay', 'quest-cancel-frontier')]
WORKLOADS += [('quest-tree-page', 33), ('quest-tree-page', 129)]
WORKLOADS += [(name, count) for count in (24, 96) for name in
              ('circle-publish', 'circle-claim-materialize-apply', 'circle-event-replay')]
WORKLOADS += [('budget-concurrent-sibling-allocation', 8), ('workspace-disjoint-lease-acquisition', 8), ('delegation-native-roundtrip', 1)]


def report(path, outcome='Passed', omit=False, duplicate=False, elapsed=100.0):
    root = ET.Element(f'{{{NS}}}TestRun')
    results = ET.SubElement(root, f'{{{NS}}}Results')
    for index, (cls, method) in enumerate(CASES):
        ET.SubElement(results, f'{{{NS}}}UnitTestResult', {
            'testName': f'RetroDownfall.Arcanum.Tests.{cls if cls.startswith("Api.") else "Guild." + cls}.{method}',
            'outcome': outcome if index == 0 else 'Passed', 'executionId': str(index)})
    summary = ET.SubElement(root, f'{{{NS}}}ResultSummary', {'outcome': 'Completed'})
    ET.SubElement(summary, f'{{{NS}}}Counters', {'total': '7', 'executed': '7', 'passed': '7', 'failed': '0'})
    output = ET.SubElement(summary, f'{{{NS}}}Output')
    stdout = ET.SubElement(output, f'{{{NS}}}StdOut')
    metrics = [{'kind': 'guild-performance', 'workload': name, 'operations': count,
                'submittedContenders': 8 if count == 8 else 1, 'elapsedMs': elapsed,
                'processAllocatedBytes': 64, 'durableRowsAdded': 0, 'pages': None,
                'sqlStatements': 10 if name.startswith(('quest-', 'circle-')) or name.startswith('budget-') else None,
                'sqlWriteStatements': 3 if name.startswith(('quest-', 'circle-')) or name.startswith('budget-') else None,
                'fixture': 'literal-fixture', 'sqlScope': ('per-contender connection, includes transaction and trigger callbacks' if name.startswith('budget-') else 'fixture-owned fresh store connections; SQL callbacks include trigger and rolled-back work; exclude connection bootstrap and fixture readbacks' if name.startswith(('quest-', 'circle-')) else 'unavailable'),
                'os': 'test OS', 'runtime': '.NET 10', 'architecture': 'Arm64'} for name, count in WORKLOADS]
    if omit:
        metrics.pop()
    if duplicate:
        metrics.append(metrics[0])
    stdout.text = '\n'.join(json.dumps(item) for item in metrics)
    ET.ElementTree(root).write(path, encoding='utf-8', xml_declaration=True)


def receipt(path, role, reports, tree):
    path.write_text(json.dumps({'schemaVersion': 1, 'role': role, 'baseCommit': 'a' * 40,
                                'sourceTreeSha256': tree * 64, 'harnessSha256': 'c' * 64,
                                'reportSha256': [hashlib.sha256(item.read_bytes()).hexdigest() for item in reports]}))


def compare_command(baseline, candidate, old_receipt, new_receipt):
    return [sys.executable, str(SCRIPT), 'compare', '--baseline', *map(str, baseline),
            '--candidate', *map(str, candidate), '--baseline-receipt', str(old_receipt),
            '--candidate-receipt', str(new_receipt)]


class GuildPerformanceQualificationTests(unittest.TestCase):
    def run_validator(self, path):
        return subprocess.run([sys.executable, str(SCRIPT), 'validate', str(path)], text=True, capture_output=True)

    def test_complete_passed_report_yields_exact_census_and_hash(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'run.trx'
            report(path)
            result = self.run_validator(path)
            self.assertEqual(0, result.returncode, result.stderr)
            evidence = json.loads(result.stdout)
            self.assertEqual(7, evidence['passedTests'])
            self.assertEqual(17, len(evidence['measurements']))
            self.assertEqual(64, len(evidence['reportSha256']))

    def test_missing_delegation_case_and_metric_is_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'run.trx'
            report(path)
            tree = ET.parse(path)
            root = tree.getroot()
            results = root.find(f'./{{{NS}}}Results')
            for result in list(results):
                if 'Performance_native_delegation_roundtrip' in result.get('testName', ''):
                    results.remove(result)
            counters = root.find(f'.//{{{NS}}}Counters')
            for key in ('total', 'executed', 'passed'):
                counters.set(key, '6')
            stdout = root.find(f'.//{{{NS}}}StdOut')
            stdout.text = '\n'.join(line for line in stdout.text.splitlines()
                                    if json.loads(line)['workload'] != 'delegation-native-roundtrip')
            tree.write(path, encoding='utf-8', xml_declaration=True)
            self.assertNotEqual(0, self.run_validator(path).returncode)

    def test_measurements_cannot_override_failed_or_skipped_test_results(self):
        for outcome in ('Failed', 'NotExecuted'):
            with self.subTest(outcome=outcome), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / 'run.trx'
                report(path, outcome=outcome)
                self.assertNotEqual(0, self.run_validator(path).returncode)

    def test_missing_duplicate_and_nonfinite_measurements_are_rejected(self):
        for options in ({'omit': True}, {'duplicate': True}, {'elapsed': float('nan')}):
            with self.subTest(options=options), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / 'run.trx'
                report(path, **options)
                self.assertNotEqual(0, self.run_validator(path).returncode)

    def test_quest_circle_and_budget_sql_census_refuses_missing_or_invalid_counts(self):
        for name, field, value in (('quest-child-issue', 'sqlStatements', None),
                                   ('circle-publish', 'sqlWriteStatements', None),
                                   ('budget-concurrent-sibling-allocation', 'sqlStatements', True),
                                   ('circle-event-replay', 'sqlWriteStatements', 99),
                                   ('quest-tree-page', 'sqlScope', 'unavailable')):
            with self.subTest(name=name, field=field, value=value), tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / 'run.trx'
                report(path)
                tree = ET.parse(path)
                stdout = tree.getroot().find(f'.//{{{NS}}}StdOut')
                lines = [json.loads(line) for line in stdout.text.splitlines()]
                next(record for record in lines if record['workload'] == name)[field] = value
                stdout.text = '\n'.join(json.dumps(record) for record in lines)
                tree.write(path, encoding='utf-8', xml_declaration=True)
                self.assertNotEqual(0, self.run_validator(path).returncode)

    def test_comparison_reports_sql_callback_and_write_count_medians(self):
        with tempfile.TemporaryDirectory() as directory:
            base, candidate = Path(directory) / 'base.trx', Path(directory) / 'candidate.trx'
            old, new = Path(directory) / 'old.json', Path(directory) / 'new.json'
            report(base)
            report(candidate, elapsed=110.0)
            receipt(old, 'baseline', [base], 'a')
            receipt(new, 'candidate', [candidate], 'b')
            result = subprocess.run(compare_command([base], [candidate], old, new), text=True, capture_output=True)
            self.assertEqual(0, result.returncode, result.stderr)
            row = next(item for item in json.loads(result.stdout)['comparisons'] if item['workload'] == 'quest-child-issue')
            self.assertEqual(10, row['baselineMedianSqlStatements'])
            self.assertEqual(3, row['candidateMedianSqlWriteStatements'])
            self.assertTrue(any(item['workload'] == 'delegation-native-roundtrip' for item in json.loads(result.stdout)['comparisons']))

    def test_comparison_refuses_reused_report_bytes_in_or_across_revisions(self):
        with tempfile.TemporaryDirectory() as directory:
            base, copy, candidate = (Path(directory) / name for name in ('base.trx', 'copy.trx', 'candidate.trx'))
            old, new = Path(directory) / 'old.json', Path(directory) / 'new.json'
            report(base)
            copy.write_bytes(base.read_bytes())
            report(candidate, elapsed=110.0)
            for baseline, candidates in (([base, copy], [candidate, candidate]), ([base], [copy])):
                receipt(old, 'baseline', baseline, 'a')
                receipt(new, 'candidate', candidates, 'b')
                result = subprocess.run(compare_command(baseline, candidates, old, new), text=True, capture_output=True)
                self.assertNotEqual(0, result.returncode, result.stdout)
                self.assertIn('distinct report bytes', result.stderr)

    def test_comparison_requires_source_receipts_bound_to_exact_report_bytes(self):
        with tempfile.TemporaryDirectory() as directory:
            base, candidate = Path(directory) / 'base.trx', Path(directory) / 'candidate.trx'
            report(base)
            report(candidate, elapsed=110.0)
            result = subprocess.run([sys.executable, str(SCRIPT), 'compare', '--baseline', str(base),
                                     '--candidate', str(candidate)], text=True, capture_output=True)
            self.assertNotEqual(0, result.returncode, result.stdout)

    def test_comparison_requires_matched_environment_and_flags_regression(self):
        with tempfile.TemporaryDirectory() as directory:
            base, candidate = Path(directory) / 'base.trx', Path(directory) / 'candidate.trx'
            old, new = Path(directory) / 'old.json', Path(directory) / 'new.json'
            report(base)
            report(candidate, elapsed=160.0)
            receipt(old, 'baseline', [base], 'a')
            receipt(new, 'candidate', [candidate], 'b')
            command = compare_command([base], [candidate], old, new)
            result = subprocess.run(command, text=True, capture_output=True)
            self.assertEqual(1, result.returncode, result.stderr)
            self.assertEqual(17, len(json.loads(result.stdout)['regressions']))
            report(candidate, elapsed=110.0)
            receipt(new, 'candidate', [candidate], 'b')
            result = subprocess.run(command, text=True, capture_output=True)
            self.assertEqual(0, result.returncode, result.stderr)
            candidate.write_text(candidate.read_text().replace('test OS', 'different OS'))
            receipt(new, 'candidate', [candidate], 'b')
            result = subprocess.run(command, text=True, capture_output=True)
            self.assertNotEqual(0, result.returncode)

    def test_receipts_reject_changed_reports_wrong_roles_harnesses_and_identical_source(self):
        with tempfile.TemporaryDirectory() as directory:
            base, candidate = Path(directory) / 'base.trx', Path(directory) / 'candidate.trx'
            old, new = Path(directory) / 'old.json', Path(directory) / 'new.json'
            report(base)
            report(candidate, elapsed=110.0)
            receipt(old, 'baseline', [base], 'a')
            receipt(new, 'candidate', [candidate], 'b')
            command = compare_command([base], [candidate], old, new)
            self.assertEqual(0, subprocess.run(command, capture_output=True).returncode)
            good = json.loads(new.read_text())
            for field, value in (('schemaVersion', True), ('role', 'baseline'), ('reportSha256', ['0' * 64]),
                                 ('harnessSha256', 'd' * 64), ('sourceTreeSha256', 'a' * 64),
                                 ('baseCommit', 'not-a-sha')):
                changed = dict(good, **{field: value})
                new.write_text(json.dumps(changed))
                self.assertNotEqual(0, subprocess.run(command, capture_output=True).returncode, field)
            new.write_text(json.dumps(good))
            candidate.write_text(candidate.read_text().replace('110.0', '111.0'))
            self.assertNotEqual(0, subprocess.run(command, capture_output=True).returncode)


if __name__ == '__main__':
    unittest.main()
