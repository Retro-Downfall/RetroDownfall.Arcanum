"""Stub-only qualification of the Guild Native AOT gate; never invokes a real SDK."""
import json
import os
import shutil
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/verify-guild-aot.sh"
MARKER = "Guild Native AOT graph smoke passed."

DOTNET_STUB = r'''#!/usr/bin/env python3
import json
import os
import shutil
from pathlib import Path
import sys

args = sys.argv[1:]
with open(os.environ["GUILD_STUB_CALLS"], "a", encoding="utf-8") as log:
    log.write(json.dumps(args) + "\n")
mode = os.environ.get("GUILD_STUB_MODE", "success")
if args == ["--info"]:
    if mode == "sdk_unavailable":
        sys.exit(1)
    print(" RID: " + os.environ["GUILD_STUB_HOST_RID"])
    sys.exit(0)
if not args or args[0] != "publish":
    sys.exit(90)
if mode != "no_ilc":
    print("Generating native code")
if mode == "publish_failed":
    sys.exit(1)
if mode == "warning":
    print("ILC : warning IL2026: unqualified reflection path")
if mode == "marker_only_in_publish":
    print("Guild Native AOT graph smoke passed.")
output = Path(args[args.index("-o") + 1])
output.mkdir(parents=True)
if mode != "missing_apphost":
    rid = args[args.index("-r") + 1]
    apphost = output / ("RetroDownfall.Arcanum.GuildAotSmoke" + (".exe" if rid.startswith("win-") else ""))
    marker = "other output" if mode in ("missing_marker", "marker_only_in_publish") else "Guild Native AOT graph smoke passed."
    code = 1 if mode == "apphost_failed" else 0
    apphost.write_text("#!/usr/bin/env bash\nprintf '%s\\n' '" + marker + "'\nexit " + str(code) + "\n")
    apphost.chmod(0o644 if mode == "nonexecutable_apphost" else 0o755)
'''


