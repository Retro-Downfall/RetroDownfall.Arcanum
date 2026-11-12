#!/usr/bin/env python3
"""Admit complete Guild performance TRXs and compare matched, repeated workloads."""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import re
from pathlib import Path
import statistics
import sys
import xml.etree.ElementTree as ET

NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
PREFIX = 'RetroDownfall.Arcanum.Tests.Guild.'
EXPECTED_TESTS = {
    PREFIX + 'GuildQuestStoreTests.Performance_quest_issue_replay_tree_and_cancel_frontier(count: 32)',
    PREFIX + 'GuildQuestStoreTests.Performance_quest_issue_replay_tree_and_cancel_frontier(count: 128)',
    PREFIX + 'GuildPortalStoreTests.Performance_circle_publish_claim_materialize_and_replay(count: 24)',
    PREFIX + 'GuildPortalStoreTests.Performance_circle_publish_claim_materialize_and_replay(count: 96)',
    PREFIX + 'GuildQuestStoreTests.Performance_budget_sibling_allocation_contention',
    PREFIX + 'GuildWorkspaceLeaseBrokerTests.Performance_disjoint_workspace_leases_concurrently',
    'RetroDownfall.Arcanum.Tests.Api.GuildCapacityTests.Performance_native_delegation_roundtrip',
}
EXPECTED_WORK = {
    **{(name, size): 1 for size in (32, 128) for name in
       ('quest-child-issue', 'quest-root-idempotent-replay', 'quest-cancel-frontier')},
    ('quest-tree-page', 33): 1, ('quest-tree-page', 129): 1,
    **{(name, size): 1 for size in (24, 96) for name in
       ('circle-publish', 'circle-claim-materialize-apply', 'circle-event-replay')},
    ('budget-concurrent-sibling-allocation', 8): 8,
    ('workspace-disjoint-lease-acquisition', 8): 8,
    ('delegation-native-roundtrip', 1): 1,
}
ENVIRONMENT = ('os', 'runtime', 'architecture')
STORE_SQL_SCOPE = 'fixture-owned fresh store connections; SQL callbacks include trigger and rolled-back work; exclude connection bootstrap and fixture readbacks'
BUDGET_SQL_SCOPE = 'per-contender connection, includes transaction and trigger callbacks'


def number(value, name, integer=False):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0:
        raise ValueError(f'{name} must be finite and nonnegative')
    if integer and not isinstance(value, int):
        raise ValueError(f'{name} must be an integer')
    return value


