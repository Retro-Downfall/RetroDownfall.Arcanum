#!/usr/bin/env python3
"""Qualify exact baseline/candidate revisions sequentially on one native Windows host.

Copy this helper outside the checkout. Logs and partial evidence survive failure.
Success requires both shipping gates, candidate native gates, identical discovered
toolchains, complete inventories, and restoration of the exact clean candidate.
"""
from __future__ import annotations
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import platform
import re
import stat
import subprocess
import sys
import xml.etree.ElementTree as ET

PROJECT = "src/RetroDownfall.Arcanum.Cli/RetroDownfall.Arcanum.Cli.csproj"
PROFILE_OWNERS = (
    "scripts/verify-shipping-publish.sh", "scripts/verify-published-apphost.sh",
    "scripts/verify-aot-il-warnings.sh", "scripts/verify-native-sqlcipher.sh",
    "Directory.Build.props", "Directory.Build.targets", "global.json", PROJECT,
    "tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj",
    "tests/RetroDownfall.Arcanum.Tests/Packaging/PublishedArcanumSessionSmokeTests.cs",
    "tests/RetroDownfall.Arcanum.RegexAotSmoke/RetroDownfall.Arcanum.RegexAotSmoke.csproj",
    "tests/RetroDownfall.Arcanum.RegexAotSmoke/Program.cs",
)
PROPERTIES = (
    "CppLinker", "CppLibCreator", "IlcToolsPath", "IlcSdkPath", "IlcHostPackagePath",
    "RuntimePackagePath", "_NETCoreAppRuntimePackPath", "IlcFrameworkNativePath",
    "ILCompilerTargetsPath", "NETCoreSdkVersion", "NETCoreSdkPortableRuntimeIdentifier",
    "_hostArchitecture", "_targetArchitecture", "_VCVarsAllFound", "IlcUseEnvironmentalTools",
    "PublishAotUsingRuntimePack", "_IsPublishing", "_FindVCVarsallOutput", "PublishAot", "SelfContained",
    "PublishSingleFile", "DebugType", "NativeDebugSymbols", "OptimizationPreference",
)
PACK_ITEMS = ("FrameworkReference", "AdditionalNativeLibraryDirectories", "ResolvedILCompilerPack",
              "ResolvedTargetILCompilerPack", "ResolvedFrameworkReference", "ResolvedRuntimePack")
TEST_PROJECT = "tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj"
FOCUSED_FILTER = "FullyQualifiedName~AiReflection|FullyQualifiedName~AiPackageVersion|FullyQualifiedName~NativeSqlCipherDeliveryTests|FullyQualifiedName~SqlCipherCompatibilityTests"


def within(path, parent):
    try:
        path.resolve().relative_to(parent.resolve())
        return True
    except ValueError:
        return False


