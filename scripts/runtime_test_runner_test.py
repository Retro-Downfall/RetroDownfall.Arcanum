"""Executable boundary tests; every dotnet child is a synthetic Python program."""

import importlib.util
import io
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts/runtime_test_runner.py"
RAPTOR = "RetroDownfall.Arcanum.Tests.Weave.Tapestry.Raptor.RaptorScaleProbe."
OPTIONAL = [
    "Complete_global_pipeline_at_one_thousand", "Complete_real_clusterer_at_one_thousand",
    "Complete_approximate_graph_at_ten_thousand_uses_less_work_than_exact",
    "Cancellation_at_ten_thousand_occurs_in_actual_descent_with_the_supplied_token",
    "Partial_neighbor_pilot_at_ten_thousand", "Partial_neighbor_pilot_at_fifty_thousand",
    "Phase_reaching_neighbor_pilot_at_fifty_thousand", "First_descent_neighbor_pilot_at_fifty_thousand",
    "Complete_neighbor_graph_at_fifty_thousand", "Complete_global_pipeline_at_ten_thousand",
    "Complete_real_clusterer_at_ten_thousand",
]

FAKE = r'''#!/usr/bin/env python3
import datetime,json,os,pathlib,subprocess,sys,time,xml.etree.ElementTree as E
cfg=json.loads(pathlib.Path(os.environ['FAKE_RUNTIME_CONFIG']).read_text())
a=sys.argv[1:]; mode=cfg.get('mode',''); root=pathlib.Path(cfg['root']); assembly=root/'out'/'Tests.dll'
kind='build' if a[0]=='build' else 'metadata' if a[0]=='msbuild' else 'discover' if '--list-tests' in a else 'run'
f=a[a.index('--filter')+1] if '--filter' in a else ''
lane='resource' if '&Category=RaptorScaleProbe' in f else 'remainder' if '&Category!=RaptorScaleProbe' in f else 'base'
event={'kind':kind,'lane':lane,'args':a,'pid':os.getpid(),'eof':sys.stdin.read()=='','server':os.environ.get('MSBUILDUSESERVER')}
with (root/'events.jsonl').open('a') as s: s.write(json.dumps(event)+'\n')
if mode=='require-server-off' and os.environ.get('MSBUILDUSESERVER')!='0': sys.exit(25)
if mode=='require-no-server':
    if not all(flag in a for flag in ['-m:1','-nr:false','-p:UseSharedCompilation=false']): sys.exit(23)
    if kind!='metadata' and '--disable-build-servers' not in a: sys.exit(24)
if kind=='build':
    if mode=='build-fail': sys.exit(7)
    sys.exit(0)
if kind=='metadata': print(assembly); sys.exit(0)
resource=cfg['resource']; remainder=cfg['remainder']
if kind=='discover':
    rows=resource if lane=='resource' else remainder if lane=='remainder' else resource+remainder
    if mode=='overlap' and lane=='remainder': rows=rows+resource[:1]
    if mode=='empty' and lane=='resource': rows=[]
    print('The following Tests are available:')
    for row in rows: print('    '+row)
    sys.exit(0)
results=pathlib.Path(a[a.index('--results-directory')+1]); results.mkdir(parents=True,exist_ok=True)
if mode=='timeout' and lane=='resource':
    child = subprocess.Popen([sys.executable,'-c',"import pathlib,signal,sys,time; root=pathlib.Path(sys.argv[1]); signal.signal(signal.SIGTERM,lambda *_: (root.joinpath('descendant-stopped').write_text('stopped'),sys.exit(0))); root.joinpath('descendant-ready').write_text('ready'); time.sleep(120)",str(root)])
    deadline=time.monotonic()+2
    while not (root/'descendant-ready').exists() and time.monotonic()<deadline: time.sleep(.005)
    time.sleep(120)
if mode=='missing' and lane=='resource': sys.exit(0)
rows=list(resource if lane=='resource' else cfg['actualRemainder'])
if mode=='empty-trx': rows=[]
if lane=='resource':
    if mode=='omit': rows.pop(0)
    if mode=='duplicate': rows[0]=rows[1]
    if mode=='unexpected': rows[0]='Tests.Intruder.Fact'
if lane=='remainder' and mode=='missing-method': rows=rows[:1]
if lane=='remainder' and mode=='unexpected-method': rows.append('Tests.Intruder.Fact')
now=datetime.datetime.now(datetime.timezone.utc); start=now-datetime.timedelta(days=2) if mode=='stale' else now
run=E.Element('TestRun'); E.SubElement(run,'Times',start=start.isoformat(),finish=now.isoformat())
settings=E.SubElement(run,'TestSettings'); E.SubElement(settings,'Deployment',runDeploymentRoot='deployment')
defs=E.SubElement(run,'TestDefinitions'); entries=E.SubElement(run,'Results')
passed=skipped=failed=0
for i,name in enumerate(rows):
    optional=name in cfg['optional']; outcome='NotExecuted' if optional else 'Passed'
    if lane=='resource' and mode=='skip-mandatory' and i==0: outcome='NotExecuted'
    if lane=='resource' and mode=='failed-row' and i==0: outcome='Failed'
    if lane=='remainder' and mode in ['remainder-skip','empty-skip-reason'] and i==0: outcome='NotExecuted'
    if lane=='remainder' and mode=='unknown-outcome' and i==0: outcome='Warning'
    if optional and mode=='skip-alias': outcome='Skipped'
    passed+=outcome=='Passed'; skipped+=outcome=='NotExecuted'; failed+=outcome=='Failed'
    method=name.split('(',1)[0]; cls,method=method.rsplit('.',1)
    d=E.SubElement(defs,'UnitTest',id=str(i)); E.SubElement(d,'TestMethod',className=cls,name=method,codeBase=str(assembly if mode!='wrong-assembly' else root/'wrong.dll'))
    row=E.SubElement(entries,'UnitTestResult',testId=str(i),executionId=str(i),testName=name,outcome=outcome)
    if outcome in ('NotExecuted','Skipped'):
        output=E.SubElement(row,'Output'); err=E.SubElement(output,'ErrorInfo'); E.SubElement(err,'Message').text='' if mode=='empty-skip-reason' else 'Platform-specific case unavailable on this OS.' if lane=='remainder' else 'Explicit offline opt-in required.'
summary=E.SubElement(run,'ResultSummary',outcome='Aborted' if mode=='abort' else 'Completed')
E.SubElement(summary,'Counters',total=str(len(rows)+(1 if mode=='bad-counters' else 0)),executed=str(passed+failed),passed=str(passed),failed=str(failed),error='0',timeout='0',aborted='0',inconclusive='0',notRunnable='0',notExecuted='99' if mode=='bad-skip-counter' else '0',disconnected='0',warning='0',completed='0',inProgress='0',pending='0',passedButRunAborted='0')
if '--collect' in a:
    coll=E.SubElement(E.SubElement(summary,'CollectorDataEntries'),'Collector',uri='datacollector://microsoft/CoverletCodeCoverage/1.0')
    attachments=E.SubElement(coll,'UriAttachments')
    for filename in ['coverage.json','coverage.cobertura.xml']:
        href='machine/'+filename
        if mode=='escape-report': href='../../../../'+filename
        attach=E.SubElement(attachments,'UriAttachment'); E.SubElement(attach,'A',href=href)
        path=results/'deployment'/'In'/'machine'/filename; path.parent.mkdir(parents=True,exist_ok=True)
        if mode!='missing-report' or filename!='coverage.json': path.write_text('{}' if filename.endswith('json') else '<coverage/>')
        copy=results/'guid-copy'/filename; copy.parent.mkdir(exist_ok=True); copy.write_text('{}' if filename.endswith('json') else '<coverage/>')
trx=results/'runtime.trx'; E.ElementTree(run).write(trx)
if mode=='malformed': trx.write_text('<broken')
if mode=='mutate-pdb': assembly.with_suffix('.pdb').write_text('changed')
if mode=='mutate-first-trx' and lane=='remainder': (results.parent/'resource'/'runtime.trx').write_text('changed after validation')
if mode=='mutate-untracked-source': pathlib.Path(cfg['sourceToMutate']).write_text('class Input { /* changed by fake child */ }')
if mode=='mutate-build': assembly.write_text('changed')
if mode=='mutate-settings': pathlib.Path(a[a.index('--settings')+1]).write_text('changed')
if mode=='exit-nonzero' and lane=='resource': sys.exit(9)
sys.exit(0)
'''


class RuntimeRunnerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.project = self.root / "project" / "Tests.csproj"
        self.project.parent.mkdir()
        self.project.write_text("<Project/>")
        output = self.root / "out"
        output.mkdir()
        for name in ["Tests.dll", "Tests.pdb", "Dependency.dll", "Tests.deps.json", "Tests.runtimeconfig.json", "xunit.runner.json"]:
            (output / name).write_text(name)
        self.fake = self.root / "fake-dotnet"
        self.fake.write_text(FAKE)
        self.fake.chmod(0o755)
        self.config = self.root / "fake.json"
        self.resource = [RAPTOR + "Mandatory", RAPTOR + 'Theory(reason: "memory")', RAPTOR + 'Theory(reason: "time")'] + [RAPTOR + x for x in OPTIONAL]
        self.remainder = ['Tests.Other.Fact', 'Tests.Other.Deferred', 'Tests.Other.Dynamic(id: "discovered")']
        self.actual = ['Tests.Other.Fact', 'Tests.Other.Deferred(value: 1)', 'Tests.Other.Deferred(value: 1)', 'Tests.Other.Dynamic(id: "runtime")']
        self.settings = self.root / "coverage.runsettings"
        self.settings.write_text('<RunSettings><DataCollectionRunSettings><DataCollectors><DataCollector friendlyName="XPlat code coverage"><Configuration><Format>json,cobertura</Format></Configuration></DataCollector></DataCollectors></DataCollectionRunSettings></RunSettings>')
        self.results = self.root / "results"
        self.environment = {**os.environ, "FAKE_RUNTIME_CONFIG": str(self.config)}
        self.environment.pop("ARCANUM_RAPTOR_SCALE_PROBE", None)
        self.configure()

    def configure(self, mode=""):
        self.config.write_text(json.dumps({"root": str(self.root), "mode": mode, "resource": self.resource, "remainder": self.remainder, "actualRemainder": self.actual, "optional": [RAPTOR + x for x in OPTIONAL]}))

    def command(self, *extra):
        return [sys.executable, str(SCRIPT), '--dotnet', str(self.fake), '--project', str(self.project), '--configuration', 'Release', '--results-directory', str(self.results), *extra]

    def invoke(self, *extra):
        return subprocess.run(self.command(*extra), env=self.environment, text=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=30)

    def events(self):
        p = self.root / "events.jsonl"
        return [json.loads(x) for x in p.read_text().splitlines()] if p.exists() else []

    def receipt(self):
        return json.loads((self.results / 'runtime-summary.json').read_text())

    def test_build_once_then_join_resource_before_remainder_and_preserve_counters(self):
        result = self.invoke()
        self.assertEqual(0, result.returncode, result.stdout)
        events = self.events()
        self.assertEqual(1, sum(x['kind'] == 'build' for x in events))
        self.assertEqual(['resource', 'remainder'], [x['lane'] for x in events if x['kind'] == 'run'])
        self.assertTrue(all(x['eof'] for x in events))
        runs = [x for x in events if x['kind'] == 'run']
        self.assertNotEqual(runs[0]['pid'], runs[1]['pid'])
        for event in events:
            if event['kind'] in ('run', 'discover'):
                self.assertIn('--no-build', event['args'])
                self.assertIn('--no-restore', event['args'])
        for event in runs:
            self.assertIn('--blame-hang-timeout', event['args'])
            self.assertIn('15m', event['args'])
            self.assertIn('--blame-hang-dump-type', event['args'])
            self.assertIn('none', event['args'])
        receipt = self.receipt()
        self.assertEqual('passed', receipt['status'])
        self.assertEqual(11, len(receipt['lanes'][0]['namedSkips']))
        self.assertEqual(2, receipt['lanes'][1]['caseCounter']['Tests.Other.Deferred(value: 1)'])
        self.assertEqual(4, receipt['lanes'][1]['counts']['total'])
        self.assertEqual('runner-built', receipt['buildIdentity']['provenance'])
        self.assertIn(str(self.root/'out'/'Tests.pdb'), receipt['buildIdentity']['files'])

    def test_every_managed_invocation_obeys_owned_no_server_build_policy(self):
        # Fake child refuses execution, not just a later assertion about command text.
        self.configure('require-no-server')
        result = self.invoke()
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertEqual(['build', 'metadata', 'discover', 'discover', 'discover', 'run', 'run'],
                         [event['kind'] for event in self.events()])

    def test_inherited_server_and_multithreading_are_disabled_only_in_owned_children(self):
        spec = importlib.util.spec_from_file_location('runtime_test_runner', SCRIPT)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        self.configure('require-server-off')
        self.environment.update(MSBUILDUSESERVER='1', DOTNET_CLI_USE_MSBUILD_SERVER='1',
                                MSBUILDFORCEMULTITHREADED='1', MSBUILDENABLEMULTITHREADED='1')
        with mock.patch.dict(os.environ, self.environment, clear=True), mock.patch('sys.stderr', new_callable=io.StringIO) as errors:
            parent = dict(os.environ)
            code = module.main(self.command()[2:])
            self.assertEqual(parent, dict(os.environ))
        self.assertEqual(0, code, errors.getvalue())
        self.assertTrue(self.events())
        self.assertTrue(all(event['server'] == '0' for event in self.events()))

    def test_resource_failures_and_untrusted_results_suppress_remainder(self):
        for mode in ['missing', 'omit', 'duplicate', 'unexpected', 'skip-mandatory', 'failed-row', 'abort', 'exit-nonzero', 'bad-counters', 'wrong-assembly', 'stale', 'mutate-build', 'mutate-pdb', 'empty-trx', 'malformed']:
            with self.subTest(mode=mode):
                self.configure(mode)
                self.results = self.root / ('results-' + mode)
                result = self.invoke()
                self.assertNotEqual(0, result.returncode, result.stdout)
                self.assertEqual('failed', self.receipt()['status'])
                self.assertFalse(any(x['kind']=='run' and x['lane']=='remainder' for x in self.events()))
                (self.root/'events.jsonl').unlink()
                (self.root/'out'/'Tests.dll').write_text('Tests.dll')
                (self.root/'out'/'Tests.pdb').write_text('Tests.pdb')

    def test_discovery_union_empty_and_overlap_fail_before_execution(self):
        for mode in ['empty', 'overlap']:
            self.configure(mode); self.results = self.root / ('results-' + mode)
            result = self.invoke()
            self.assertNotEqual(0, result.returncode)
            self.assertFalse(any(x['kind']=='run' for x in self.events()))

    def test_remainder_requires_each_discovered_method_and_valid_terminal_outcomes(self):
        for mode in ['missing-method', 'unexpected-method', 'unknown-outcome', 'empty-skip-reason']:
            self.configure(mode); self.results = self.root / ('results-' + mode)
            result = self.invoke()
            self.assertNotEqual(0, result.returncode)
            self.assertEqual('failed', self.receipt()['status'])

    def test_normal_remainder_platform_skip_is_reported_separately_from_passes(self):
        self.configure('remainder-skip')
        result = self.invoke()
        self.assertEqual(0, result.returncode, result.stdout)
        lane = self.receipt()['lanes'][1]
        self.assertEqual({'total': 4, 'passed': 3, 'skipped': 1, 'failed': 0}, lane['counts'])
        self.assertEqual([{'name': 'Tests.Other.Fact', 'reason': 'Platform-specific case unavailable on this OS.'}], lane['namedSkips'])

    def test_unrecognized_skipped_outcome_alias_is_not_sdk_evidence(self):
        self.configure('skip-alias')
        self.assertNotEqual(0, self.invoke().returncode)

    def test_enabled_optin_refuses_before_any_child_and_does_not_change_environment(self):
        self.environment['ARCANUM_RAPTOR_SCALE_PROBE'] = '1'
        result = self.invoke()
        self.assertNotEqual(0, result.returncode)
        self.assertEqual([], self.events())
        self.assertIn('offline', result.stdout)

    def test_other_optin_values_and_caller_owned_build_are_honest(self):
        self.environment['ARCANUM_RAPTOR_SCALE_PROBE'] = 'true'
        result = self.invoke('--no-build')
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertFalse(any(x['kind']=='build' for x in self.events()))
        self.assertEqual('caller-owned', self.receipt()['buildIdentity']['provenance'])

    def test_initially_present_untracked_source_cannot_change_behind_identical_porcelain(self):
        # Outside every managed project: this is a fake compiler input, never real C# compilation.
        with tempfile.TemporaryDirectory(prefix='runtime-provenance-', dir=ROOT) as directory:
            source = Path(directory) / 'Input.cs'
            source.write_text('class Input { /* original */ }')
            self.configure('mutate-untracked-source')
            config = json.loads(self.config.read_text())
            config['sourceToMutate'] = str(source)
            self.config.write_text(json.dumps(config))
            before = subprocess.check_output(['git', '-C', str(ROOT), 'status', '--porcelain'])
            result = self.invoke()
            after = subprocess.check_output(['git', '-C', str(ROOT), 'status', '--porcelain'])
            self.assertEqual(before, after)
            self.assertIn('changed by fake child', source.read_text())
            self.assertNotEqual(0, result.returncode, result.stdout)
            self.assertFalse(any(x['kind']=='run' and x['lane']=='remainder' for x in self.events()))

    def test_results_under_project_sources_refuse_before_any_dotnet_child(self):
        self.results = self.project.parent / 'new-results'
        result = self.invoke()
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertEqual([], self.events())
        self.assertFalse(self.results.exists())

    def test_stale_results_directory_and_wrong_source_refuse_before_dotnet(self):
        self.results.mkdir(); (self.results/'runtime-summary.json').write_text('{}')
        self.assertNotEqual(0, self.invoke().returncode)
        self.assertEqual([], self.events())
        self.results = self.root/'other-results'
        self.assertNotEqual(0, self.invoke('--source-sha', '0'*40).returncode)
        self.assertEqual([], self.events())

    def test_both_declared_collector_reports_are_bound_without_counting_guid_copies(self):
        result = self.invoke('--coverage-settings', str(self.settings))
        self.assertEqual(0, result.returncode, result.stdout)
        receipt = self.receipt()
        self.assertEqual(str(self.settings), receipt['coverageSettings']['path'])
        for lane in receipt['lanes']:
            self.assertEqual({'json', 'cobertura'}, set(lane['coverage']))
            for artifact in lane['coverage'].values():
                self.assertIn('/deployment/In/machine/', artifact['path'])
                self.assertEqual(64, len(artifact['sha256']))

    def test_missing_escaping_or_changed_coverage_evidence_fails(self):
        for mode in ['missing-report', 'escape-report', 'mutate-settings']:
            self.configure(mode); self.results=self.root/('results-'+mode)
            result=self.invoke('--coverage-settings',str(self.settings))
            self.assertNotEqual(0,result.returncode)
            self.assertFalse(any(x['kind']=='run' and x['lane']=='remainder' for x in self.events()))
            (self.root/'events.jsonl').unlink()
            self.settings.write_text('<RunSettings/>')

    def test_inconsistent_skip_counter_is_not_hidden_by_sdk_zero_convention(self):
        self.configure('bad-skip-counter')
        result = self.invoke()
        self.assertNotEqual(0, result.returncode, result.stdout)

    def test_later_child_cannot_replace_previously_validated_trx(self):
        self.configure('mutate-first-trx')
        result = self.invoke()
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertEqual('failed', self.receipt()['status'])

    def test_failed_build_cannot_start_discovery_or_test_children(self):
        self.configure('build-fail')
        result = self.invoke()
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(['build'], [x['kind'] for x in self.events()])

    def test_timeout_is_joined_and_remainder_never_starts(self):
        spec=importlib.util.spec_from_file_location('runtime_test_runner',SCRIPT)
        module=importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
        self.configure('timeout')
        with mock.patch.dict(os.environ,self.environment,clear=True), mock.patch.object(module,'LANE_TIMEOUT_SECONDS',0.4), mock.patch('sys.stderr',new_callable=io.StringIO):
            code=module.main(self.command()[2:])
        self.assertNotEqual(0,code)
        receipt=self.receipt()
        self.assertTrue(receipt['lanes'][0]['joined'])
        self.assertTrue((self.root/'descendant-ready').exists())
        self.assertTrue((self.root/'descendant-stopped').exists())
        self.assertFalse(any(x['kind']=='run' and x['lane']=='remainder' for x in self.events()))


if __name__ == '__main__':
    unittest.main()
