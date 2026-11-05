import importlib.util
import json
from pathlib import Path, PureWindowsPath
import subprocess
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET
from contextlib import redirect_stdout
import io
import shutil


SCRIPT = Path(__file__).resolve().parents[1] / "qualify_windows_native_pair.py"
SPEC = importlib.util.spec_from_file_location("qualify_windows_native_pair", SCRIPT)
pair = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(pair)


def make_trx(path):
    cases = [
        ("RetroDownfall.Arcanum.Tests.Build.AiReflectionProductionClosureTests", "Every_compiled_production_project_is_free_of_unsafe_ai_reflection_entry_points"),
        ("RetroDownfall.Arcanum.Tests.Build.AiPackageVersionTests", "All_four_first_party_references_use_the_reviewed_ai_package_version"),
        ("RetroDownfall.Arcanum.Tests.Build.AiPackageVersionTests", "All_three_resolved_ai_packages_match_the_reviewed_version"),
        ("RetroDownfall.Arcanum.Tests.NativeSqlCipher.NativeSqlCipherDeliveryTests", "Delivered_asset_matches_the_manifest"),
        ("RetroDownfall.Arcanum.Tests.NativeSqlCipher.SqlCipherCompatibilityTests", "Encrypted_round_trip"),
    ]
    root = ET.Element("TestRun")
    definitions = ET.SubElement(root, "TestDefinitions")
    results = ET.SubElement(root, "Results")
    for index, (owner, method) in enumerate(cases):
        identity = "test-" + str(index)
        definition = ET.SubElement(definitions, "UnitTest", id=identity)
        ET.SubElement(definition, "TestMethod", className=owner, name=method)
        ET.SubElement(results, "UnitTestResult", testId=identity, executionId="run-" + str(index), outcome="Passed", testName=owner + "." + method)
    summary = ET.SubElement(root, "ResultSummary", outcome="Completed")
    ET.SubElement(summary, "Counters", total="5", executed="5", passed="5", failed="0", error="0", timeout="0", aborted="0", notExecuted="0")
    ET.ElementTree(root).write(path, encoding="utf-8", xml_declaration=True)


class FocusedEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.path = Path(self.temporary.name) / "focused.trx"
        make_trx(self.path)

    def test_actual_production_guard_package_and_native_results_must_all_pass(self):
        self.assertEqual(5, pair.validate_focused_trx(self.path)["passed"])

    def test_empty_skipped_failed_duplicate_missing_or_unexecuted_guard_is_rejected(self):
        for kind in ("empty", "skipped", "failed", "counter", "missing-class", "missing-actual-guard", "duplicate"):
            with self.subTest(kind=kind):
                make_trx(self.path)
                tree = ET.parse(self.path)
                root = tree.getroot()
                results = root.find("Results")
                if kind == "empty":
                    results.clear()
                elif kind in ("skipped", "failed"):
                    results[0].set("outcome", "NotExecuted" if kind == "skipped" else "Failed")
                elif kind == "counter":
                    root.find("ResultSummary/Counters").set("total", "99")
                elif kind == "missing-class":
                    root.find("TestDefinitions")[4].find("TestMethod").set("className", "OtherTests")
                elif kind == "missing-actual-guard":
                    root.find("TestDefinitions")[0].find("TestMethod").set("name", "Fixture_only")
                else:
                    results[1].set("testId", results[0].get("testId"))
                tree.write(self.path)
                with self.assertRaises((ValueError, RuntimeError)):
                    pair.validate_focused_trx(self.path)


class WindowsManifestOrderTests(unittest.TestCase):
    def test_windows_paths_use_exact_relative_string_order(self):
        root = PureWindowsPath("C:/publish")
        names = ["symbols.pdb", "libonigwrap.dll", "RetroDownfall.Arcanum.Cli.exe", "LICENSE.txt", "e_sqlcipher.dll"]
        paths = [root / name for name in names]
        actual = [path.relative_to(root).as_posix() for path in pair.sorted_relative_paths(root, paths)]
        self.assertEqual(["LICENSE.txt", "RetroDownfall.Arcanum.Cli.exe", "e_sqlcipher.dll", "libonigwrap.dll", "symbols.pdb"], actual)