def write_json(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def file_record(path):
    before = path.stat()
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    after = path.stat()
    if (before.st_size, before.st_mtime_ns) != (after.st_size, after.st_mtime_ns):
        raise RuntimeError("File changed while measuring: " + str(path))
    return {"bytes": after.st_size, "sha256": digest.hexdigest()}


def sorted_relative_paths(directory, paths):
    return sorted(paths, key=lambda path: path.relative_to(directory).as_posix())


def publishing_properties(rid):
    return ["-p:Configuration=Release", "-p:RuntimeIdentifier=" + rid,
            "-p:PublishAot=true", "-p:SelfContained=true", "-p:PublishSingleFile=false", "-p:_IsPublishing=true"]


def restore_arguments(rid):
    # dotnet restore --runtime populates RuntimeIdentifiers, not the singular property that
    # activates the CLI's AOT profile. Restore and evaluate the same publishing graph.
    return publishing_properties(rid)


def inventory(directory, rid):
    if rid not in ("win-x64", "win-arm64") or not directory.is_dir():
        raise ValueError("Missing publish directory or unsupported RID")
    records = []
    for path in [directory, *sorted_relative_paths(directory, directory.rglob("*"))]:
        mode = path.lstat()
        if path.is_symlink() or getattr(mode, "st_file_attributes", 0) & 0x400:
            raise ValueError("Linked/reparse publish entry: " + str(path))
        if stat.S_ISDIR(mode.st_mode):
            continue
        if not stat.S_ISREG(mode.st_mode):
            raise ValueError("Nonregular publish entry: " + str(path))
        if path.name.casefold() in ("hostfxr.dll", "hostpolicy.dll", "retrodownfall.arcanum.cli.dll"):
            raise ValueError("Managed fallback in shipping publish: " + str(path))
        records.append({"path": path.relative_to(directory).as_posix(), **file_record(path)})
    by_name = {item["path"]: item for item in records}
    required = ("RetroDownfall.Arcanum.Cli.exe", "e_sqlcipher.dll", "libonigwrap.dll")
    for name in required:
        if name not in by_name or by_name[name]["bytes"] <= 0:
            raise ValueError("Incomplete shipping publish: " + name)
    return {"files": records, "apphostBytes": by_name[required[0]]["bytes"],
            "totalBytes": sum(item["bytes"] for item in records),
            "symbolPolicy": "All regular publish files included, including any symbols and licenses."}


def default_runner(argv, cwd, log, timeout):
    with log.open("wb") as output:
        process = subprocess.Popen(argv, cwd=cwd, stdin=subprocess.DEVNULL,
                                   stdout=output, stderr=subprocess.STDOUT)
        try:
            code = process.wait(timeout=timeout)
        except BaseException:
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                               stdout=output, stderr=subprocess.STDOUT, check=False)
            else:
                process.kill()
            process.wait()
            raise
    text = log.read_text(encoding="utf-8", errors="replace")
    if code:
        raise RuntimeError("Command failed (exit " + str(code) + "): " + str(log))
    return text