class GuildAotGateTests(unittest.TestCase):
    def setUp(self):
        self.workspace = tempfile.TemporaryDirectory(prefix="guild-aot-gate-tests-")
        self.addCleanup(self.workspace.cleanup)
        self.work = Path(self.workspace.name)
        self.bin = self.work / "bin"
        self.bin.mkdir()
        self.calls = self.work / "calls.jsonl"
        self.write_stub("dotnet", DOTNET_STUB)
        self.write_stub("uname", '#!/usr/bin/env bash\nprintf "%s\\n" "$GUILD_STUB_OS"\n')
        self.env = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ["PATH"],
                        TMPDIR=str(self.work), GUILD_STUB_CALLS=str(self.calls),
                        GUILD_STUB_HOST_RID="osx-arm64", GUILD_STUB_OS="Darwin")

    def write_stub(self, name, text):
        path = self.bin / name
        path.write_text(text)
        path.chmod(0o755)

    def run_gate(self, *args, mode="success", rid="osx-arm64", host_os="Darwin"):
        env = dict(self.env, GUILD_STUB_MODE=mode, GUILD_STUB_HOST_RID=rid,
                   GUILD_STUB_OS=host_os)
        return subprocess.run(["bash", str(SCRIPT), *args], cwd=ROOT, env=env,
                              text=True, capture_output=True, timeout=15)

    def sdk_calls(self):
        return [json.loads(line) for line in self.calls.read_text().splitlines()] if self.calls.exists() else []

    def test_all_native_shipping_rids_publish_and_execute_with_required_properties(self):
        for rid, host_os in (("osx-arm64", "Darwin"), ("win-x64", "MINGW64_NT"), ("win-arm64", "MINGW64_NT")):
            with self.subTest(rid=rid):
                result = self.run_gate("--rid", rid, rid=rid, host_os=host_os)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn(MARKER, result.stdout)
                publish = [args for args in self.sdk_calls() if args[0] == "publish"][-1]
                self.assertIn("Release", publish)
                self.assertEqual(publish[publish.index("-r") + 1], rid)
                self.assertTrue(publish[1].endswith("RetroDownfall.Arcanum.GuildAotSmoke.csproj"))
                for setting in ("PublishAot=true", "SelfContained=true", "UseAppHost=true",
                                "TreatWarningsAsErrors=true", "IlcTreatWarningsAsErrors=true",
                                "ILLinkTreatWarningsAsErrors=true", "TrimmerSingleWarn=false",
                                "SuppressTrimAnalysisWarnings=false", "JsonSerializerIsReflectionEnabledByDefault=false"):
                    self.assertIn("-p:" + setting, publish)
                self.assertFalse(any("NoWarn=" in arg or "WarningsNotAsErrors=" in arg for arg in publish))

    def test_each_run_uses_fresh_artifacts_output_and_cleans_up(self):
        for _ in range(2):
            result = self.run_gate("--rid", "osx-arm64")
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        publishes = [args for args in self.sdk_calls() if args[0] == "publish"]
        artifacts = [args[args.index("--artifacts-path") + 1] for args in publishes]
        outputs = [args[args.index("-o") + 1] for args in publishes]
        self.assertEqual(len(set(artifacts)), 2)
        self.assertEqual(len(set(outputs)), 2)
        for path in artifacts + outputs:
            self.assertFalse(Path(path).exists(), path)

    def test_requires_explicit_shipping_rid(self):
        for args in ((), ("--rid",), ("--rid", "linux-x64"), ("--rid", "osx-x64"), ("--force",)):
            with self.subTest(args=args):
                result = self.run_gate(*args)
                self.assertNotEqual(result.returncode, 0)
                self.assertFalse(any(args[0] == "publish" for args in self.sdk_calls()))

    def test_cross_os_publish_is_refused_before_sdk_publish(self):
        result = self.run_gate("--rid", "osx-arm64", host_os="Linux")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("host", result.stderr)
        self.assertFalse(any(args[0] == "publish" for args in self.sdk_calls()))

    def test_non_native_architecture_is_refused(self):
        result = self.run_gate("--rid", "win-arm64", rid="win-x64", host_os="MINGW64_NT")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("host RID", result.stderr)
        self.assertFalse(any(args[0] == "publish" for args in self.sdk_calls()))

    def test_unavailable_sdk_is_refused(self):
        result = self.run_gate("--rid", "osx-arm64", mode="sdk_unavailable")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("RID", result.stderr)
        self.assertFalse(any(args[0] == "publish" for args in self.sdk_calls()))

    def test_publish_smoke_and_evidence_failures_are_never_reported_as_pass(self):
        for mode, reason in (("publish_failed", "publish"), ("no_ilc", "native compilation"),
                             ("missing_apphost", "apphost"), ("nonexecutable_apphost", "apphost"), ("apphost_failed", "smoke"),
                             ("missing_marker", "marker"), ("marker_only_in_publish", "marker"),
                             ("warning", "warning")):
            with self.subTest(mode=mode):
                result = self.run_gate("--rid", "osx-arm64", mode=mode)
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn(reason, result.stderr.lower())
                self.assertNotIn("Guild Native AOT verification passed", result.stdout)

    def test_failed_evidence_scan_is_not_a_clean_log(self):
        self.write_stub("rg", "#!/usr/bin/env bash\nexit 2\n")
        result = self.run_gate("--rid", "osx-arm64")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("scan", result.stderr)

    def test_missing_required_command_is_a_named_refusal(self):
        for missing in ("dotnet", "rg"):
            with self.subTest(missing=missing):
                isolated_bin = self.work / ("without-" + missing)
                isolated_bin.mkdir()
                (isolated_bin / "dirname").symlink_to(shutil.which("dirname"))
                if missing == "rg":
                    (isolated_bin / "dotnet").symlink_to(self.bin / "dotnet")
                result = subprocess.run([shutil.which("bash"), str(SCRIPT), "--rid", "osx-arm64"],
                                        cwd=ROOT, env=dict(self.env, PATH=str(isolated_bin)),
                                        text=True, capture_output=True, timeout=15)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("required command not found: " + missing, result.stderr)

    def test_ci_invokes_gate_in_all_three_native_aot_lanes(self):
        workflow = (ROOT / ".github/workflows/ci.yml").read_text()
        for job, rid in (("aot-il", "osx-arm64"), ("windows-suite", "win-x64"), ("windows-arm64-suite", "win-arm64")):
            with self.subTest(job=job):
                lines = workflow.splitlines()
                start = lines.index("  " + job + ":") + 1
                end = next((index for index in range(start, len(lines)) if lines[index].startswith("  ") and not lines[index].startswith("    ") and lines[index].strip() and not lines[index].lstrip().startswith("#")), len(lines))
                section = "\n".join(lines[start:end])
                self.assertIn("./scripts/verify-guild-aot.sh --rid " + rid, section)


if __name__ == "__main__":
    unittest.main()
