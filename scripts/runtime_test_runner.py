#!/usr/bin/env python3
"""Required serial fresh-process resource and remainder runtime qualification.

No-build is explicitly caller-owned provenance, not proof of source-to-binary lineage.
Coverage attachments are retained separately; the coverage adapter owns their union.
"""

from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time
from urllib.parse import unquote, urlsplit
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
BASE_FILTER = "Category!=Perf&Category!=HostedProducerAnalysis"
LANES = (("resource", BASE_FILTER + "&Category=RaptorScaleProbe"),
         ("remainder", BASE_FILTER + "&Category!=RaptorScaleProbe"))
RAPTOR = "RetroDownfall.Arcanum.Tests.Weave.Tapestry.Raptor.RaptorScaleProbe."
# Existing explicit offline opt-ins only; every new discovered case remains mandatory.
OPTIONAL = frozenset(RAPTOR + name for name in (
    "Complete_global_pipeline_at_one_thousand",
    "Complete_real_clusterer_at_one_thousand",
    "Complete_approximate_graph_at_ten_thousand_uses_less_work_than_exact",
    "Cancellation_at_ten_thousand_occurs_in_actual_descent_with_the_supplied_token",
    "Partial_neighbor_pilot_at_ten_thousand", "Partial_neighbor_pilot_at_fifty_thousand",
    "Phase_reaching_neighbor_pilot_at_fifty_thousand", "First_descent_neighbor_pilot_at_fifty_thousand",
    "Complete_neighbor_graph_at_fifty_thousand", "Complete_global_pipeline_at_ten_thousand",
    "Complete_real_clusterer_at_ten_thousand",
))
LANE_TIMEOUT_SECONDS = 6 * 60 * 60
DISCOVERY_TIMEOUT_SECONDS = 5 * 60
BUILD_TIMEOUT_SECONDS = 30 * 60
COLLECTOR_URI = "datacollector://microsoft/CoverletCodeCoverage/1.0"
MSBUILD_POLICY = ("-m:1", "-nr:false", "-p:UseSharedCompilation=false")

# Reuse only the established owned-process boundary, not its scheduler or selection rules.
_spec = importlib.util.spec_from_file_location("_runtime_process_owner", ROOT / "scripts/hosted_producer_analysis_runner.py")
assert _spec is not None and _spec.loader is not None
_process_owner = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_process_owner)


def sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def artifact(path: Path) -> dict:
    if not path.is_file() or path.stat().st_size == 0:
        raise RuntimeError(f"missing or empty artifact: {path}")
    return {"path": str(path.resolve()), "sha256": sha256(path)}


def run_child(command: list[str], log: Path, timeout: float, record: dict | None = None) -> str:
    record = record if record is not None else {}
    record.update(command=command, joined=False, exitCode=None)
    with log.open("w", encoding="utf-8") as output:
        # Pinned SDK respects this explicit opt-out even with inherited multithreaded mode.
        process = subprocess.Popen(command, env={**os.environ, "MSBUILDUSESERVER": "0"},
                                   stdin=subprocess.DEVNULL, stdout=output,
                                   stderr=subprocess.STDOUT, text=True,
                                   start_new_session=os.name == "posix",
                                   creationflags=_process_owner.process_creation_flags())
        record["pid"] = process.pid
        try:
            _process_owner.attach_process_tree(process)
            try:
                record["exitCode"] = process.wait(timeout=timeout)
            except subprocess.TimeoutExpired as error:
                raise RuntimeError(f"child exceeded {timeout:g}s deadline: {log}") from error
        finally:
            # Includes descendants that outlive an already joined parent.
            _process_owner.terminate_processes([process])
            record["joined"] = process.poll() is not None
            record["exitCode"] = process.returncode
            _process_owner.release_process_tree(process)
    if not record["joined"] or record["exitCode"] != 0:
        raise RuntimeError(f"child failed or unjoined ({record['exitCode']}): {log}")
    return log.read_text(encoding="utf-8")


def source_state(owned_results: Path) -> dict:
    def git(*args: str) -> str:
        return subprocess.run(["git", "-C", str(ROOT), *args], stdin=subprocess.DEVNULL,
                              stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                              check=True, timeout=15).stdout
    status_paths = ["."]
    if owned_results.is_relative_to(ROOT):
        status_paths.append(":(literal,exclude)" + owned_results.relative_to(ROOT).as_posix())
    untracked = {}
    for name in git("ls-files", "--others", "--exclude-standard", "-z", "--", *status_paths).split("\0"):
        if not name:
            continue
        path = ROOT / name
        if not path.is_file():
            raise RuntimeError(f"unsupported non-file untracked input: {name}")
        untracked[name] = sha256(path)
    return {"sha": git("rev-parse", "HEAD").strip(),
            "status": git("status", "--porcelain", "--", *status_paths),
            "untrackedFiles": untracked,
            "trackedDiffSha256": hashlib.sha256(git("diff", "HEAD", "--binary").encode()).hexdigest()}