def validate(path):
    data = Path(path).read_bytes()
    root = ET.fromstring(data)
    results = root.findall('./t:Results/t:UnitTestResult', NS)
    names = [result.get('testName') for result in results]
    if len(names) != len(EXPECTED_TESTS) or set(names) != EXPECTED_TESTS:
        raise ValueError('The exact seven performance cases must each occur once')
    if any(result.get('outcome') != 'Passed' for result in results):
        raise ValueError('Every performance case must pass; console output is not success evidence')
    summary = root.find('./t:ResultSummary', NS)
    counters = root.find('./t:ResultSummary/t:Counters', NS)
    if summary is None or summary.get('outcome') != 'Completed' or counters is None:
        raise ValueError('A completed TRX summary and counters are required')
    for key in ('total', 'executed', 'passed'):
        if counters.get(key) != str(len(EXPECTED_TESTS)):
            raise ValueError('TRX counters do not match the seven passing results')
    for key in ('failed', 'error', 'timeout', 'aborted', 'notExecuted', 'disconnected'):
        if int(counters.get(key, '0')) != 0:
            raise ValueError('TRX contains unsuccessful or missing results')
    measurements = {}
    for output in root.findall('.//t:StdOut', NS):
        for line in (output.text or '').splitlines():
            if not line.startswith('{'):
                continue
            record = json.loads(line)
            if record.get('kind') != 'guild-performance':
                continue
            key = (record.get('workload'), record.get('operations'))
            if key not in EXPECTED_WORK or key in measurements:
                raise ValueError('Unexpected or duplicate workload measurement')
            if record.get('submittedContenders') != EXPECTED_WORK[key]:
                raise ValueError('Submitted contender count differs from the declared workload')
            number(record.get('elapsedMs'), 'elapsedMs')
            number(record.get('processAllocatedBytes'), 'processAllocatedBytes', integer=True)
            for field in ('durableRowsAdded', 'pages', 'sqlStatements', 'sqlWriteStatements'):
                if record.get(field) is not None:
                    number(record[field], field, integer=True)
            if record['workload'].startswith(('quest-', 'circle-', 'budget-')):
                statements = number(record.get('sqlStatements'), 'sqlStatements', integer=True)
                writes = number(record.get('sqlWriteStatements'), 'sqlWriteStatements', integer=True)
                if statements == 0 or writes > statements:
                    raise ValueError('SQL callback census is empty or has more writes than callbacks')
                expected_scope = BUDGET_SQL_SCOPE if record['workload'].startswith('budget-') else STORE_SQL_SCOPE
                if record.get('sqlScope') != expected_scope:
                    raise ValueError('SQL callback scope does not match the declared fixture boundary')
            for field in (*ENVIRONMENT, 'fixture', 'sqlScope'):
                if not isinstance(record.get(field), str) or not record[field]:
                    raise ValueError(f'Missing measurement boundary: {field}')
            measurements[key] = record
    if set(measurements) != set(EXPECTED_WORK):
        raise ValueError('Report is missing a declared workload measurement')
    environments = {tuple(record[field] for field in ENVIRONMENT) for record in measurements.values()}
    if len(environments) != 1:
        raise ValueError('One report must describe one runtime environment')
    return {'schemaVersion': 1, 'reportSha256': hashlib.sha256(data).hexdigest(), 'passedTests': len(EXPECTED_TESTS),
            'measurements': [measurements[key] for key in sorted(measurements)]}


def read_receipt(path, role, samples):
    raw = Path(path).read_bytes()
    receipt = json.loads(raw)
    if not isinstance(receipt, dict) or type(receipt.get('schemaVersion')) is not int or receipt['schemaVersion'] != 1 or receipt.get('role') != role:
        raise ValueError(f'{role} receipt has an invalid schema or role')
    for field, length in (('baseCommit', 40), ('sourceTreeSha256', 64), ('harnessSha256', 64)):
        value = receipt.get(field)
        if not isinstance(value, str) or re.fullmatch(r'[0-9a-f]{%d}' % length, value) is None:
            raise ValueError(f'{role} receipt has invalid {field}')
    if receipt.get('reportSha256') != [sample['reportSha256'] for sample in samples]:
        raise ValueError(f'{role} receipt does not bind the exact ordered report bytes')
    return {'role': role, 'baseCommit': receipt['baseCommit'],
            'sourceTreeSha256': receipt['sourceTreeSha256'], 'harnessSha256': receipt['harnessSha256'],
            'receiptSha256': hashlib.sha256(raw).hexdigest()}