class Pair:
    def __init__(self, repo, baseline, candidate, rid, sdk, output, runner=None, platform_name=None):
        self.repo = Path(repo).resolve()
        self.baseline, self.candidate, self.rid, self.sdk = baseline, candidate, rid, sdk
        self.output = Path(output)
        self.runner = runner or default_runner
        self.platform = platform_name or platform.system()
        self.sequence = 0

    def command(self, argv, label, timeout=7200):
        self.sequence += 1
        stem = self.output / (f"{self.sequence:03d}-" + label)
        write_json(stem.with_suffix(".command.json"), {"argv": argv, "cwd": str(self.repo)})
        log = stem.with_suffix(".log")
        log.touch()
        print("==> " + label, flush=True)
        return self.runner(argv, self.repo, log, timeout)

    def git(self, *args):
        return self.command(["git", *args], "git-" + args[0], 120).strip()

    def check_source(self, expected):
        if self.git("rev-parse", "HEAD") != expected:
            raise RuntimeError("Wrong source revision; expected " + expected)
        if self.git("status", "--porcelain", "--untracked-files=all"):
            raise RuntimeError("Qualification requires a clean checkout")

    def validate(self):
        if self.platform != "Windows":
            raise ValueError("Native Windows host required")
        if self.rid not in ("win-x64", "win-arm64"):
            raise ValueError("Unsupported native RID")
        if not all(re.fullmatch(r"[0-9a-f]{40}", value) for value in (self.baseline, self.candidate)):
            raise ValueError("Both source revisions require full lowercase 40-character SHAs")
        if self.baseline == self.candidate or not re.fullmatch(r"\d+\.\d+\.\d+", self.sdk):
            raise ValueError("Distinct revisions and exact stable SDK version required")
        self.check_source(self.candidate)
        for revision in (self.baseline, self.candidate):
            if self.git("rev-parse", "--verify", revision + "^{commit}") != revision:
                raise ValueError("Source identity did not resolve exactly")
        self.git("merge-base", "--is-ancestor", self.baseline, self.candidate)
        changed = self.git("diff", "--name-only", self.baseline, self.candidate, "--", *PROFILE_OWNERS)
        if changed:
            raise ValueError("Qualification gate/profile changed between revisions: " + changed)
        for owner in PROFILE_OWNERS:
            if self.git("cat-file", "-t", self.baseline + ":" + owner) != "blob":
                raise ValueError("Missing qualification profile owner: " + owner)
        if self.command(["dotnet", "--version"], "sdk-version", 120).strip() != self.sdk:
            raise ValueError("Actual SDK differs from requested exact version")
        info = self.command(["dotnet", "--info"], "sdk-info", 120)
        if re.findall(r"^\s*RID:\s*(\S+)\s*$", info, re.M) != [self.rid]:
            raise ValueError("SDK host RID must match the native qualification RID")

    def capture_toolchain(self, label):
        # ResolveFrameworkReferences populates actual compiler/runtime packs. SetupOSSpecificProps
        # runs findvcvarsall: PATH link and inherited vcvars variables do not prove the selected tool.
        args = ["dotnet", "msbuild", PROJECT,
                "-target:ResolveFrameworkReferences;SetupOSSpecificProps", "-nologo", "-m:1", "-nodeReuse:false",
                *publishing_properties(self.rid),
                "-p:BuildProjectReferences=false", "-getProperty:" + ",".join(PROPERTIES),
                "-getItem:" + ",".join(PACK_ITEMS)]
        raw = json.loads(self.command(args, label + "-native-properties", 300))
        properties = {key: raw["Properties"][key] for key in PROPERTIES}
        if properties["_VCVarsAllFound"] != "0" or properties["IlcUseEnvironmentalTools"] == "true":
            raise RuntimeError("Actual SDK-discovered native toolchain is unavailable")
        for key, expected in (("PublishAot", "true"), ("SelfContained", "true"),
                              ("PublishSingleFile", "false"), ("_IsPublishing", "true"), ("NETCoreSdkVersion", self.sdk)):
            if properties[key] != expected:
                raise RuntimeError("Unexpected native shipping/toolchain property: " + key)
        architecture = self.rid.removeprefix("win-")
        if properties["_hostArchitecture"] != architecture or properties["_targetArchitecture"] != architecture:
            raise RuntimeError("Native toolchain architecture differs from RID")
        libraries = raw["Items"]["AdditionalNativeLibraryDirectories"]
        if not libraries or not properties["_FindVCVarsallOutput"]:
            raise RuntimeError("Missing actual Windows SDK/native library discovery")
        frameworks = raw["Items"]["ResolvedFrameworkReference"]
        if not frameworks or not any(item.get("RuntimePackVersion") for item in frameworks):
            raise RuntimeError("Missing resolved native runtime pack version metadata")
        if properties["PublishAotUsingRuntimePack"] == "true" and not any(
                item.get("Identity") == "Microsoft.NETCore.App" and item.get("RuntimePackLabels") == "NativeAOT"
                for item in raw["Items"]["FrameworkReference"]):
            raise RuntimeError("Publishing context did not select the Native AOT runtime pack")
        for item in libraries:
            path = Path(item["Identity"])
            if not path.is_absolute() or not path.is_dir():
                raise RuntimeError("Missing discovered native library directory: " + str(path))
        tools = {"linker": Path(properties["CppLinker"]), "librarian": Path(properties["CppLibCreator"]),
                 "compiler": Path(properties["IlcToolsPath"]) / "ilc.exe"}
        for path in tools.values():
            if not path.is_absolute() or not path.is_file():
                raise RuntimeError("Missing actual native tool executable: " + str(path))
        literals = ",".join("'" + str(path).replace("'", "''") + "'" for path in tools.values())
        script = "$ErrorActionPreference='Stop'; @(" + literals + ") | ForEach-Object { $p=Get-Item -LiteralPath $_; [pscustomobject]@{path=$p.FullName;fileVersion=$p.VersionInfo.FileVersion;productVersion=$p.VersionInfo.ProductVersion} } | ConvertTo-Json -Compress"
        encoded = base64.b64encode(script.encode("utf-16-le")).decode("ascii")
        versions = json.loads(self.command(["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive",
                                           "-EncodedCommand", encoded], label + "-native-versions", 120))
        by_path = {os.path.normcase(item["path"]): item for item in versions}
        executables = {}
        for kind, path in tools.items():
            version = by_path.get(os.path.normcase(str(path)))
            if not version or not version.get("fileVersion"):
                raise RuntimeError("Missing actual native executable version: " + str(path))
            executables[kind] = {**version, **file_record(path)}
        native = Path(properties["IlcSdkPath"])
        if not native.is_absolute() or not native.is_dir():
            raise RuntimeError("Missing resolved Native AOT runtime pack")
        runtime_files = [{"path": path.relative_to(native).as_posix(), **file_record(path)}
                         for path in sorted_relative_paths(native, native.rglob("*")) if path.is_file()]
        if not runtime_files:
            raise RuntimeError("Empty resolved Native AOT runtime pack")
        result = {"properties": properties, "packItems": {key: raw["Items"][key] for key in PACK_ITEMS},
                  "executables": executables, "runtimePackFiles": runtime_files}
        write_json(self.output / (label + "-toolchain.json"), result)
        return result

    def revision(self, label, sha, expected_toolchain):
        self.git("checkout", "--detach", sha)
        self.check_source(sha)
        stage = self.output / label
        stage.mkdir()
        self.command(["dotnet", "restore", PROJECT, *restore_arguments(self.rid),
                      "--disable-parallel"], label + "-restore", 900)
        if label == "candidate":
            self.command(["bash", "scripts/verify-aot-il-warnings.sh", self.rid], "candidate-aot-audit")
            self.command(["bash", "scripts/verify-native-sqlcipher.sh", "--rid", self.rid, "--strict"],
                         "candidate-native-sqlcipher", 300)
        before = self.capture_toolchain(label + "-before")
        if expected_toolchain is not None and before != expected_toolchain:
            raise RuntimeError("Baseline/candidate toolchain drift")
        publish = self.command(["bash", "-c", 'cygpath -u "$1"', "arcanum-native-pair-path",
                                str(stage / "publish")], label + "-publish-path", 120).strip()
        if not publish.startswith("/") or "\n" in publish or "\r" in publish:
            raise RuntimeError("Could not resolve a Git Bash absolute publish path")
        self.command(["bash", "scripts/verify-shipping-publish.sh", "--rid", self.rid,
                      "--retain-publish-dir", publish], label + "-shipping")
        after = self.capture_toolchain(label + "-after")
        if before != after:
            raise RuntimeError("Native toolchain drift during shipping publish")
        self.check_source(sha)
        manifest = {"sourceSha": sha, "sourceTree": self.git("rev-parse", "HEAD^{tree}"),
                    "rid": self.rid, "toolchain": before, **inventory(stage / "publish", self.rid)}
        write_json(stage / "manifest.json", manifest)
        if label == "candidate":
            self.command(["dotnet", "build", TEST_PROJECT, "-c", "Release", "--disable-build-servers", "-m:1"], "candidate-focused-build", 1800)
            results = stage / "focused"
            self.command(["dotnet", "test", TEST_PROJECT, "-c", "Release", "--no-build", "--no-restore",
                          "--filter", FOCUSED_FILTER, "--blame-hang-timeout", "15m", "--blame-hang-dump-type", "none",
                          "--logger", "trx;LogFileName=focused.trx", "--results-directory", str(results)], "candidate-focused-tests", 1800)
            manifest["focusedTests"] = validate_focused_trx(results / "focused.trx")
            self.check_source(sha)
            write_json(stage / "manifest.json", manifest)
        return manifest

    def execute(self):
        if not self.output.is_absolute() or within(self.output, self.repo) or self.output.exists():
            raise ValueError("Output must be a fresh absolute directory outside the checkout")
        self.output.mkdir(parents=True)
        report = {"schema": 1, "success": False, "rid": self.rid, "sdkVersion": self.sdk,
                  "baselineSha": self.baseline, "candidateSha": self.candidate}
        validated = False
        error = None
        try:
            self.validate()
            validated = True
            report["baseline"] = self.revision("baseline", self.baseline, None)
            report["candidate"] = self.revision("candidate", self.candidate, report["baseline"]["toolchain"])
            report["deltaBytes"] = {key: report["candidate"][key] - report["baseline"][key]
                                    for key in ("apphostBytes", "totalBytes")}
        except BaseException as failure:
            error = failure
            report["error"] = str(failure)
        finally:
            if validated:
                try:
                    self.git("checkout", "--detach", self.candidate)
                    self.check_source(self.candidate)
                except BaseException as failure:
                    report["restoreError"] = str(failure)
                    error = error or failure
            report["success"] = error is None
            write_json(self.output / "summary.json", report)
        if error is not None:
            raise error
        return report


