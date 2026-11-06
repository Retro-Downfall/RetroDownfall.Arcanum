"""Stub-only checks for the Covenant benchmark's build/measurement resource boundary."""

import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/benchmark-covenant.sh"

DOTNET_STUB = r'''#!/usr/bin/env python3
import json
import os
from pathlib import Path
import sys

args = sys.argv[1:]
calls = Path(os.environ["COVENANT_LAUNCHER_CALLS"])
servers = Path(os.environ["COVENANT_LAUNCHER_SERVERS"])
previous = [json.loads(line) for line in calls.read_text().splitlines()] if calls.exists() else []
event = {"event": "dotnet", "args": args, "servers": sorted(path.name for path in servers.iterdir())}
with calls.open("a", encoding="utf-8") as log:
    log.write(json.dumps(event) + "\n")

if args == ["--info"]:
    print(" RID: osx-arm64")
    sys.exit(0)

if args == ["build-server", "shutdown"]:
    count = 1 + sum(call.get("args") == args for call in previous)
    if str(count) == os.environ.get("COVENANT_LAUNCHER_FAIL_SHUTDOWN", ""):
        print("controlled shutdown failure", file=sys.stderr)
        sys.exit(17)
    for server in servers.iterdir():
        server.unlink()
    print("controlled build servers shut down")
    sys.exit(0)

if not args or args[0] != "publish":
    sys.exit(90)

if "--disable-build-servers" not in args:
    (servers / "publish-created-server").touch()

output = Path(args[args.index("-o") + 1])
output.mkdir(parents=True, exist_ok=True)
apphost = output / "RetroDownfall.Arcanum.Covenant.Benchmarks"
apphost.write_text(r"""#!/usr/bin/env python3
import json
import os
from pathlib import Path
import sys

servers = sorted(path.name for path in Path(os.environ["COVENANT_LAUNCHER_SERVERS"]).iterdir())
with open(os.environ["COVENANT_LAUNCHER_CALLS"], "a", encoding="utf-8") as log:
    log.write(json.dumps({"event": "apphost", "args": sys.argv[1:], "servers": servers}) + "\n")
if servers:
    print("measurement started while build servers remained resident", file=sys.stderr)
    sys.exit(91)
if "--out" in sys.argv:
    Path(sys.argv[sys.argv.index("--out") + 1]).write_text('{"measured":true}')
sys.exit(int(os.environ.get("COVENANT_LAUNCHER_APPHOST_EXIT", "0")))
""")
apphost.chmod(0o755)
'''


class CovenantBenchmarkLauncherTests(unittest.TestCase):
    def setUp(self):
        workspace = tempfile.TemporaryDirectory(prefix="covenant-launcher-tests-")
        self.addCleanup(workspace.cleanup)
        self.work = Path(workspace.name)
        binary = self.work / "bin"
        binary.mkdir()
        dotnet = binary / "dotnet"
        dotnet.write_text(DOTNET_STUB)
        dotnet.chmod(0o755)
        self.calls = self.work / "calls.jsonl"
        self.servers = self.work / "servers"
        self.servers.mkdir()
        (self.servers / "inherited-compiler-server").touch()
        (self.servers / "inherited-msbuild-node").touch()
        self.record = self.work / "run.json"
        self.env = dict(
            os.environ,
            PATH=str(binary) + os.pathsep + os.environ["PATH"],
            TMPDIR=str(self.work),
            ARCANUM_BENCHMARK_RID="osx-arm64",
            COVENANT_LAUNCHER_CALLS=str(self.calls),
            COVENANT_LAUNCHER_SERVERS=str(self.servers),
        )

    def launch(self, *args, fail_shutdown="", apphost_exit=0):
        environment = dict(
            self.env,
            COVENANT_LAUNCHER_FAIL_SHUTDOWN=str(fail_shutdown),
            COVENANT_LAUNCHER_APPHOST_EXIT=str(apphost_exit),
        )
        return subprocess.run(
            ["bash", str(SCRIPT), *args], cwd=ROOT, env=environment,
            text=True, capture_output=True, timeout=30,
        )

    def events(self):
        return [json.loads(line) for line in self.calls.read_text().splitlines()] if self.calls.exists() else []

    def test_inherited_servers_are_stopped_before_publish_and_shutdown_is_confirmed_before_measurement(self):
        result = self.launch("--gate", "--record", str(self.record))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        events = self.events()
        self.assertEqual(len(events), 4, events)
        self.assertEqual(events[0]["args"], ["build-server", "shutdown"])
        self.assertEqual(events[0]["servers"], ["inherited-compiler-server", "inherited-msbuild-node"])
        self.assertEqual(events[1]["args"][0], "publish")
        self.assertEqual(events[1]["servers"], [])
        self.assertEqual(events[2]["args"], ["build-server", "shutdown"])
        self.assertEqual(events[3]["event"], "apphost")
        self.assertEqual(events[3]["servers"], [])
        self.assertEqual(list(self.servers.iterdir()), [])
        self.assertTrue(self.record.exists())

    def test_publish_disables_build_servers_without_changing_release_rid_or_project(self):
        self.launch()
        publish = next(event["args"] for event in self.events() if event.get("args", [None])[0] == "publish")
        self.assertIn("--disable-build-servers", publish)
        self.assertEqual(publish[publish.index("-c") + 1], "Release")
        self.assertEqual(publish[publish.index("-r") + 1], "osx-arm64")
        self.assertTrue(publish[1].endswith("RetroDownfall.Arcanum.Covenant.Benchmarks.csproj"))

    def test_inherited_server_shutdown_failure_is_unmeasurable_and_never_publishes_or_executes(self):
        result = self.launch("--gate", "--record", str(self.record), fail_shutdown=1)
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("controlled shutdown failure", result.stdout + result.stderr)
        self.assertEqual([event["args"] for event in self.events()], [["build-server", "shutdown"]])
        self.assertFalse(self.record.exists())

    def test_post_publish_shutdown_failure_is_unmeasurable_and_never_executes(self):
        result = self.launch("--gate", "--record", str(self.record), fail_shutdown=2)
        self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
        self.assertIn("controlled shutdown failure", result.stdout + result.stderr)
        events = self.events()
        self.assertEqual([event["args"][0] for event in events], ["build-server", "publish", "build-server"])
        self.assertFalse(any(event["event"] == "apphost" for event in events))
        self.assertFalse(self.record.exists())

    def test_gate_breach_is_still_exit_one_and_the_record_is_retained(self):
        result = self.launch("--gate", "--record", str(self.record), apphost_exit=1)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        apphost = next(event for event in self.events() if event["event"] == "apphost")
        self.assertEqual(apphost["args"], ["--gate", "--out", str(self.record)])
        self.assertTrue(self.record.exists())

    def test_record_and_compare_arguments_are_forwarded_without_a_second_measurement(self):
        baseline = self.work / "baseline.json"
        baseline.write_text("{}")
        result = self.launch("--record", str(self.record), "--compare", str(baseline))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        apphosts = [event for event in self.events() if event["event"] == "apphost"]
        self.assertEqual(len(apphosts), 1)
        self.assertEqual(apphosts[0]["args"], ["--out", str(self.record), "--compare", str(baseline)])


if __name__ == "__main__":
    unittest.main()