class ActualSdkPublishingContextTests(unittest.TestCase):
    def test_fresh_restore_imports_the_real_native_setup_target_in_the_publishing_context(self):
        if shutil.which("dotnet") is None:
            self.fail("The actual shipping SDK is required for the publishing-context regression")
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            project = root / "Fresh.csproj"
            project.write_text('''<Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><IsAotCompatible>true</IsAotCompatible></PropertyGroup>
              <PropertyGroup Condition="'$(RuntimeIdentifier)' != ''"><PublishAot>true</PublishAot><SelfContained>true</SelfContained><PublishSingleFile>false</PublishSingleFile></PropertyGroup>
            </Project>''')
            info = subprocess.check_output(["dotnet", "--info"], text=True)
            import re
            host = re.search(r"^\s*RID:\s*(\S+)", info, re.M).group(1)
            rid = host if host in ("win-x64", "win-arm64") else "win-x64"
            self.assertFalse((root / "obj").exists())
            restored = subprocess.run(["dotnet", "restore", str(project), *pair.restore_arguments(rid)],
                                      cwd=root, capture_output=True, text=True, timeout=900)
            self.assertEqual(0, restored.returncode, restored.stdout + restored.stderr)
            properties = pair.publishing_properties(rid)
            evaluated = subprocess.run(["dotnet", "msbuild", str(project), "-nologo", *properties,
                                        "-getProperty:ILCompilerTargetsPath,_IsPublishing,PublishAot"],
                                       cwd=root, capture_output=True, text=True, check=True, timeout=120)
            values = json.loads(evaluated.stdout)["Properties"]
            self.assertTrue(values["ILCompilerTargetsPath"], "Fresh restore did not import the Native AOT compiler targets")
            self.assertEqual("true", values["_IsPublishing"])
            self.assertEqual("true", values["PublishAot"])
            processed = root / "expanded.xml"
            subprocess.run(["dotnet", "msbuild", str(project), "-nologo", *properties,
                            "-preprocess:" + str(processed)], cwd=root, check=True, capture_output=True, timeout=120)
            targets = ET.parse(processed).getroot().findall("{*}Target")
            self.assertTrue(any(target.get("Name") == "SetupOSSpecificProps" for target in targets))


class ToolchainTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.output = self.root / "evidence"
        self.output.mkdir()
        self.tools = {name: self.root / name for name in ("link.exe", "lib.exe", "ilc.exe")}
        for name, path in self.tools.items():
            path.write_bytes(name.encode())
        native = self.root / "native"
        native.mkdir()
        (native / "Runtime.lib").write_bytes(b"native runtime")
        properties = {key: "" for key in pair.PROPERTIES}
        properties.update(CppLinker=str(self.tools["link.exe"]), CppLibCreator=str(self.tools["lib.exe"]),
                          IlcToolsPath=str(self.root), IlcSdkPath=str(native),
                          NETCoreSdkVersion="10.0.403", _VCVarsAllFound="0",
                          _hostArchitecture="x64", _targetArchitecture="x64", _FindVCVarsallOutput="actual SDK libraries",
                          PublishAot="true", SelfContained="true", PublishSingleFile="false", _IsPublishing="true", PublishAotUsingRuntimePack="true")
        self.raw = {"Properties": properties, "Items": {key: [] for key in pair.PACK_ITEMS}}
        self.raw["Items"]["AdditionalNativeLibraryDirectories"] = [{"Identity": str(native)}]
        self.raw["Items"]["ResolvedFrameworkReference"] = [{"Identity": "Microsoft.NETCore.App", "RuntimePackVersion": "10.0.14", "RuntimePackPath": str(native)}]
        self.raw["Items"]["FrameworkReference"] = [{"Identity": "Microsoft.NETCore.App", "RuntimePackLabels": "NativeAOT"}]
        self.versions = [{"path": str(path), "fileVersion": "10.0.14", "productVersion": "10.0.14"} for path in self.tools.values()]
        self.commands = []

    def runner(self, argv, cwd, log, timeout):
        self.commands.append(argv)
        value = self.raw if argv[0] == "dotnet" else self.versions
        text = json.dumps(value)
        log.write_text(text)
        return text

    def capture(self):
        return pair.Pair(self.root, "a" * 40, "b" * 40, "win-x64", "10.0.403",
                         self.output, runner=self.runner, platform_name="Windows").capture_toolchain("fixture")

    def test_snapshot_uses_resolved_tools_pack_bytes_and_post_resolution_targets(self):
        result = self.capture()
        self.assertEqual(str(self.tools["link.exe"]), result["executables"]["linker"]["path"])
        self.assertEqual(64, len(result["executables"]["compiler"]["sha256"]))
        self.assertEqual("Runtime.lib", result["runtimePackFiles"][0]["path"])
        self.assertIn("-target:ResolveFrameworkReferences;SetupOSSpecificProps", self.commands[0])
        self.assertTrue(any("ResolvedFrameworkReference" in argument for argument in self.commands[0]))
        self.assertNotIn(["link", "/?"], self.commands)

    def test_missing_actual_tool_version_or_discovery_cannot_qualify(self):
        self.versions[0]["fileVersion"] = ""
        with self.assertRaisesRegex(RuntimeError, "version"):
            self.capture()
        self.versions[0]["fileVersion"] = "10.0.14"
        self.raw["Properties"]["_VCVarsAllFound"] = "1"
        with self.assertRaisesRegex(RuntimeError, "toolchain"):
            self.capture()

    def test_unrecorded_framework_pack_is_refused_even_when_sdk_path_exists(self):
        self.raw["Items"]["ResolvedFrameworkReference"] = []
        with self.assertRaisesRegex(RuntimeError, "runtime pack"):
            self.capture()

    def test_actual_tool_and_pack_content_changes_are_visible_in_the_snapshot(self):
        before = self.capture()
        self.tools["ilc.exe"].write_bytes(b"different compiler")
        compiler_changed = self.capture()
        self.assertNotEqual(before, compiler_changed)
        (self.root / "native" / "Runtime.lib").write_bytes(b"different native runtime")
        self.assertNotEqual(compiler_changed, self.capture())


class PairTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.repo = self.root / "repo"
        self.repo.mkdir()
        self.git("init", "-q")
        self.git("config", "user.name", "Qualification fixture")
        self.git("config", "user.email", "qualification@example.invalid")
        for name in pair.PROFILE_OWNERS:
            path = self.repo / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("unchanged qualification owner\n")
        self.baseline = self.commit()
        (self.repo / "change.txt").write_text("candidate\n")
        self.candidate = self.commit()
        self.out = self.root / "evidence"
        self.commands = []
        self.fail_gate = None
        self.wrong_rid = False
        self.converted_paths = {}

    def git(self, *args):
        return subprocess.check_output(["git", "-C", str(self.repo), *args], stderr=subprocess.PIPE).decode().strip()

    def commit(self):
        self.git("add", "-A")
        self.git("commit", "-qm", "qualification fixture")
        return self.git("rev-parse", "HEAD")

    def fake_run(self, argv, cwd, log, timeout):
        self.commands.append(argv)
        if argv[0] == "git":
            output = subprocess.check_output(argv, cwd=cwd, stderr=subprocess.STDOUT).decode()
        elif argv[:2] == ["dotnet", "--version"]:
            output = "10.0.403\n"
        elif argv[:2] == ["dotnet", "--info"]:
            output = " RID: " + ("win-arm64" if self.wrong_rid else "win-x64") + "\n"
        elif argv[0] == "bash":
            if argv[1] == "-c":
                converted = "/pair-fixture/" + str(len(self.converted_paths))
                self.converted_paths[converted] = argv[-1]
                return converted + "\n"
            if self.fail_gate and self.fail_gate in argv[1]:
                raise RuntimeError("fixture gate failed")
            if "--retain-publish-dir" in argv:
                argument = argv[argv.index("--retain-publish-dir") + 1]
                publish = Path(self.converted_paths[argument])
                publish.mkdir()
                self.publish_files(publish)
            output = "gate passed\n"
        elif argv[:2] == ["dotnet", "test"]:
            results = Path(argv[argv.index("--results-directory") + 1])
            results.mkdir(parents=True, exist_ok=True)
            make_trx(results / "focused.trx")
            output = "focused tests passed\n"
        else:
            output = ""
        log.write_text(output)
        return output

    @staticmethod
    def publish_files(directory):
        for name, data in {"RetroDownfall.Arcanum.Cli.exe": b"apphost", "e_sqlcipher.dll": b"cipher", "libonigwrap.dll": b"regex", "symbols.pdb": b"symbols", "LICENSE.txt": b"license"}.items():
            (directory / name).write_bytes(data)

    def subject(self, **kwargs):
        options = dict(repo=self.repo, baseline=self.baseline, candidate=self.candidate,
                       rid="win-x64", sdk="10.0.403", output=self.out,
                       runner=self.fake_run, platform_name="Windows")
        options.update(kwargs)
        return pair.Pair(**options)

    def execute(self, **kwargs):
        with redirect_stdout(io.StringIO()), patch.object(pair.Pair, "capture_toolchain", return_value={"exact": "same"}):
            return self.subject(**kwargs).execute()

    def test_manifests_include_every_regular_file_and_separate_apphost_from_total(self):
        publish = self.root / "publish"
        publish.mkdir()
        self.publish_files(publish)
        result = pair.inventory(publish, "win-x64")
        self.assertEqual(7, result["apphostBytes"])
        self.assertEqual(32, result["totalBytes"])
        self.assertEqual(["LICENSE.txt", "RetroDownfall.Arcanum.Cli.exe", "e_sqlcipher.dll", "libonigwrap.dll", "symbols.pdb"], [item["path"] for item in result["files"]])
        self.assertTrue(all(len(item["sha256"]) == 64 for item in result["files"]))

    def test_incomplete_managed_or_linked_publish_cannot_supply_size_evidence(self):
        for bad in ("missing-apphost", "missing-sidecar", "managed-fallback", "symlink"):
            with self.subTest(bad=bad):
                publish = self.root / bad
                publish.mkdir()
                self.publish_files(publish)
                if bad == "missing-apphost":
                    (publish / "RetroDownfall.Arcanum.Cli.exe").unlink()
                elif bad == "missing-sidecar":
                    (publish / "e_sqlcipher.dll").unlink()
                elif bad == "managed-fallback":
                    (publish / "hostfxr.dll").write_bytes(b"fallback")
                else:
                    (publish / "linked").symlink_to(publish / "LICENSE.txt")
                with self.assertRaises((ValueError, RuntimeError)):
                    pair.inventory(publish, "win-x64")

    def test_success_uses_same_checkout_path_and_restores_exact_candidate(self):
        result = self.execute()
        self.assertTrue(result["success"])
        self.assertEqual(self.candidate, self.git("rev-parse", "HEAD"))
        self.assertEqual(self.baseline, result["baseline"]["sourceSha"])
        self.assertEqual(self.candidate, result["candidate"]["sourceSha"])
        checkouts = [command[-1] for command in self.commands if command[:3] == ["git", "checkout", "--detach"]]
        self.assertEqual([self.baseline, self.candidate, self.candidate], checkouts)
        self.assertEqual(2, len([c for c in self.commands if "--retain-publish-dir" in c]))
        self.assertEqual(1, len([c for c in self.commands if c[:2] == ["bash", "scripts/verify-aot-il-warnings.sh"]]))
        self.assertEqual(1, len([c for c in self.commands if c[:2] == ["bash", "scripts/verify-native-sqlcipher.sh"] and "--strict" in c]))
        self.assertTrue(json.loads((self.out / "summary.json").read_text())["success"])
        self.assertEqual(5, result["candidate"]["focusedTests"]["passed"])
        tests = [c for c in self.commands if c[:2] == ["dotnet", "test"]]
        self.assertEqual(1, len(tests))
        self.assertEqual(pair.FOCUSED_FILTER, tests[0][tests[0].index("--filter") + 1])

    def test_gate_failure_keeps_partial_evidence_and_restores_candidate(self):
        self.fail_gate = "verify-aot-il-warnings"
        with self.assertRaisesRegex(RuntimeError, "fixture gate failed"):
            self.execute()
        self.assertEqual(self.candidate, self.git("rev-parse", "HEAD"))
        self.assertTrue((self.out / "baseline" / "publish").is_dir())
        self.assertFalse(json.loads((self.out / "summary.json").read_text())["success"])

    def test_each_shipping_gate_receives_a_git_bash_absolute_retention_path(self):
        self.execute()
        conversions = [command for command in self.commands if command[:2] == ["bash", "-c"]]
        self.assertEqual(2, len(conversions))
        self.assertTrue(all(command[2] == 'cygpath -u "$1"' for command in conversions))

    def test_any_toolchain_drift_fails_before_success_and_restores_candidate(self):
        for snapshots in ([{"id": 1}, {"id": 2}], [{"id": 1}, {"id": 1}, {"id": 2}]):
            with self.subTest(snapshots=snapshots):
                output = self.root / ("drift-" + str(len(snapshots)))
                with redirect_stdout(io.StringIO()), patch.object(pair.Pair, "capture_toolchain", side_effect=snapshots):
                    with self.assertRaisesRegex(RuntimeError, "toolchain"):
                        self.subject(output=output).execute()
                self.assertEqual(self.candidate, self.git("rev-parse", "HEAD"))
                self.assertFalse(json.loads((output / "summary.json").read_text())["success"])

    def test_dirty_wrong_source_wrong_native_rid_or_short_sha_never_checks_out(self):
        scenarios = [dict(baseline="b458d92b"), dict(candidate="0" * 40)]
        for index, changes in enumerate(scenarios):
            with self.subTest(changes=changes):
                with self.assertRaises((ValueError, RuntimeError)):
                    self.execute(output=self.root / ("invalid-" + str(index)), **changes)
        self.wrong_rid = True
        with self.assertRaisesRegex((ValueError, RuntimeError), "RID"):
            self.execute(output=self.root / "wrong-rid")
        self.wrong_rid = False
        (self.repo / "untracked.txt").write_text("preserve unrelated work")
        with self.assertRaisesRegex((ValueError, RuntimeError), "clean"):
            self.execute(output=self.root / "dirty")
        self.assertFalse(any(c[:3] == ["git", "checkout", "--detach"] for c in self.commands))

    def test_changed_gate_or_profile_is_rejected_before_checkout(self):
        path = self.repo / pair.PROFILE_OWNERS[0]
        path.write_text("changed gate\n")
        candidate = self.commit()
        with self.assertRaisesRegex((ValueError, RuntimeError), "profile|gate"):
            self.execute(candidate=candidate)
        self.assertFalse(any(c[:3] == ["git", "checkout", "--detach"] for c in self.commands))

    def test_existing_or_in_checkout_evidence_directory_is_refused(self):
        self.out.mkdir()
        for output in (self.out, self.repo / "evidence"):
            with self.subTest(output=output):
                with self.assertRaises((ValueError, RuntimeError)):
                    self.execute(output=output)

    def test_nonancestor_baseline_cannot_start_any_publish(self):
        self.git("checkout", "--detach", self.baseline)
        (self.repo / "other.txt").write_text("another history branch")
        other = self.commit()
        self.git("checkout", "--detach", self.candidate)
        with self.assertRaises(subprocess.CalledProcessError):
            self.execute(baseline=other)
        self.assertFalse(any(c[0] == "bash" for c in self.commands))

    def test_post_publish_source_mutation_cannot_report_success(self):
        original = self.fake_run

        def mutate(argv, cwd, log, timeout):
            result = original(argv, cwd, log, timeout)
            if "--retain-publish-dir" in argv:
                (self.repo / pair.PROFILE_OWNERS[0]).write_text("unrelated mutation to preserve")
            return result

        with self.assertRaisesRegex(RuntimeError, "clean"):
            self.execute(runner=mutate)
        report = json.loads((self.out / "summary.json").read_text())
        self.assertFalse(report["success"])
        self.assertIn("restoreError", report)


if __name__ == "__main__":
    unittest.main()