def validate_focused_trx(path):
    try:
        root = ET.parse(path).getroot()
        results = root.find("{*}Results")
        summary = root.find("{*}ResultSummary")
        counters = summary.find("{*}Counters")
        rows = list(results.findall("{*}UnitTestResult"))
        if not rows or summary.get("outcome") != "Completed":
            raise ValueError("Focused test results are empty or incomplete")
        if any(row.get("outcome") != "Passed" for row in rows):
            raise ValueError("Every focused test must pass without skips")
        for key in ("total", "executed", "passed"):
            if int(counters.get(key, "-1")) != len(rows):
                raise ValueError("Focused TRX counters do not match actual results")
        for key in ("failed", "error", "timeout", "aborted", "notExecuted", "notRunnable", "inconclusive", "disconnected", "inProgress", "pending"):
            if int(counters.get(key, "0")) != 0:
                raise ValueError("Focused TRX contains incomplete or nonpassing cases")
        identities = [row.get("testId") for row in rows]
        executions = [row.get("executionId") for row in rows]
        if not all(identities) or len(set(identities)) != len(rows) or not all(executions) or len(set(executions)) != len(rows):
            raise ValueError("Focused TRX contains duplicate or missing test identities")
        definitions = {}
        for definition in root.find("{*}TestDefinitions").findall("{*}UnitTest"):
            method = definition.find("{*}TestMethod")
            identity = definition.get("id")
            if not identity or identity in definitions or method is None:
                raise ValueError("Malformed focused test definition")
            definitions[identity] = (method.get("className"), method.get("name"))
        observed = {definitions[identity] for identity in identities}
        required = {
            "RetroDownfall.Arcanum.Tests.Build.AiReflectionProductionClosureTests": "Every_compiled_production_project_is_free_of_unsafe_ai_reflection_entry_points",
            "RetroDownfall.Arcanum.Tests.Build.AiPackageVersionTests": None,
            "RetroDownfall.Arcanum.Tests.NativeSqlCipher.NativeSqlCipherDeliveryTests": None,
            "RetroDownfall.Arcanum.Tests.NativeSqlCipher.SqlCipherCompatibilityTests": None,
        }
        for owner, method in required.items():
            if not any(found_owner == owner and (method is None or found_method == method) for found_owner, found_method in observed):
                raise ValueError("Required focused production/package/native proof did not run: " + owner)
        return {"passed": len(rows), "classes": sorted({owner for owner, _ in observed}), "trx": str(path)}
    except (OSError, ET.ParseError, AttributeError, KeyError, TypeError) as error:
        raise ValueError("Missing or malformed focused TRX: " + str(path)) from error


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline-sha", required=True)
    parser.add_argument("--candidate-sha", required=True)
    parser.add_argument("--rid", required=True, choices=("win-x64", "win-arm64"))
    parser.add_argument("--sdk-version", required=True)
    parser.add_argument("--output-dir", required=True, type=Path)
    args = parser.parse_args()
    try:
        repo = Path(subprocess.check_output(["git", "rev-parse", "--show-toplevel"], text=True).strip())
        if within(Path(__file__), repo):
            raise ValueError("Copy the helper outside the checkout before switching revisions")
        Pair(repo, args.baseline_sha, args.candidate_sha, args.rid, args.sdk_version, args.output_dir).execute()
        print("Paired native Windows qualification passed: " + str(args.output_dir / "summary.json"))
        return 0
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print("Paired native Windows qualification failed: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
