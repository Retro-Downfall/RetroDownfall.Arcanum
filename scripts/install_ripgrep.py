#!/usr/bin/env python3
"""Install the pinned native Windows ripgrep and print its verified directory for the current step."""

import argparse
import hashlib
import http.client
import io
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
import zipfile
import zlib


VERSION = "15.2.0"
# ZIP digests from https://api.github.com/repos/BurntSushi/ripgrep/releases/tags/15.2.0.
ASSETS = {
    "win-x64": ("x86_64-pc-windows-msvc", "71b2fef860abe467217a538ff31de02f5258807c0129f771846f87bd029aafc5"),
    "win-arm64": ("aarch64-pc-windows-msvc", "e4abca10c3a64ebea742667dd7009449d49403db5460dd6873e389fa2945360f"),
}
MAX_ARCHIVE_BYTES = 8 * 1024 * 1024
MAX_EXECUTABLE_BYTES = 16 * 1024 * 1024
DOWNLOAD_SECONDS = 30
SOCKET_TIMEOUT_SECONDS = 10
MAX_ATTEMPTS = 3


def download_archive(url, *, urlopen, sleep, clock):
    for attempt in range(MAX_ATTEMPTS):
        try:
            deadline = clock() + DOWNLOAD_SECONDS
            with urlopen(url, timeout=SOCKET_TIMEOUT_SECONDS) as response:
                contents = bytearray()
                while True:
                    if clock() >= deadline:
                        raise TimeoutError("The ripgrep download exceeded its time limit.")
                    # read1 performs at most one underlying read, so a trickling response cannot
                    # keep one buffered read alive indefinitely between deadline checks.
                    block = response.read1(min(64 * 1024, MAX_ARCHIVE_BYTES + 1 - len(contents)))
                    if clock() >= deadline:
                        raise TimeoutError("The ripgrep download exceeded its time limit.")
                    if not block:
                        return bytes(contents)
                    contents.extend(block)
                    if len(contents) > MAX_ARCHIVE_BYTES:
                        raise ValueError("The ripgrep archive exceeds the download size limit.")
        except (urllib.error.URLError, TimeoutError, ConnectionError, http.client.IncompleteRead) as error:
            if isinstance(error, urllib.error.HTTPError) and not (
                error.code in (408, 429) or 500 <= error.code <= 599
            ):
                raise
            if attempt + 1 == MAX_ATTEMPTS:
                raise
            print(f"Ripgrep download attempt {attempt + 1} failed; retrying: {error}", file=sys.stderr)
            sleep(attempt + 1)
    raise RuntimeError("The ripgrep download did not complete.")


def extract_executable(contents, target, destination):
    expected = f"ripgrep-{VERSION}-{target}/rg.exe"
    with zipfile.ZipFile(io.BytesIO(contents)) as archive:
        entries = [entry for entry in archive.infolist() if entry.filename == expected]
        if len(entries) != 1:
            raise ValueError("The ripgrep archive must contain exactly one expected rg.exe member.")
        entry = entries[0]
        if entry.flag_bits & 1 or entry.compress_type not in (zipfile.ZIP_STORED, zipfile.ZIP_DEFLATED):
            raise ValueError("The ripgrep executable uses encrypted or unsupported ZIP compression.")
        mode = entry.external_attr >> 16
        if entry.is_dir() or entry.external_attr & 0x10 or stat.S_ISDIR(mode) or stat.S_ISLNK(mode):
            raise ValueError("The expected ripgrep archive member is not a regular executable.")
        if not 0 < entry.file_size <= MAX_EXECUTABLE_BYTES:
            raise ValueError("The ripgrep executable exceeds its allowed size.")
        executable = destination / "rg.exe"
        with archive.open(entry) as source, executable.open("xb") as output:
            copied = 0
            while block := source.read(64 * 1024):
                copied += len(block)
                if copied > MAX_EXECUTABLE_BYTES:
                    raise ValueError("The ripgrep executable exceeds its allowed size.")
                output.write(block)
        if copied != entry.file_size:
            raise ValueError("The ripgrep executable is incomplete.")
    return executable


def main(argv=None, *, environ=None, platform=None, urlopen=None, sleep=None, run=None, clock=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True, choices=ASSETS)
    arguments = parser.parse_args(argv)
    environment = dict(os.environ if environ is None else environ)
    platform = sys.platform if platform is None else platform
    urlopen = urllib.request.urlopen if urlopen is None else urlopen
    sleep = time.sleep if sleep is None else sleep
    run = subprocess.run if run is None else run
    clock = time.monotonic if clock is None else clock
    destination = None
    try:
        if platform != "win32":
            raise ValueError("The release ripgrep bootstrap requires Windows.")
        if not environment.get("RUNNER_TEMP") or not environment.get("GITHUB_PATH"):
            raise ValueError("RUNNER_TEMP and GITHUB_PATH are required for the release bootstrap.")
        runner_temp = Path(environment["RUNNER_TEMP"])
        path_export = Path(environment["GITHUB_PATH"])
        if not runner_temp.is_absolute() or not runner_temp.is_dir() or not path_export.is_absolute():
            raise ValueError("The runner temporary directory and GitHub path file must be absolute paths.")
        if any(character in str(runner_temp) for character in ("\r", "\n", "\0")):
            raise ValueError("The runner temporary directory cannot be exported as one PATH entry.")
        target, expected_sha256 = ASSETS[arguments.rid]
        destination = Path(tempfile.mkdtemp(prefix="arcanum-ripgrep-", dir=runner_temp))
        url = f"https://github.com/BurntSushi/ripgrep/releases/download/{VERSION}/ripgrep-{VERSION}-{target}.zip"
        contents = download_archive(url, urlopen=urlopen, sleep=sleep, clock=clock)
        if hashlib.sha256(contents).hexdigest() != expected_sha256:
            raise ValueError("The downloaded ripgrep archive does not match its pinned SHA-256.")
        executable = extract_executable(contents, target, destination)
        result = run([str(executable), "--version"], capture_output=True, text=True,
                     encoding="utf-8", timeout=15, check=False, env=environment)
        version = result.stdout.splitlines()[0] if result.stdout else ""
        if result.returncode != 0 or not re.fullmatch(r"ripgrep 15\.2\.0(?: \(rev [0-9a-f]+\))?", version):
            raise ValueError("The installed executable did not report ripgrep 15.2.0 successfully.")
        with path_export.open("a", encoding="utf-8", newline="\n") as output:
            output.write(str(destination) + "\n")
    except (OSError, ValueError, UnicodeError, zipfile.BadZipFile, zlib.error, subprocess.SubprocessError,
            http.client.HTTPException) as error:
        print(f"Ripgrep installation failed: {error}", file=sys.stderr)
        if destination is not None:
            try:
                shutil.rmtree(destination)
            except OSError as cleanup_error:
                print(f"Ripgrep temporary-directory cleanup failed: {cleanup_error}", file=sys.stderr)
        return 1
    print(destination)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
