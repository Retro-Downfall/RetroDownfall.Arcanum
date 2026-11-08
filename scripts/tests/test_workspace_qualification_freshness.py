"""A no-result test runner cannot reuse evidence from a previous qualification."""
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest

ROOT=Path(__file__).resolve().parents[2]

class WorkspaceQualificationFreshnessTests(unittest.TestCase):
    def test_stale_trx_and_summary_are_removed_before_starting_a_new_test_run(self):
        with tempfile.TemporaryDirectory() as folder:
            root=Path(folder)
            (root/"scripts").mkdir()
            shutil.copy2(ROOT/"scripts/qualify-guild-workspace.sh",root/"scripts/qualify-guild-workspace.sh")
            results=root/".tmp/guild-workspace-qualification"
            results.mkdir(parents=True)
            report=results/"guild-workspace.trx"
            summary=results/"evidence-summary.json"
            report.write_text("stale passing report")
            summary.write_text("stale passing summary")
            binary=root/"bin"
            binary.mkdir()
            for name,body in (("git", "exit 0"), ("dotnet", 'if [ "$1" = test ]; then touch "$QUALIFICATION_TEST_STARTED"; fi\nexit 0')):
                path=binary/name
                path.write_text("#!/bin/sh\n"+body+"\n")
                path.chmod(0o755)
            started=root/"started"
            env=dict(os.environ, PATH=str(binary)+os.pathsep+os.environ["PATH"], QUALIFICATION_TEST_STARTED=str(started))
            result=subprocess.run(["bash",str(root/"scripts/qualify-guild-workspace.sh")],env=env,capture_output=True,text=True)
            self.assertTrue(started.exists(),result.stderr)
            self.assertFalse(report.exists(),"stale TRX survived the new run")
            self.assertFalse(summary.exists(),"stale summary survived the new run")
            self.assertNotEqual(0,result.returncode)