def compare(baseline, candidate, relative, absolute, baseline_receipt, candidate_receipt):
    if len(baseline) != len(candidate) or not baseline:
        raise ValueError('Matched nonempty baseline and candidate sample counts are required')
    number(relative, 'relative limit')
    number(absolute, 'absolute limit')
    if relative < 1:
        raise ValueError('Relative regression limit must be at least one')
    before, after = [validate(path) for path in baseline], [validate(path) for path in candidate]
    hashes = [sample['reportSha256'] for sample in before + after]
    if len(set(hashes)) != len(hashes):
        raise ValueError('Every baseline and candidate sample must have distinct report bytes')
    old_receipt = read_receipt(baseline_receipt, 'baseline', before)
    new_receipt = read_receipt(candidate_receipt, 'candidate', after)
    if old_receipt['harnessSha256'] != new_receipt['harnessSha256']:
        raise ValueError('Baseline and candidate benchmark harness bytes differ')
    if old_receipt['sourceTreeSha256'] == new_receipt['sourceTreeSha256']:
        raise ValueError('Baseline and candidate source identities are identical')
    comparisons, regressions = [], []
    for index, key in enumerate(sorted(EXPECTED_WORK)):
        old = [sample['measurements'][index] for sample in before]
        new = [sample['measurements'][index] for sample in after]
        boundary = (*ENVIRONMENT, 'fixture', 'sqlScope', 'submittedContenders', 'durableRowsAdded', 'pages')
        if len({tuple(record[field] for field in boundary) for record in old + new}) != 1:
            raise ValueError(f'Workload/environment mismatch for {key}')
        old_time = statistics.median(record['elapsedMs'] for record in old)
        new_time = statistics.median(record['elapsedMs'] for record in new)
        limit = max(old_time * relative, old_time + absolute)
        row = {'workload': key[0], 'operations': key[1], 'baselineMedianMs': old_time,
               'candidateMedianMs': new_time, 'limitMs': limit,
               'baselineMedianAllocatedBytes': statistics.median(record['processAllocatedBytes'] for record in old),
               'candidateMedianAllocatedBytes': statistics.median(record['processAllocatedBytes'] for record in new)}
        for field, suffix in (('sqlStatements', 'SqlStatements'), ('sqlWriteStatements', 'SqlWriteStatements')):
            old_counts = [record[field] for record in old]
            new_counts = [record[field] for record in new]
            if any(value is None for value in old_counts + new_counts):
                if not all(value is None for value in old_counts + new_counts):
                    raise ValueError(f'Inconsistent {field} availability for {key}')
                row['baselineMedian' + suffix] = None
                row['candidateMedian' + suffix] = None
            else:
                row['baselineMedian' + suffix] = statistics.median(old_counts)
                row['candidateMedian' + suffix] = statistics.median(new_counts)
        comparisons.append(row)
        if new_time > limit:
            regressions.append(row)
    return {'schemaVersion': 1, 'samplesPerRevision': len(before), 'relativeLimit': relative,
            'baselineReceipt': old_receipt, 'candidateReceipt': new_receipt,
            'provenanceBoundary': 'Receipts bind declared source and report hashes; independently verify collection and source stability',
            'outputBoundary': 'Workload census comes from aggregate TRX stdout, not per-test output attribution',
            'absoluteLimitMs': absolute, 'baselineReportHashes': [s['reportSha256'] for s in before],
            'candidateReportHashes': [s['reportSha256'] for s in after],
            'comparisons': comparisons, 'regressions': regressions}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    admission = sub.add_parser('validate')
    admission.add_argument('report')
    comparison = sub.add_parser('compare')
    comparison.add_argument('--baseline', nargs='+', required=True)
    comparison.add_argument('--candidate', nargs='+', required=True)
    comparison.add_argument('--baseline-receipt', required=True)
    comparison.add_argument('--candidate-receipt', required=True)
    comparison.add_argument('--relative-limit', type=float, default=1.25)
    comparison.add_argument('--absolute-limit-ms', type=float, default=50.0)
    args = parser.parse_args()
    try:
        result = validate(args.report) if args.command == 'validate' else compare(
            args.baseline, args.candidate, args.relative_limit, args.absolute_limit_ms,
            args.baseline_receipt, args.candidate_receipt)
    except (ValueError, OSError, ET.ParseError, TypeError, KeyError) as error:
        print(f'Guild performance evidence refused: {error}', file=sys.stderr)
        return 2
    print(json.dumps(result, indent=2, sort_keys=True, allow_nan=False))
    return 1 if result.get('regressions') else 0


if __name__ == '__main__':
    raise SystemExit(main())