def validate_results(directory: Path, project: Path) -> None:
    protected = [project.resolve().parent, *(ROOT / name for name in
                 ("src", "tests", "vendor", "build", "scripts", ".github", ".config"))]
    if any(directory.is_relative_to(tree) for tree in protected):
        raise RuntimeError("qualified results must stay outside source/project/build trees")
    if directory.is_relative_to(ROOT):
        tracked = subprocess.run(
            ["git", "-C", str(ROOT), "ls-files", "-z", "--",
             ":(literal)" + directory.relative_to(ROOT).as_posix()],
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            check=True, timeout=15).stdout
        if tracked:
            raise RuntimeError("results directory overlaps tracked inputs")


def build_files(assembly: Path) -> dict[str, str]:
    required = [assembly, assembly.with_suffix(".deps.json"), assembly.with_suffix(".runtimeconfig.json")]
    for path in required:
        artifact(path)
    files = {p.resolve() for p in assembly.parent.iterdir() if p.is_file() and (
        p.suffix.lower() in (".dll", ".pdb") or p.name.endswith((".deps.json", ".runtimeconfig.json"))
        or ("xunit" in p.name.lower() and p.suffix.lower() in (".json", ".config")))}
    return {str(p): sha256(p) for p in sorted(files)}


def method_name(display: str) -> str:
    return display.split("(", 1)[0].strip()


def discovery(output: str) -> Counter:
    found = False
    rows = Counter()
    for raw in output.splitlines():
        if raw.strip() == "The following Tests are available:":
            found = True
        elif found and raw.startswith("    "):
            name = raw.strip()
            if "." in method_name(name) and name:
                rows[name] += 1
    if not rows:
        raise RuntimeError("empty discovery selection")
    return rows


def method_set(rows: Counter) -> set[str]:
    return {method_name(name) for name in rows}


def coverage_artifacts(root: ET.Element, directory: Path) -> dict:
    collectors = [c for c in root.iterfind(".//{*}Collector") if c.get("uri") == COLLECTOR_URI]
    deployments = list(root.iterfind(".//{*}Deployment"))
    if len(collectors) != 1 or len(deployments) != 1:
        raise RuntimeError("expected one declared Coverlet collector and deployment")
    deployment = deployments[0].get("runDeploymentRoot", "")
    if not deployment or Path(deployment).is_absolute() or ".." in Path(deployment).parts:
        raise RuntimeError("invalid collector deployment root")
    base = (directory / deployment / "In").resolve()
    if not base.is_relative_to(directory.resolve()):
        raise RuntimeError("collector deployment escapes fresh results")
    reports = {}
    links = list(collectors[0].iterfind(".//{*}UriAttachment/{*}A"))
    for link in links:
        href = link.get("href", "")
        uri = urlsplit(href)
        relative = Path(unquote(uri.path))
        if uri.scheme or uri.netloc or uri.query or uri.fragment or relative.is_absolute() or ".." in relative.parts:
            raise RuntimeError("invalid collector attachment href")
        path = (base / relative).resolve()
        if not path.is_relative_to(base):
            raise RuntimeError("collector attachment escapes deployment")
        key = {"coverage.json": "json", "coverage.cobertura.xml": "cobertura"}.get(path.name)
        if key is None or key in reports:
            raise RuntimeError("unexpected or duplicate collector report")
        reports[key] = artifact(path)
    if len(links) != 2 or set(reports) != {"json", "cobertura"}:
        raise RuntimeError("both declared Coverlet JSON and Cobertura reports are required")
    return reports


