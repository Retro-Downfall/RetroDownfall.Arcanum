#!/usr/bin/env python3
"""Export the installed MSVC environment for Arcanum's three Windows build architectures."""

import argparse
import os
from pathlib import Path
import re
import subprocess
import sys
import uuid


ARCHITECTURES = ("amd64", "amd64_arm64", "arm64")
PATH_VARIABLES = frozenset(("path", "include", "lib", "libpath"))
ENVIRONMENT_KEY = re.compile(r"[A-Za-z_][A-Za-z0-9_]*\Z")


def environment_value(environment, key):
    normalized = key.casefold()
    return next((value for name, value in environment.items() if name.casefold() == normalized), "")


def safe_command_path(value):
    # CALL expands percent expressions a second time. Quotes/newlines could escape the command
    # body, so installed paths containing any of them cannot safely name this batch invocation.
    if not value or any(character in value for character in ('"', "\r", "\n", "\0", "%")):
        raise ValueError("The installed MSVC command path is empty or contains unsafe command characters.")
    return Path(value)


def export_environment(changes, destination, *, delimiter_factory=None):
    """Validate the complete update before appending GitHub's UTF-8 environment-file records."""
    if not changes:
        raise ValueError("MSVC setup produced no changed environment variables.")
    delimiter_factory = delimiter_factory or (lambda: "ARCANUM_MSVC_" + uuid.uuid4().hex)
    records = []
    for key, value in changes.items():
        if not ENVIRONMENT_KEY.fullmatch(key):
            raise ValueError("MSVC setup produced an invalid environment variable name.")
        if "\0" in value:
            raise ValueError("MSVC setup produced an invalid environment variable value.")
        delimiter = delimiter_factory()
        while delimiter in value.splitlines():
            delimiter = delimiter_factory()
        if not ENVIRONMENT_KEY.fullmatch(delimiter):
            raise ValueError("The environment-file delimiter is invalid.")
        records.append(f"{key}<<{delimiter}\n{value}\n{delimiter}\n")
    with Path(destination).open("a", encoding="utf-8", newline="\n") as output:
        output.write("".join(records))


def discover_vcvarsall(arch, environment, run):
    program_files = safe_command_path(environment_value(environment, "ProgramFiles(x86)"))
    vswhere = program_files / "Microsoft Visual Studio" / "Installer" / "vswhere.exe"
    if not vswhere.is_file():
        raise ValueError("The installed Visual Studio locator vswhere.exe was not found.")
    component = "Microsoft.VisualStudio.Component.VC.Tools." + ("x86.x64" if arch == "amd64" else "ARM64")
    discovered = run(
        [str(vswhere), "-latest", "-prerelease", "-products", "*", "-requires", component,
         "-property", "installationPath", "-utf8"],
        capture_output=True, text=True, encoding="utf-8-sig", check=False, env=environment,
    )
    if discovered.returncode != 0:
        raise ValueError("Visual Studio discovery failed; vswhere.exe returned a nonzero exit code.")
    installation = safe_command_path(discovered.stdout.strip())
    vcvarsall = installation / "VC" / "Auxiliary" / "Build" / "vcvarsall.bat"
    if not vcvarsall.is_file():
        raise ValueError("The selected Visual Studio installation has no vcvarsall.bat.")
    return vcvarsall


def changed_environment(output, sentinel, original):
    lines = output.splitlines()
    if lines.count(sentinel) != 1:
        raise ValueError("MSVC setup did not return its unique environment sentinel.")
    baseline = {key.casefold(): value for key, value in original.items()}
    changes = {}
    seen = set()
    for line in lines[lines.index(sentinel) + 1:]:
        if not line:
            continue
        if "=" not in line:
            raise ValueError("MSVC setup returned a malformed environment dump.")
        key, value = line.split("=", 1)
        normalized = key.casefold()
        if normalized in seen:
            raise ValueError("MSVC setup returned duplicate environment variable names.")
        seen.add(normalized)
        if baseline.get(normalized) == value:
            continue
        if normalized in PATH_VARIABLES:
            value = ";".join(dict.fromkeys(value.split(";")))
        if baseline.get(normalized) != value:
            changes[key] = value
    return changes


def main(argv=None, *, environ=None, run=None, platform=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--arch", required=True, choices=ARCHITECTURES)
    arguments = parser.parse_args(argv)
    environment = dict(os.environ if environ is None else environ)
    run = subprocess.run if run is None else run
    platform = sys.platform if platform is None else platform
    try:
        if platform != "win32":
            raise ValueError("MSVC environment setup is available only on Windows.")
        destination = environment_value(environment, "GITHUB_ENV")
        if not destination:
            raise ValueError("GITHUB_ENV is required to export the MSVC environment.")
        cmd = safe_command_path(environment_value(environment, "SystemRoot")) / "System32" / "cmd.exe"
        if not cmd.is_file():
            raise ValueError("The trusted SystemRoot/System32/cmd.exe was not found.")
        vcvarsall = discover_vcvarsall(arguments.arch, environment, run)
        sentinel = "ARCANUM_MSVC_ENV_" + uuid.uuid4().hex
        # Pass a complete command line. list2cmdline would backslash-escape the nested quotes,
        # which cmd does not interpret as escapes. /s strips this one outer /c quote pair.
        # /u makes the batch's SET/ECHO output Unicode; /v:off prevents ! expansion in paths.
        command = f'"{cmd}" /u /v:off /d /s /c "call "{vcvarsall}" {arguments.arch} && echo {sentinel}&& set"'
        configured = run(command, capture_output=True, text=True, encoding="utf-16-le", check=False, env=environment)
        if configured.returncode != 0:
            raise ValueError("MSVC setup failed; vcvarsall.bat returned a nonzero exit code.")
        if any(re.match(r"\s*\[ERROR", line, re.IGNORECASE)
               for line in (configured.stdout + "\n" + configured.stderr).splitlines()):
            raise ValueError("MSVC setup reported an error despite its exit code.")
        changes = changed_environment(configured.stdout, sentinel, environment)
        export_environment(changes, destination)
    except (OSError, UnicodeError, ValueError) as error:
        print(f"MSVC environment setup failed: {error}", file=sys.stderr)
        return 1
    print(f"Configured MSVC environment for {arguments.arch} ({len(changes)} changed variables).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
