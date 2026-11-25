"""Exercise the release bootstrap with real archives and controlled network/process boundaries."""

import contextlib
import hashlib
import importlib.util
import io
from pathlib import Path
import stat
import struct
import subprocess
import tempfile
import unittest
from unittest import mock
import urllib.error
import warnings
import zipfile


SCRIPT = Path(__file__).resolve().parents[1] / "install_ripgrep.py"
TARGETS = {
    "win-x64": "x86_64-pc-windows-msvc",
    "win-arm64": "aarch64-pc-windows-msvc",
}
EXECUTABLE = b"controlled executable bytes"


def archive_bytes(target, mode="valid"):
    output = io.BytesIO()
    member = f"ripgrep-15.2.0-{target}/rg.exe"
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr("../must-not-extract.txt", "outside")
        if mode == "directory":
            entry = zipfile.ZipInfo(member)
            entry.external_attr = (stat.S_IFDIR | 0o755) << 16 | 0x10
            archive.writestr(entry, "")
        elif mode == "missing":
            archive.writestr("unexpected/rg.exe", EXECUTABLE)
        else:
            archive.writestr(member, EXECUTABLE)
            if mode == "duplicate":
                with warnings.catch_warnings():
                    warnings.simplefilter("ignore", UserWarning)
                    archive.writestr(member, EXECUTABLE)
    contents = bytearray(output.getvalue())
    if mode in ("encrypted", "invalid-deflate", "unsupported-compression"):
        with zipfile.ZipFile(io.BytesIO(contents)) as archive:
            entry = archive.getinfo(member)
            if mode == "invalid-deflate":
                data_offset = entry.header_offset + 30 + len(member.encode("utf-8"))
                contents[data_offset] = 0x07  # Deflate's reserved block type.
            else:
                if mode == "encrypted":
                    contents[entry.header_offset + 6] |= 1
                else:
                    struct.pack_into("<H", contents, entry.header_offset + 8, 99)
                central = archive.start_dir
                while contents[central:central + 4] == b"PK\x01\x02":
                    name_length, extra_length, comment_length = struct.unpack_from("<HHH", contents, central + 28)
                    if contents[central + 46:central + 46 + name_length] == member.encode("utf-8"):
                        if mode == "encrypted":
                            contents[central + 8] |= 1
                        else:
                            struct.pack_into("<H", contents, central + 10, 99)
                        break
                    central += 46 + name_length + extra_length + comment_length
    return bytes(contents)


class InstallRipgrepTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(SCRIPT.is_file(), "The release ripgrep bootstrap has not been implemented.")
        spec = importlib.util.spec_from_file_location("arcanum_install_ripgrep", SCRIPT)
        self.helper = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.helper)
        temporary = tempfile.TemporaryDirectory(prefix="ripgrep-install-tests-")
        self.addCleanup(temporary.cleanup)
        self.work = Path(temporary.name)
        self.runner_temp = self.work / "runner temp"
        self.runner_temp.mkdir()
        self.path_export = self.work / "github-path"
        self.path_export.write_text("previous-tool-directory\n", encoding="utf-8")
        self.environment = {"RUNNER_TEMP": str(self.runner_temp), "GITHUB_PATH": str(self.path_export)}
        self.downloads = []
        self.pauses = []
        self.executions = []
        self.failures = []
        self.process_mode = "success"
        self.archive_mode = "valid"
        self.clock = lambda: 0

    def open_download(self, url, *, timeout):
        self.downloads.append((url, timeout))
        self.assertEqual(url, self.expected_url)
        self.assertGreater(timeout, 0)
        self.assertLessEqual(timeout, 30)
        if self.failures:
            raise self.failures.pop(0)
        return io.BytesIO(self.payload)

    def run_executable(self, command, **kwargs):
        executable = Path(command[0])
        self.assertEqual(command[1:], ["--version"])
        self.assertTrue(executable.is_file())
        self.assertEqual(executable.read_bytes(), EXECUTABLE)
        self.assertEqual(executable.parent.parent, self.runner_temp)
        self.assertEqual(self.path_export.read_text(encoding="utf-8"), "previous-tool-directory\n")
        self.assertFalse(kwargs.get("shell", False))
        self.assertGreater(kwargs.get("timeout", 0), 0)
        self.assertLessEqual(kwargs["timeout"], 30)
        self.executions.append(executable)
        if self.process_mode == "timeout":
            raise subprocess.TimeoutExpired(command, kwargs["timeout"])
        return subprocess.CompletedProcess(
            command, 8 if self.process_mode == "failure" else 0,
            "ripgrep 0.0.0\n" if self.process_mode == "wrong-version" else "ripgrep 15.2.0\nfeatures: controlled\n", "",
        )

    def invoke(self, rid="win-arm64", *, corrupt=False, environ=None, platform="win32"):
        target = TARGETS[rid]
        self.payload = archive_bytes(target, self.archive_mode)
        digest = hashlib.sha256(self.payload).hexdigest()
        if corrupt:
            self.payload += b"corruption"
        self.expected_url = f"https://github.com/BurntSushi/ripgrep/releases/download/15.2.0/ripgrep-15.2.0-{target}.zip"
        self.stderr = io.StringIO()
        production_target = self.helper.ASSETS[rid][0]
        with mock.patch.dict(self.helper.ASSETS, {rid: (production_target, digest)}), contextlib.redirect_stderr(self.stderr), contextlib.redirect_stdout(io.StringIO()):
            return self.helper.main(
                ["--rid", rid], environ=self.environment if environ is None else environ,
                platform=platform, urlopen=self.open_download, sleep=self.pauses.append,
                run=self.run_executable, clock=self.clock,
            )

    def assert_failed_without_export(self, result):
        self.assertNotEqual(result, 0, self.stderr.getvalue())
        self.assertEqual(self.path_export.read_text(encoding="utf-8"), "previous-tool-directory\n")
        self.assertEqual(list(self.runner_temp.iterdir()), [])

    def test_installs_each_native_asset_and_exports_only_its_verified_private_directory(self):
        for rid in TARGETS:
            with self.subTest(rid=rid):
                self.path_export.write_text("previous-tool-directory\n", encoding="utf-8")
                self.assertEqual(self.invoke(rid), 0, self.stderr.getvalue())
                installed = self.executions[-1]
                self.assertEqual(self.path_export.read_text(encoding="utf-8").splitlines(),
                                 ["previous-tool-directory", str(installed.parent)])
                self.assertEqual(list(installed.parent.iterdir()), [installed])
                self.assertFalse((self.work / "must-not-extract.txt").exists())
                self.assertFalse((self.runner_temp / "must-not-extract.txt").exists())
        self.assertNotEqual(self.executions[0].parent, self.executions[1].parent)

    def test_retries_transient_http_failure_then_installs_a_verified_download(self):
        for status in (408, 429, 500, 503, 504):
            with self.subTest(status=status):
                self.path_export.write_text("previous-tool-directory\n", encoding="utf-8")
                self.downloads.clear()
                self.pauses.clear()
                self.executions.clear()
                self.failures = [urllib.error.HTTPError("https://fixture", status, "controlled", {}, None)]
                self.assertEqual(self.invoke(), 0, self.stderr.getvalue())
                self.assertEqual(len(self.downloads), 2)
                self.assertEqual(len(self.pauses), 1)
                self.assertTrue(self.executions[0].exists())

    def test_network_failures_stop_after_three_attempts_and_clean_the_private_directory(self):
        self.failures = [urllib.error.URLError(TimeoutError("controlled")) for _ in range(4)]
        self.assert_failed_without_export(self.invoke())
        self.assertEqual(len(self.downloads), 3)
        self.assertEqual(len(self.pauses), 2)
        self.assertEqual(self.executions, [])

    def test_nontransient_http_failure_does_not_retry(self):
        self.failures = [urllib.error.HTTPError("https://fixture", 404, "controlled", {}, None)]
        self.assert_failed_without_export(self.invoke())
        self.assertEqual(len(self.downloads), 1)
        self.assertEqual(self.pauses, [])

    def test_corrupt_download_is_never_extracted_executed_exported_or_retried(self):
        self.assert_failed_without_export(self.invoke(corrupt=True))
        self.assertIn("SHA-256", self.stderr.getvalue())
        self.assertEqual(self.executions, [])
        self.assertEqual(len(self.downloads), 1)
        self.assertEqual(self.pauses, [])

    def test_archive_must_have_one_expected_regular_executable(self):
        for mode in ("missing", "duplicate", "directory"):
            with self.subTest(mode=mode):
                self.archive_mode = mode
                self.assert_failed_without_export(self.invoke())
                self.assertEqual(self.executions, [])
                self.assertEqual(self.pauses, [])

    def test_download_size_bound_is_enforced_before_hash_or_executable_admission(self):
        with mock.patch.object(self.helper, "MAX_ARCHIVE_BYTES", 64):
            self.assert_failed_without_export(self.invoke())
        self.assertIn("size", self.stderr.getvalue())
        self.assertEqual(self.executions, [])
        self.assertEqual(len(self.downloads), 1)

    def test_verified_but_encrypted_or_invalid_compressed_member_fails_with_cleanup(self):
        for mode in ("encrypted", "invalid-deflate", "unsupported-compression"):
            with self.subTest(mode=mode):
                self.archive_mode = mode
                try:
                    result = self.invoke()
                except Exception as error:
                    self.fail(f"Malformed ZIP escaped the bootstrap's controlled failure: {type(error).__name__}: {error}")
                self.assert_failed_without_export(result)
                self.assertEqual(self.executions, [])
                self.assertEqual(self.pauses, [])

    def test_slow_stream_is_bounded_and_exhausts_only_three_attempts(self):
        ticks = iter(range(0, 1000, 100))
        self.clock = lambda: next(ticks)
        self.assert_failed_without_export(self.invoke())
        self.assertEqual(len(self.downloads), 3)
        self.assertEqual(self.executions, [])

    def test_uncompressed_executable_size_is_bounded_before_execution(self):
        with mock.patch.object(self.helper, "MAX_EXECUTABLE_BYTES", 4):
            self.assert_failed_without_export(self.invoke())
        self.assertEqual(self.executions, [])

    def test_failed_wrong_version_or_hung_executable_is_cleaned_without_path_export(self):
        for mode in ("failure", "wrong-version", "timeout"):
            with self.subTest(mode=mode):
                self.process_mode = mode
                self.assert_failed_without_export(self.invoke())

    def test_missing_environment_or_nonwindows_host_refuses_before_download(self):
        for key in self.environment:
            with self.subTest(missing=key):
                environment = {name: value for name, value in self.environment.items() if name != key}
                self.assert_failed_without_export(self.invoke(environ=environment))
        self.assert_failed_without_export(self.invoke(platform="darwin"))
        self.assertEqual(self.downloads, [])

    def test_invalid_rid_refuses_before_download(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as failure:
            self.helper.main(["--rid", "win-unknown"], urlopen=self.open_download)
        self.assertEqual(failure.exception.code, 2)
        self.assertEqual(self.downloads, [])

    def test_path_export_failure_removes_the_verified_executable(self):
        self.environment["GITHUB_PATH"] = str(self.work / "missing" / "github-path")
        self.assert_failed_without_export(self.invoke())
        self.assertEqual(len(self.executions), 1)
        self.assertFalse(self.executions[0].exists())


if __name__ == "__main__":
    unittest.main()
