"""Injected-process tests of the MSVC environment helper; no native Windows tools are run."""

import contextlib
import importlib.util
import io
import os
from pathlib import Path
import re
import subprocess
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts/setup_msvc_environment.py"


def read_exports(path):
    result = {}
    lines = path.read_text(encoding="utf-8").splitlines()
    index = 0
    while index < len(lines):
        line = lines[index]
        if "<<" in line:
            key, delimiter = line.split("<<", 1)
            index += 1
            value = []
            while index < len(lines) and lines[index] != delimiter:
                value.append(lines[index])
                index += 1
            if index == len(lines):
                raise AssertionError("Unterminated environment value")
            result[key] = "\n".join(value)
        else:
            key, value = line.split("=", 1)
            result[key] = value
        index += 1
    return result


class SetupMsvcEnvironmentTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(SCRIPT.is_file(), "The local Windows MSVC environment helper is required.")
        spec = importlib.util.spec_from_file_location("arcanum_setup_msvc_environment", SCRIPT)
        self.helper = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.helper)
        temporary = tempfile.TemporaryDirectory(prefix="msvc-environment-tests-")
        self.addCleanup(temporary.cleanup)
        self.work = Path(temporary.name)
        self.program_files = self.work / "Program Files (x86)"
        self.vswhere = self.program_files / "Microsoft Visual Studio" / "Installer" / "vswhere.exe"
        self.vswhere.parent.mkdir(parents=True)
        self.vswhere.touch()
        self.visual_studio = self.work / "Visual Studio Enterprise"
        self.vcvars = self.visual_studio / "VC" / "Auxiliary" / "Build" / "vcvarsall.bat"
        self.vcvars.parent.mkdir(parents=True)
        self.vcvars.touch()
        self.system_root = self.work / "Windows"
        self.cmd = self.system_root / "System32" / "cmd.exe"
        self.cmd.parent.mkdir(parents=True)
        self.cmd.touch()
        self.destination = self.work / "github-env"
        self.environment = {
            "ProgramFiles(x86)": str(self.program_files),
            "GITHUB_ENV": str(self.destination),
            "SystemRoot": str(self.system_root),
            "COMSPEC": r"C:\Untrusted\cmd.exe",
            "PATH": r"C:\Base",
            "KEEP": "unchanged",
            "CaseKept": "same",
        }
        self.calls = []
        self.mode = "success"
        self.cmd_lines = [
            r"PATH=C:\VC;C:\Base;C:\VC",
            r"INCLUDE=C:\Headers;C:\Headers;C:\OtherHeaders",
            r"LIB=C:\Lib;C:\Lib",
            r"LIBPATH=C:\LibPath;C:\LibPath",
            "KEEP=unchanged",
            "casekept=same",
            "VALUE_WITH_EQUALS=first=second=third",
            "VSCMD_ARG_TGT_ARCH=arm64",
        ]

    def fake_run(self, command, **kwargs):
        self.calls.append((command, kwargs))
        if isinstance(command, (list, tuple)) and str(command[0]) == str(self.vswhere):
            if self.mode == "vswhere_oserror":
                raise OSError("controlled discovery error")
            return subprocess.CompletedProcess(
                command, 9 if self.mode == "vswhere_failed" else 0,
                "" if self.mode == "vswhere_empty" else str(self.visual_studio) + "\r\n",
                "controlled vswhere failure" if self.mode == "vswhere_failed" else "",
            )
        serialized = command if isinstance(command, str) else " ".join(str(part) for part in command)
        sentinel = re.search(r"echo ([A-Za-z0-9_]+)([ \t]*)&&\s*set", serialized)
        self.assertIsNotNone(sentinel, "The command must print a distinct sentinel before its environment dump.")
        # cmd's ECHO includes spaces preceding the command separator in the emitted line.
        marker = sentinel.group(1) + sentinel.group(2)
        output = "Developer command prompt banner\r\nPREAMBLE_ONLY=do-not-export\r\n"
        if self.mode == "cmd_error_output":
            output += "[ERROR:vcvarsall.bat] Invalid parameters\r\n"
        if self.mode != "missing_sentinel":
            output += marker + "\r\n"
        output += "\r\n".join(self.cmd_lines) + "\r\n"
        return subprocess.CompletedProcess(
            command, 23 if self.mode == "cmd_failed" else 0, output,
            "controlled vcvarsall failure" if self.mode == "cmd_failed" else "",
        )

    def invoke(self, arch="amd64", platform="win32"):
        self.stderr = io.StringIO()
        with contextlib.redirect_stderr(self.stderr):
            return self.helper.main(
                ["--arch", arch], environ=self.environment, run=self.fake_run, platform=platform,
            )

    def assert_failed_without_writes(self, result):
        self.assertNotEqual(result, 0)
        self.assertFalse(self.destination.exists(), "A refused setup must not export a partial environment.")
        self.assertTrue(self.stderr.getvalue().strip(), "A refused setup must explain the failure.")

    def test_only_windows_main_is_admitted(self):
        self.assert_failed_without_writes(self.invoke(platform="darwin"))
        self.assertEqual(self.calls, [])

    def test_architecture_surface_is_closed(self):
        with self.assertRaises(SystemExit) as failure:
            self.invoke(arch="amd64 & injected-command")
        self.assertEqual(failure.exception.code, 2)
        self.assertEqual(self.calls, [])
        self.assertFalse(self.destination.exists())

    def test_all_supported_architectures_are_passed_exactly_with_required_discovery_component(self):
        for arch, component in (
            ("amd64", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64"),
            ("amd64_arm64", "Microsoft.VisualStudio.Component.VC.Tools.ARM64"),
            ("arm64", "Microsoft.VisualStudio.Component.VC.Tools.ARM64"),
        ):
            with self.subTest(arch=arch):
                self.calls.clear()
                self.destination.unlink(missing_ok=True)
                self.assertEqual(self.invoke(arch), 0, self.stderr.getvalue())
                self.assertEqual(len(self.calls), 2)
                discovery = self.calls[0][0]
                for flag in ("-latest", "-prerelease", "-products", "-requires", "-property"):
                    self.assertIn(flag, discovery)
                self.assertEqual(discovery[discovery.index("-products") + 1], "*")
                self.assertIn(component, discovery)
                self.assertEqual(discovery[discovery.index("-property") + 1], "installationPath")
                cmd = self.calls[1][0]
                self.assertIsInstance(cmd, str, "Do not apply list2cmdline escaping to cmd's quoted /c body.")
                self.assertTrue(cmd.startswith(f'"{self.cmd}" /u /v:off /d /s /c "call "{self.vcvars}" {arch} && echo '))
                self.assertRegex(cmd, r'echo [A-Za-z0-9_]+&& set"$')
                self.assertNotIn(self.environment["COMSPEC"], cmd)
                self.assertFalse(self.calls[1][1].get("shell", False))
                self.assertEqual(self.calls[1][1].get("encoding"), "utf-16-le")

    def test_exports_only_case_insensitive_changes_and_preserves_equals_and_path_order(self):
        self.assertEqual(self.invoke(), 0, self.stderr.getvalue())
        exported = read_exports(self.destination)
        self.assertNotIn("KEEP", exported)
        self.assertNotIn("casekept", exported)
        self.assertNotIn("PREAMBLE_ONLY", exported)
        self.assertEqual(exported["VALUE_WITH_EQUALS"], "first=second=third")
        self.assertEqual(exported["PATH"], r"C:\VC;C:\Base")
        self.assertEqual(exported["INCLUDE"], r"C:\Headers;C:\OtherHeaders")
        self.assertEqual(exported["LIB"], r"C:\Lib")
        self.assertEqual(exported["LIBPATH"], r"C:\LibPath")
        self.assertEqual(exported["VSCMD_ARG_TGT_ARCH"], "arm64")

    def test_windows_uppercase_environment_keys_locate_the_same_installed_tools(self):
        self.environment = {key.upper(): value for key, value in self.environment.items()}
        self.assertEqual(self.invoke(), 0, self.stderr.getvalue())
        self.assertEqual(str(self.calls[0][0][0]), str(self.vswhere))
        self.assertTrue(self.destination.exists())

    def test_missing_github_env_refuses_before_running_tools(self):
        del self.environment["GITHUB_ENV"]
        self.assert_failed_without_writes(self.invoke())
        self.assertEqual(self.calls, [])

    def test_missing_vswhere_refuses_without_searching_path_or_running_cmd(self):
        self.vswhere.unlink()
        self.assert_failed_without_writes(self.invoke())
        self.assertEqual(self.calls, [])

    def test_discovery_failure_empty_result_and_process_error_never_run_cmd(self):
        for mode in ("vswhere_failed", "vswhere_empty", "vswhere_oserror"):
            with self.subTest(mode=mode):
                self.calls.clear()
                self.mode = mode
                self.assert_failed_without_writes(self.invoke())
                self.assertEqual(len(self.calls), 1)

    def test_missing_vcvarsall_refuses_before_cmd(self):
        self.vcvars.unlink()
        self.assert_failed_without_writes(self.invoke())
        self.assertEqual(len(self.calls), 1)

    def test_quote_newline_and_expansion_characters_in_discovered_paths_are_refused_before_cmd(self):
        for name in ('VS "injected"', "VS\ninjected", "VS%INJECTED%"):
            with self.subTest(name=name):
                self.calls.clear()
                self.visual_studio = self.work / name
                vcvars = self.visual_studio / "VC" / "Auxiliary" / "Build" / "vcvarsall.bat"
                vcvars.parent.mkdir(parents=True)
                vcvars.touch()
                self.assert_failed_without_writes(self.invoke())
                self.assertEqual(len(self.calls), 1)

    def test_missing_trusted_system_cmd_refuses_without_using_comspec(self):
        self.cmd.unlink()
        self.assert_failed_without_writes(self.invoke())
        self.assertFalse(any(isinstance(command, str) for command, _ in self.calls))

    def test_nonzero_cmd_and_error_output_even_exit_zero_refuse_without_export(self):
        for mode in ("cmd_failed", "cmd_error_output"):
            with self.subTest(mode=mode):
                self.mode = mode
                self.assert_failed_without_writes(self.invoke())

    def test_missing_environment_sentinel_refuses_without_export(self):
        self.mode = "missing_sentinel"
        self.assert_failed_without_writes(self.invoke())

    def test_malformed_environment_dump_refuses_without_export(self):
        self.cmd_lines.append("unexpected output after the sentinel")
        self.assert_failed_without_writes(self.invoke())

    def test_no_effective_environment_change_refuses_without_export(self):
        self.cmd_lines = ["KEEP=unchanged", "casekept=same", r"PATH=C:\Base"]
        self.assert_failed_without_writes(self.invoke())

    def test_invalid_changed_key_refuses_without_any_partial_append(self):
        self.destination.write_text("EXISTING=preserved\n", encoding="utf-8")
        self.cmd_lines.append("bad-key=not-an-exportable-key")
        self.assertNotEqual(self.invoke(), 0)
        self.assertEqual(self.destination.read_text(encoding="utf-8"), "EXISTING=preserved\n")

    def test_environment_export_preserves_multiline_values_and_avoids_delimiter_collisions(self):
        delimiters = iter(("COLLIDES", "SAFE_DELIMITER"))
        value = "first=part\nCOLLIDES\nlast=part"
        self.helper.export_environment(
            {"MULTILINE": value}, self.destination, delimiter_factory=lambda: next(delimiters),
        )
        exported = read_exports(self.destination)
        self.assertEqual(exported, {"MULTILINE": value})
        self.assertIn("MULTILINE<<SAFE_DELIMITER\n", self.destination.read_text(encoding="utf-8"))

    def test_export_rejects_newline_key_before_writing_any_value(self):
        with self.assertRaises(ValueError):
            self.helper.export_environment({"VALID": "first", "INJECTED\nKEY": "second"}, self.destination)
        self.assertFalse(self.destination.exists())


if __name__ == "__main__":
    unittest.main()