def read_trx(path: Path, expected: Counter, lane: str, assembly: Path, started: float, finished: float) -> dict:
    root = ET.parse(path).getroot()
    summaries = list(root.iterfind(".//{*}ResultSummary"))
    times = root.find("{*}Times")
    if len(summaries) != 1 or summaries[0].get("outcome") != "Completed" or times is None:
        raise RuntimeError("TRX missing successful terminal summary/times")
    start = datetime.fromisoformat(times.attrib["start"]).timestamp()
    finish = datetime.fromisoformat(times.attrib["finish"]).timestamp()
    if start < started - 1 or finish < start or finish > finished + 1:
        raise RuntimeError("stale or inconsistent TRX time interval")
    definitions = {}
    for definition in root.iterfind(".//{*}TestDefinitions/{*}UnitTest"):
        ident = definition.get("id")
        method = definition.find("{*}TestMethod")
        if not ident or ident in definitions or method is None:
            raise RuntimeError("invalid/duplicate TRX test definition")
        if Path(method.attrib["codeBase"]).resolve() != assembly:
            raise RuntimeError("TRX belongs to a different test assembly")
        definitions[ident] = method.attrib["className"] + "." + method_name(method.attrib["name"])
    expected_methods = method_set(expected)
    rows = list(root.iterfind(".//{*}Results/{*}UnitTestResult"))
    if not rows:
        raise RuntimeError("empty terminal TRX")
    cases = Counter()
    observed_methods = set()
    outcomes = Counter()
    skips = []
    execution_ids = set()
    for row in rows:
        name, outcome = row.get("testName", ""), row.get("outcome", "")
        method = definitions.get(row.get("testId"))
        execution = row.get("executionId")
        if not method or method not in expected_methods or not (name == method or name.startswith(method + "(")):
            raise RuntimeError("TRX contains an unselected or inconsistent method identity")
        if not execution or execution in execution_ids:
            raise RuntimeError("TRX duplicate/missing execution identity")
        execution_ids.add(execution)
        observed_methods.add(method)
        cases[name] += 1
        outcomes[outcome] += 1
        if outcome != "Passed":
            if outcome != "NotExecuted" or (lane == "resource" and name not in OPTIONAL):
                raise RuntimeError(f"runtime row has forbidden outcome: {name} ({outcome})")
            reason = row.findtext("{*}Output/{*}ErrorInfo/{*}Message", "").strip()
            if not reason:
                raise RuntimeError("NotExecuted row has no retained skip reason")
            skips.append({"name": name, "reason": reason})
    if observed_methods != expected_methods:
        raise RuntimeError("TRX omitted a discovered method")
    if lane == "resource" and (cases != expected or {s["name"] for s in skips} != OPTIONAL):
        raise RuntimeError("resource row Counter or established optional skips disagree with discovery")
    counters = summaries[0].find("{*}Counters")
    if counters is None:
        raise RuntimeError("missing TRX terminal counters")
    passed = outcomes["Passed"]
    # SDK reports notExecuted=0 for skip rows; row outcomes remain authoritative.
    for key, value in (("total", len(rows)), ("executed", passed), ("passed", passed)):
        if int(counters.get(key, "-1")) != value:
            raise RuntimeError(f"TRX counter disagreement: {key}")
    if int(counters.get("notExecuted", "-1")) not in (0, len(skips)):
        raise RuntimeError("TRX skipped counter disagrees with terminal rows")
    for key in ("failed", "error", "timeout", "aborted", "inconclusive", "passedButRunAborted", "notRunnable", "disconnected", "warning", "inProgress", "pending"):
        if int(counters.get(key, "0")) != 0:
            raise RuntimeError(f"nonterminal/failing TRX counter: {key}")
    return {"trx": artifact(path), "counts": {"total": len(rows), "passed": passed, "skipped": len(skips), "failed": 0},
            "namedSkips": skips, "caseCounter": dict(sorted(cases.items())),
            "methods": sorted(observed_methods)}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--configuration", required=True)
    parser.add_argument("--results-directory", type=Path, required=True)
    parser.add_argument("--source-sha")
    parser.add_argument("--no-build", action="store_true")
    parser.add_argument("--coverage-settings", type=Path)
    args = parser.parse_args(argv)
    # This must precede even build, metadata, discovery and source-identity children.
    if os.environ.get("ARCANUM_RAPTOR_SCALE_PROBE") == "1":
        print("Ordinary runtime qualification refuses ARCANUM_RAPTOR_SCALE_PROBE=1; use the separate explicit offline numerical scale-probe command.", file=sys.stderr)
        return 2
    directory = args.results_directory.resolve()
    if directory.exists():
        print(f"results directory must be fresh: {directory}", file=sys.stderr)
        return 2
    receipt = {"schemaVersion": 1, "status": "failed", "sourceSha": None,
               "configuration": args.configuration, "project": str(args.project.resolve()),
               "testAssembly": None, "buildIdentity": None, "coverageSettings": None, "lanes": []}
    old_term = signal.getsignal(signal.SIGTERM)
    def interrupted(_number, _frame):
        raise KeyboardInterrupt()
    signal.signal(signal.SIGTERM, interrupted)
    try:
        validate_results(directory, args.project)
        source = source_state(directory)
        if args.source_sha is not None and args.source_sha != source["sha"]:
            raise RuntimeError("requested source SHA does not match checkout HEAD")
        receipt.update(sourceSha=source["sha"], sourceState=source)
        if not args.project.is_file() or not args.configuration.strip():
            raise RuntimeError("project and configuration are required")
        if args.coverage_settings:
            receipt["coverageSettings"] = artifact(args.coverage_settings.resolve())
        directory.mkdir(parents=True, exist_ok=False)
        project = str(args.project.resolve())
        common = [project, "--configuration", args.configuration, "--disable-build-servers", *MSBUILD_POLICY]
        if not args.no_build:
            run_child([args.dotnet, "build", *common], directory / "build.log", BUILD_TIMEOUT_SECONDS)
        output = run_child([args.dotnet, "msbuild", project, "-getProperty:TargetPath", "-p:Configuration=" + args.configuration, *MSBUILD_POLICY],
                           directory / "target-path.log", DISCOVERY_TIMEOUT_SECONDS).strip()
        assembly = Path(output)
        if not assembly.is_absolute() or assembly.suffix.lower() != ".dll":
            raise RuntimeError("metadata query did not return one absolute DLL TargetPath")
        assembly = assembly.resolve()
        identity = build_files(assembly)
        receipt.update(testAssembly=str(assembly), buildIdentity={"files": identity,
            "provenance": "caller-owned" if args.no_build else "runner-built",
            "symbols": {str(p): "adjacent" if p.with_suffix(".pdb").is_file() else "no-adjacent-pdb" for p in assembly.parent.glob("*.dll")}})
        def unchanged():
            if build_files(assembly) != identity or source_state(directory) != source:
                raise RuntimeError("source or built closure changed during runtime qualification")
            if args.coverage_settings and artifact(args.coverage_settings.resolve()) != receipt["coverageSettings"]:
                raise RuntimeError("coverage settings changed during runtime qualification")
        selections = {}
        for name, selected in (("base", BASE_FILTER), *LANES):
            output = run_child([args.dotnet, "test", *common, "--no-build", "--no-restore", "--list-tests", "--filter", selected, "--verbosity", "quiet"],
                               directory / ("discovery-" + name + ".log"), DISCOVERY_TIMEOUT_SECONDS)
            selections[name] = discovery(output)
            unchanged()
        universe, resource, remainder = (method_set(selections[k]) for k in ("base", "resource", "remainder"))
        if resource & remainder or resource | remainder != universe:
            raise RuntimeError("discovered methods are not a disjoint union of base runtime selection")
        if not all(m.startswith(RAPTOR) for m in resource) or not OPTIONAL <= resource:
            raise RuntimeError("resource discovery is not the unchanged Raptor class/opt-in contract")
        if any(m.startswith(RAPTOR) for m in remainder):
            raise RuntimeError("resource method appeared in remainder")
        receipt["discovery"] = {k: {"methods": sorted(method_set(v)), "caseCounter": dict(sorted(v.items()))} for k, v in selections.items()}
        for name, selected in LANES:
            lane_directory = directory / name
            lane_directory.mkdir()
            lane = {"name": name, "filter": selected, "exitCode": None, "joined": False,
                    "trx": None, "counts": None, "namedSkips": [], "caseCounter": {}}
            receipt["lanes"].append(lane)
            command = [args.dotnet, "test", *common, "--no-build", "--no-restore", "--filter", selected,
                       "--logger", "trx;LogFileName=runtime.trx", "--logger", "console;verbosity=normal",
                       "--results-directory", str(lane_directory), "--blame-hang-timeout", "15m", "--blame-hang-dump-type", "none"]
            if args.coverage_settings:
                command += ["--collect", "XPlat Code Coverage", "--settings", str(args.coverage_settings.resolve())]
            started = time.time()
            run_child(command, lane_directory / "runtime.log", LANE_TIMEOUT_SECONDS, lane)
            finished = time.time()
            unchanged()
            path = lane_directory / "runtime.trx"
            lane.update(read_trx(path, selections[name], name, assembly, started, finished))
            if args.coverage_settings:
                lane["coverage"] = coverage_artifacts(ET.parse(path).getroot(), lane_directory)
        receipt["caseCounter"] = dict(sorted(sum((Counter(l["caseCounter"]) for l in receipt["lanes"]), Counter()).items()))
        receipt["counts"] = {k: sum(l["counts"][k] for l in receipt["lanes"]) for k in ("total", "passed", "skipped", "failed")}
        unchanged()
        for lane in receipt["lanes"]:
            for recorded in [lane["trx"], *lane.get("coverage", {}).values()]:
                if artifact(Path(recorded["path"])) != recorded:
                    raise RuntimeError("previously validated runtime artifact changed")
        receipt["status"] = "passed"
        return 0
    except (Exception, KeyboardInterrupt) as error:
        receipt["error"] = f"{type(error).__name__}: {error}"
        print(receipt["error"], file=sys.stderr)
        return 1
    finally:
        signal.signal(signal.SIGTERM, old_term)
        if directory.is_dir():
            (directory / "runtime-summary.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    sys.exit(main())
