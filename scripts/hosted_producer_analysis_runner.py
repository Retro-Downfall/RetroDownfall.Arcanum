#!/usr/bin/env python3
"""Run hosted-producer source proofs with bounded parallelism and live progress."""

from __future__ import annotations

import argparse
from collections import Counter, OrderedDict
import ctypes
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import threading
import time
from typing import NamedTuple, Optional, Sequence, TextIO, Tuple
import xml.etree.ElementTree as ElementTree


PRODUCTION_FILTER = "Category=HostedProducerProductionAnalysis"

FIXTURE_FILTER = (
    "Category=HostedProducerAnalysis"
    "&Category!=HostedProducerProductionAnalysis"
)

ADDITIONAL_FILTER = "Category=HostedProducerAdditionalAnalysis"

RECEIPT_FILE = "hosted-producer-analysis-receipt.json"

ROOT_PROGRESS_VARIABLE = "ARCANUM_HOSTED_ANALYSIS_PROGRESS"

ROOT_WORKERS_VARIABLE = "ARCANUM_HOSTED_ANALYSIS_ROOT_WORKERS"

MAX_WORKERS = 16

MAX_ANALYSIS_SECONDS = 30 * 60

DISCOVERY_TIMEOUT_SECONDS = 2 * 60

RESULT_LINE = re.compile(
    r"^\s*(Passed|Failed|Skipped)\s+(.+?)(?:\s+\[[^\]]+\])?\s*$"
)

WINDOWS_JOB_OBJECT_EXTENDED_LIMIT_INFORMATION = 9

WINDOWS_JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000

WINDOWS_CREATE_SUSPENDED = 0x00000004

WINDOWS_THREAD_SUSPEND_RESUME = 0x0002

WINDOWS_THREAD_SNAPSHOT = 0x00000004

WINDOWS_INVALID_DWORD = 0xFFFFFFFF

_WINDOWS_JOB_HANDLES: dict[int, int] = {}

_WINDOWS_JOB_GATE = threading.Lock()


class DiscoveredMethod(NamedTuple):
    name: str
    case_count: int


class AnalysisShard(NamedTuple):
    index: int
    methods: Tuple[DiscoveredMethod, ...]
    case_count: int


class AnalysisRunResult(NamedTuple):
    exit_codes: Tuple[int, ...]
    completed_count: int
    passed_count: int
    expected_count: int
    log_path: Path


class TrxSummary(NamedTuple):
    completed_count: int
    passed_count: int
    nonpassing: Tuple[Tuple[str, str], ...]


def discover_methods(output: str) -> list[DiscoveredMethod]:
    """Return unique methods and their discovered theory-case counts."""

    found_header = False

    counts: OrderedDict[str, int] = OrderedDict()

    for raw_line in output.splitlines():
        if raw_line.strip() == "The following Tests are available:":
            found_header = True

            continue

        if not found_header or not raw_line.startswith("    "):
            continue

        display_name = raw_line.strip()

        if not display_name:
            continue

        method_name = display_name.split("(", 1)[0].strip()

        if "." not in method_name:
            continue

        counts[method_name] = counts.get(method_name, 0) + 1

    if not counts:
        raise ValueError("no hosted-producer analysis tests were discovered")

    return [DiscoveredMethod(name, count) for name, count in counts.items()]


def balance_methods(
    methods: Sequence[DiscoveredMethod],
    worker_count: int,
) -> list[AnalysisShard]:
    """Greedily balance discovered cases while keeping each theory method intact."""

    if worker_count < 1:
        raise ValueError("worker count must be positive")

    if not methods:
        raise ValueError("at least one discovered method is required")

    shard_count = min(worker_count, len(methods))

    assigned: list[list[DiscoveredMethod]] = [[] for _ in range(shard_count)]

    totals = [0] * shard_count

    for method in sorted(methods, key=lambda item: (-item.case_count, item.name)):
        target = min(range(shard_count), key=lambda index: (totals[index], index))

        assigned[target].append(method)

        totals[target] += method.case_count

    return [
        AnalysisShard(index=index, methods=tuple(shard), case_count=totals[index])
        for index, shard in enumerate(assigned)
    ]


def _method_map(
    methods: Sequence[DiscoveredMethod],
    label: str,
) -> dict[str, DiscoveredMethod]:
    mapped: dict[str, DiscoveredMethod] = {}

    for method in methods:
        if method.case_count < 1:
            raise ValueError(
                f"{label} method {method.name} has a non-positive case count"
            )

        if method.name in mapped:
            raise ValueError(f"duplicate {label} method: {method.name}")

        mapped[method.name] = method

    return mapped


def select_partition(
    production: Sequence[DiscoveredMethod],
    additional: Sequence[DiscoveredMethod],
    fixtures: Sequence[DiscoveredMethod],
    partition: str,
) -> Tuple[list[DiscoveredMethod], list[DiscoveredMethod]]:
    """Select one complete partition after validating the shared universe."""

    if partition not in {"full", "primary", "additional"}:
        raise ValueError(f"unknown hosted-producer analysis partition: {partition}")

    production_by_name = _method_map(production, "production")

    additional_by_name = _method_map(additional, "additional")

    fixture_by_name = _method_map(fixtures, "fixture")

    overlap = set(production_by_name).intersection(fixture_by_name)

    if overlap:
        raise ValueError(
            "methods selected by both production and fixture universes: "
            + ", ".join(sorted(overlap))
        )

    outside = set(additional_by_name).difference(production_by_name)

    if outside:
        raise ValueError(
            "additional methods outside the production universe: "
            + ", ".join(sorted(outside))
        )

    for name, method in additional_by_name.items():
        if production_by_name[name].case_count != method.case_count:
            raise ValueError(
                "additional method case count does not match production: "
                f"{name} ({method.case_count} != "
                f"{production_by_name[name].case_count})"
            )

    if not additional_by_name or len(additional_by_name) >= len(production_by_name):
        raise ValueError(
            "additional partition is empty or its production methods are not "
            "a nonempty proper subset of the production universe"
        )

    primary = [
        method
        for method in production
        if method.name not in additional_by_name
    ]

    secondary = [*additional, *fixtures]

    if not primary:
        raise ValueError("primary partition is empty")

    if not secondary:
        raise ValueError("additional partition is empty")

    if partition == "primary":
        return primary, []

    if partition == "additional":
        return list(additional), list(fixtures)

    return list(production), list(fixtures)


def plan_shards(
    production: Sequence[DiscoveredMethod],
    fixtures: Sequence[DiscoveredMethod],
    worker_count: int,
) -> list[AnalysisShard]:
    """Reserve production traversal capacity while balancing fixture processes."""

    if worker_count < 1:
        raise ValueError("worker count must be positive")

    if not production and not fixtures:
        raise ValueError("at least one discovered method is required")

    overlap = {
        method.name
        for method in production
    }.intersection(method.name for method in fixtures)

    if overlap:
        raise ValueError(
            "methods selected by both production and fixture lanes: "
            + ", ".join(sorted(overlap))
        )

    if worker_count == 1:
        methods = tuple([*production, *fixtures])

        return [
            AnalysisShard(
                index=0,
                methods=methods,
                case_count=sum(method.case_count for method in methods),
            )
        ]

    shards: list[AnalysisShard] = []

    if production:
        production_methods = tuple(production)

        shards.append(
            AnalysisShard(
                index=0,
                methods=production_methods,
                case_count=sum(
                    method.case_count
                    for method in production_methods
                ),
            )
        )

    if fixtures:
        fixture_workers = max(1, worker_count - (min(3, worker_count) if production else 0))

        for fixture_shard in balance_methods(fixtures, fixture_workers):
            shards.append(
                AnalysisShard(
                    index=len(shards),
                    methods=fixture_shard.methods,
                    case_count=fixture_shard.case_count,
                )
            )

    return shards


def shard_filter(shard: AnalysisShard) -> str:
    return "|".join(
        "FullyQualifiedName=" + method.name
        for method in shard.methods
    )


def write_shard_runsettings(
    results_directory: Path,
    shard: AnalysisShard,
) -> Path:
    path = results_directory / (
        f"hosted-producer-analysis-shard-{shard.index + 1:02d}.runsettings"
    )

    root = ElementTree.Element("RunSettings")

    configuration = ElementTree.SubElement(root, "RunConfiguration")

    ElementTree.SubElement(configuration, "TestCaseFilter").text = (
        shard_filter(shard)
    )

    ElementTree.SubElement(configuration, "TreatNoTestsAsError").text = "true"

    ElementTree.ElementTree(root).write(
        path,
        encoding="utf-8",
        xml_declaration=True,
    )

    return path


def default_worker_count() -> int:
    processors = os.cpu_count() or 1

    return min(MAX_WORKERS, max(1, processors - 2))


def parse_result_line(line: str) -> Optional[Tuple[str, str]]:
    match = RESULT_LINE.match(line)

    if match is None:
        return None

    return match.group(1), match.group(2)


def read_trx_summary(
    path: Path,
    expected_methods: Optional[Sequence[DiscoveredMethod]] = None,
) -> TrxSummary:
    try:
        root = ElementTree.parse(path).getroot()
    except ElementTree.ParseError as error:
        raise RuntimeError(f"cannot parse analysis TRX: {path}") from error

    results = list(root.iterfind(".//{*}UnitTestResult"))

    if not results:
        raise RuntimeError(f"analysis TRX contains no test results: {path}")

    if expected_methods is not None:
        observed: Counter[str] = Counter()

        for result in results:
            test_name = result.attrib.get("testName", "")

            matching = [
                method.name
                for method in expected_methods
                if test_name == method.name
                or test_name.startswith(method.name + "(")
            ]

            if len(matching) != 1:
                raise RuntimeError(
                    "analysis TRX contains an unselected test identity: "
                    + (test_name or "<unnamed>")
                )

            observed[matching[0]] += 1

        for method in expected_methods:
            if observed[method.name] != method.case_count:
                raise RuntimeError(
                    "analysis TRX reported "
                    f"{observed[method.name]} of {method.case_count} results "
                    f"for {method.name}"
                )

    nonpassing = tuple(
        (
            result.attrib.get("testName", "<unnamed>"),
            result.attrib.get("outcome", "<missing>"),
        )
        for result in results
        if result.attrib.get("outcome") != "Passed"
    )

    return TrxSummary(
        completed_count=len(results),
        passed_count=len(results) - len(nonpassing),
        nonpassing=nonpassing,
    )


def _entry(method: DiscoveredMethod, kind: Optional[str] = None) -> dict[str, object]:
    entry: dict[str, object] = {
        "name": method.name,
        "caseCount": method.case_count,
    }

    if kind is not None:
        entry["kind"] = kind

    return entry


def write_partition_receipt(
    results_directory: Path,
    source_sha: str,
    partition: str,
    production: Sequence[DiscoveredMethod],
    additional: Sequence[DiscoveredMethod],
    fixtures: Sequence[DiscoveredMethod],
    selected_production: Sequence[DiscoveredMethod],
    selected_fixtures: Sequence[DiscoveredMethod],
    shards: Sequence[AnalysisShard],
) -> Path:
    results_directory.mkdir(parents=True, exist_ok=True)

    selected = [
        *(_entry(method, "production") for method in selected_production),
        *(_entry(method, "fixture") for method in selected_fixtures),
    ]

    receipt = {
        "schemaVersion": 1,
        "sourceSha": source_sha,
        "partition": partition,
        "fullUniverse": [
            *(_entry(method, "production") for method in production),
            *(_entry(method, "fixture") for method in fixtures),
        ],
        "additionalProduction": [_entry(method) for method in additional],
        "selected": selected,
        "shards": [
            {
                "trxFile": (
                    "hosted-producer-analysis-"
                    f"shard-{shard.index + 1:02d}.trx"
                ),
                "methods": [_entry(method) for method in shard.methods],
            }
            for shard in shards
        ],
    }

    path = results_directory / RECEIPT_FILE

    path.write_text(
        json.dumps(receipt, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )

    return path


def _load_receipt(path: Path, expected_partition: str, source_sha: str) -> dict:
    receipt_path = path / RECEIPT_FILE

    try:
        receipt = json.loads(receipt_path.read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        raise RuntimeError(
            f"missing {expected_partition} analysis receipt: {receipt_path}"
        ) from error
    except (json.JSONDecodeError, UnicodeDecodeError) as error:
        raise RuntimeError(
            f"malformed {expected_partition} analysis receipt: {receipt_path}"
        ) from error

    if not isinstance(receipt, dict) or receipt.get("schemaVersion") != 1:
        raise RuntimeError(
            f"unsupported {expected_partition} analysis receipt schema"
        )

    if receipt.get("sourceSha") != source_sha:
        raise RuntimeError(
            f"{expected_partition} analysis receipt source SHA does not match "
            "the aggregate source SHA"
        )

    if receipt.get("partition") != expected_partition:
        raise RuntimeError(
            f"expected {expected_partition} analysis receipt, got "
            f"{receipt.get('partition')!r}"
        )

    return receipt


def _parse_receipt_entries(
    value: object,
    label: str,
    *,
    require_kind: bool,
) -> list[Tuple[str, int, Optional[str]]]:
    if not isinstance(value, list):
        raise RuntimeError(f"{label} must be a list")

    parsed: list[Tuple[str, int, Optional[str]]] = []

    names: set[str] = set()

    for item in value:
        if not isinstance(item, dict):
            raise RuntimeError(f"{label} contains a malformed method entry")

        name = item.get("name")

        case_count = item.get("caseCount")

        kind = item.get("kind")

        if not isinstance(name, str) or not name:
            raise RuntimeError(f"{label} contains an invalid method name")

        if not isinstance(case_count, int) or isinstance(case_count, bool) or case_count < 1:
            raise RuntimeError(f"{label} contains an invalid case count for {name}")

        if require_kind and kind not in {"production", "fixture"}:
            raise RuntimeError(f"{label} contains an invalid method kind for {name}")

        if not require_kind and kind is not None:
            raise RuntimeError(f"{label} contains an unexpected method kind for {name}")

        if name in names:
            raise RuntimeError(f"{label} contains duplicate method {name}")

        names.add(name)

        parsed.append((name, case_count, kind if isinstance(kind, str) else None))

    return parsed


def _validate_receipt(
    directory: Path,
    receipt: dict,
) -> Tuple[Counter[Tuple[str, int, str]], int, int]:
    partition = str(receipt["partition"])

    universe = _parse_receipt_entries(
        receipt.get("fullUniverse"),
        f"{partition} full universe",
        require_kind=True,
    )

    additional = _parse_receipt_entries(
        receipt.get("additionalProduction"),
        f"{partition} additional production",
        require_kind=False,
    )

    selected = _parse_receipt_entries(
        receipt.get("selected"),
        f"{partition} selected methods",
        require_kind=True,
    )

    production = {
        name: case_count
        for name, case_count, kind in universe
        if kind == "production"
    }

    fixtures = {
        name: case_count
        for name, case_count, kind in universe
        if kind == "fixture"
    }

    if set(production).intersection(fixtures):
        raise RuntimeError(f"{partition} full universe contains duplicate identities")

    additional_map = {name: count for name, count, _ in additional}

    outside = set(additional_map).difference(production)

    if outside:
        raise RuntimeError(
            f"{partition} additional production is outside the full universe"
        )

    if any(production[name] != count for name, count in additional_map.items()):
        raise RuntimeError(
            f"{partition} additional production case counts do not match"
        )

    if not additional_map or len(additional_map) >= len(production):
        raise RuntimeError(
            f"{partition} additional production is not a nonempty proper subset"
        )

    if partition == "primary":
        expected = Counter(
            (name, count, "production")
            for name, count in production.items()
            if name not in additional_map
        )
    else:
        expected = Counter(
            [
                *((name, count, "production") for name, count in additional_map.items()),
                *((name, count, "fixture") for name, count in fixtures.items()),
            ]
        )

    selected_counter = Counter(
        (name, count, str(kind))
        for name, count, kind in selected
    )

    if selected_counter != expected:
        raise RuntimeError(
            f"{partition} selected methods do not match the required partition"
        )

    shards = receipt.get("shards")

    if not isinstance(shards, list) or not shards:
        raise RuntimeError(f"{partition} receipt declares no analysis shards")

    selected_by_name = {
        name: (count, str(kind))
        for name, count, kind in selected
    }

    shard_methods: list[Tuple[str, int, str]] = []

    declared_trx: set[str] = set()

    passed = 0

    completed = 0

    for shard in shards:
        if not isinstance(shard, dict):
            raise RuntimeError(f"{partition} receipt contains a malformed shard")

        trx_file = shard.get("trxFile")

        if (
            not isinstance(trx_file, str)
            or not trx_file.endswith(".trx")
            or Path(trx_file).name != trx_file
            or trx_file in declared_trx
        ):
            raise RuntimeError(f"{partition} receipt contains an invalid TRX declaration")

        declared_trx.add(trx_file)

        methods = _parse_receipt_entries(
            shard.get("methods"),
            f"{partition} shard {trx_file}",
            require_kind=False,
        )

        if not methods:
            raise RuntimeError(f"{partition} shard {trx_file} selects no methods")

        expected_methods: list[DiscoveredMethod] = []

        for name, count, _ in methods:
            if name not in selected_by_name or selected_by_name[name][0] != count:
                raise RuntimeError(
                    f"{partition} shard {trx_file} contains an unselected method"
                )

            shard_methods.append((name, count, selected_by_name[name][1]))

            expected_methods.append(DiscoveredMethod(name, count))

        trx_path = directory / trx_file

        if not trx_path.is_file():
            raise RuntimeError(
                f"{partition} analysis evidence is missing TRX {trx_file}"
            )

        summary = read_trx_summary(trx_path, expected_methods)

        if summary.nonpassing:
            raise RuntimeError(
                f"{partition} analysis evidence contains tests that did not pass"
            )

        completed += summary.completed_count

        passed += summary.passed_count

    if Counter(shard_methods) != selected_counter:
        raise RuntimeError(
            f"{partition} shard declarations are duplicate, incomplete, or extra"
        )

    actual_trx = {
        path.relative_to(directory).as_posix()
        for path in directory.rglob("*.trx")
    }

    if actual_trx != declared_trx:
        raise RuntimeError(
            f"{partition} evidence contains missing or undeclared TRX files"
        )

    return selected_counter, completed, passed


def aggregate_partition_evidence(
    primary_directory: Path,
    additional_directory: Path,
    source_sha: str,
    summary_path: Path,
) -> dict:
    if not source_sha:
        raise RuntimeError("aggregate source SHA is required")

    primary_receipt = _load_receipt(primary_directory, "primary", source_sha)

    additional_receipt = _load_receipt(
        additional_directory,
        "additional",
        source_sha,
    )

    primary_universe = Counter(
        _parse_receipt_entries(
            primary_receipt.get("fullUniverse"),
            "primary full universe",
            require_kind=True,
        )
    )

    additional_universe = Counter(
        _parse_receipt_entries(
            additional_receipt.get("fullUniverse"),
            "additional full universe",
            require_kind=True,
        )
    )

    if primary_universe != additional_universe:
        raise RuntimeError("partition full discovery manifests do not agree")

    if Counter(
        _parse_receipt_entries(
            primary_receipt.get("additionalProduction"),
            "primary additional production",
            require_kind=False,
        )
    ) != Counter(
        _parse_receipt_entries(
            additional_receipt.get("additionalProduction"),
            "additional additional production",
            require_kind=False,
        )
    ):
        raise RuntimeError("partition additional discovery manifests do not agree")

    primary_selected, primary_completed, primary_passed = _validate_receipt(
        primary_directory,
        primary_receipt,
    )

    additional_selected, additional_completed, additional_passed = _validate_receipt(
        additional_directory,
        additional_receipt,
    )

    overlap = primary_selected & additional_selected

    if overlap:
        raise RuntimeError("partition selected method/case multisets overlap")

    selected_union = primary_selected + additional_selected

    if selected_union != primary_universe:
        raise RuntimeError("partition selected method/case union is incomplete or extra")

    total_cases = sum(
        case_count * multiplicity
        for (_, case_count, _), multiplicity in primary_universe.items()
    )

    completed = primary_completed + additional_completed

    passed = primary_passed + additional_passed

    if completed != total_cases or passed != total_cases:
        raise RuntimeError(
            "aggregate TRX evidence does not contain one passing result for every case"
        )

    def manifest_hashes(
        manifest: Counter[Tuple[str, int, str]],
    ) -> Tuple[str, str]:
        methods: list[str] = []

        cases: list[str] = []

        for (name, case_count, kind), multiplicity in sorted(manifest.items()):
            for occurrence in range(multiplicity):
                methods.append(f"{kind}\t{name}\t{occurrence}\n")

                cases.extend(
                    f"{kind}\t{name}\t{occurrence}\t{case_index}\n"
                    for case_index in range(case_count)
                )

        return (
            hashlib.sha256("".join(methods).encode("utf-8")).hexdigest(),
            hashlib.sha256("".join(cases).encode("utf-8")).hexdigest(),
        )

    full_method_hash, full_case_hash = manifest_hashes(primary_universe)

    primary_method_hash, primary_case_hash = manifest_hashes(primary_selected)

    additional_method_hash, additional_case_hash = manifest_hashes(
        additional_selected
    )

    summary = {
        "schemaVersion": 2,
        "aggregateJob": "Hosted producer source analysis",
        "partitionAccounting": {
            "status": "PASS",
            "sourceSha": source_sha,
            "disjoint": True,
            "unionComplete": True,
            "fullUniverse": {
                "discoveredCases": total_cases,
                "passedCases": passed,
                "failedCases": 0,
                "skippedCases": 0,
                "methodsSha256": full_method_hash,
                "casesSha256": full_case_hash,
            },
            "partitions": [
                {
                    "name": "primary",
                    "job": "Hosted producer primary analysis",
                    "selectedCases": primary_completed,
                    "methodsSha256": primary_method_hash,
                    "casesSha256": primary_case_hash,
                },
                {
                    "name": "additional",
                    "job": "Hosted producer additional analysis and fixtures",
                    "selectedCases": additional_completed,
                    "methodsSha256": additional_method_hash,
                    "casesSha256": additional_case_hash,
                },
            ],
        },
    }

    summary_path.parent.mkdir(parents=True, exist_ok=True)

    summary_path.write_text(
        json.dumps(summary, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )

    return summary


def shard_environment(
    results_directory: Path,
    production: bool,
    root_workers: int = 1,
) -> dict[str, str]:
    environment = os.environ.copy()

    if production:
        environment[ROOT_WORKERS_VARIABLE] = str(root_workers)

        environment[ROOT_PROGRESS_VARIABLE] = str(
            (results_directory / "hosted-producer-roots.log").resolve()
        )
    else:
        environment.pop(ROOT_PROGRESS_VARIABLE, None)

        environment.pop(ROOT_WORKERS_VARIABLE, None)

    return environment


def read_progress_records(path: Path, offset: int) -> Tuple[list[str], int]:
    if not path.exists():
        return [], offset

    records: list[str] = []

    with path.open("r", encoding="utf-8") as stream:
        stream.seek(offset)

        while True:
            record_start = stream.tell()

            line = stream.readline()

            if not line:
                return records, stream.tell()

            if not line.endswith("\n"):
                return records, record_start

            records.append(line.rstrip("\r\n"))


def process_creation_flags() -> int:
    if os.name != "nt":
        return 0

    return WINDOWS_CREATE_SUSPENDED


def _windows_kernel32() -> ctypes.LibraryLoader:
    return ctypes.WinDLL("kernel32", use_last_error=True)


def _raise_last_windows_error() -> None:
    raise ctypes.WinError(ctypes.get_last_error())


def _create_windows_kill_job(process: subprocess.Popen[str]) -> int:
    from ctypes import wintypes

    class IoCounters(ctypes.Structure):
        _fields_ = [
            ("ReadOperationCount", ctypes.c_ulonglong),
            ("WriteOperationCount", ctypes.c_ulonglong),
            ("OtherOperationCount", ctypes.c_ulonglong),
            ("ReadTransferCount", ctypes.c_ulonglong),
            ("WriteTransferCount", ctypes.c_ulonglong),
            ("OtherTransferCount", ctypes.c_ulonglong),
        ]

    class BasicLimitInformation(ctypes.Structure):
        _fields_ = [
            ("PerProcessUserTimeLimit", ctypes.c_longlong),
            ("PerJobUserTimeLimit", ctypes.c_longlong),
            ("LimitFlags", wintypes.DWORD),
            ("MinimumWorkingSetSize", ctypes.c_size_t),
            ("MaximumWorkingSetSize", ctypes.c_size_t),
            ("ActiveProcessLimit", wintypes.DWORD),
            ("Affinity", ctypes.c_size_t),
            ("PriorityClass", wintypes.DWORD),
            ("SchedulingClass", wintypes.DWORD),
        ]

    class ExtendedLimitInformation(ctypes.Structure):
        _fields_ = [
            ("BasicLimitInformation", BasicLimitInformation),
            ("IoInfo", IoCounters),
            ("ProcessMemoryLimit", ctypes.c_size_t),
            ("JobMemoryLimit", ctypes.c_size_t),
            ("PeakProcessMemoryUsed", ctypes.c_size_t),
            ("PeakJobMemoryUsed", ctypes.c_size_t),
        ]

    kernel32 = _windows_kernel32()

    kernel32.CreateJobObjectW.argtypes = [
        ctypes.c_void_p,
        wintypes.LPCWSTR,
    ]

    kernel32.CreateJobObjectW.restype = wintypes.HANDLE

    kernel32.SetInformationJobObject.argtypes = [
        wintypes.HANDLE,
        ctypes.c_int,
        ctypes.c_void_p,
        wintypes.DWORD,
    ]

    kernel32.SetInformationJobObject.restype = wintypes.BOOL

    kernel32.AssignProcessToJobObject.argtypes = [
        wintypes.HANDLE,
        wintypes.HANDLE,
    ]

    kernel32.AssignProcessToJobObject.restype = wintypes.BOOL

    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]

    kernel32.CloseHandle.restype = wintypes.BOOL

    job = kernel32.CreateJobObjectW(None, None)

    if not job:
        _raise_last_windows_error()

    information = ExtendedLimitInformation()

    information.BasicLimitInformation.LimitFlags = (
        WINDOWS_JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
    )

    if not kernel32.SetInformationJobObject(
        job,
        WINDOWS_JOB_OBJECT_EXTENDED_LIMIT_INFORMATION,
        ctypes.byref(information),
        ctypes.sizeof(information),
    ):
        error = ctypes.get_last_error()

        kernel32.CloseHandle(job)

        raise ctypes.WinError(error)

    process_handle = wintypes.HANDLE(int(process._handle))

    if not kernel32.AssignProcessToJobObject(job, process_handle):
        error = ctypes.get_last_error()

        kernel32.CloseHandle(job)

        raise ctypes.WinError(error)

    return int(job)


def _resume_windows_process(process: subprocess.Popen[str]) -> None:
    from ctypes import wintypes

    class ThreadEntry(ctypes.Structure):
        _fields_ = [
            ("dwSize", wintypes.DWORD),
            ("cntUsage", wintypes.DWORD),
            ("th32ThreadID", wintypes.DWORD),
            ("th32OwnerProcessID", wintypes.DWORD),
            ("tpBasePri", wintypes.LONG),
            ("tpDeltaPri", wintypes.LONG),
            ("dwFlags", wintypes.DWORD),
        ]

    kernel32 = _windows_kernel32()

    kernel32.CreateToolhelp32Snapshot.argtypes = [
        wintypes.DWORD,
        wintypes.DWORD,
    ]

    kernel32.CreateToolhelp32Snapshot.restype = wintypes.HANDLE

    kernel32.Thread32First.argtypes = [
        wintypes.HANDLE,
        ctypes.POINTER(ThreadEntry),
    ]

    kernel32.Thread32First.restype = wintypes.BOOL

    kernel32.Thread32Next.argtypes = [
        wintypes.HANDLE,
        ctypes.POINTER(ThreadEntry),
    ]

    kernel32.Thread32Next.restype = wintypes.BOOL

    kernel32.OpenThread.argtypes = [
        wintypes.DWORD,
        wintypes.BOOL,
        wintypes.DWORD,
    ]

    kernel32.OpenThread.restype = wintypes.HANDLE

    kernel32.ResumeThread.argtypes = [wintypes.HANDLE]

    kernel32.ResumeThread.restype = wintypes.DWORD

    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]

    kernel32.CloseHandle.restype = wintypes.BOOL

    snapshot = kernel32.CreateToolhelp32Snapshot(
        WINDOWS_THREAD_SNAPSHOT,
        0,
    )

    if snapshot == wintypes.HANDLE(-1).value:
        _raise_last_windows_error()

    resumed = False

    try:
        entry = ThreadEntry()

        entry.dwSize = ctypes.sizeof(entry)

        current = kernel32.Thread32First(snapshot, ctypes.byref(entry))

        while current:
            if entry.th32OwnerProcessID == process.pid:
                thread = kernel32.OpenThread(
                    WINDOWS_THREAD_SUSPEND_RESUME,
                    False,
                    entry.th32ThreadID,
                )

                if thread:
                    try:
                        if kernel32.ResumeThread(thread) != WINDOWS_INVALID_DWORD:
                            resumed = True
                    finally:
                        kernel32.CloseHandle(thread)

            current = kernel32.Thread32Next(snapshot, ctypes.byref(entry))
    finally:
        kernel32.CloseHandle(snapshot)

    if not resumed:
        raise RuntimeError(
            f"could not resume owned Windows process {process.pid}"
        )


def _close_windows_job(handle: int) -> None:
    from ctypes import wintypes

    kernel32 = _windows_kernel32()

    kernel32.CloseHandle.argtypes = [wintypes.HANDLE]

    kernel32.CloseHandle.restype = wintypes.BOOL

    if not kernel32.CloseHandle(wintypes.HANDLE(handle)):
        _raise_last_windows_error()


def attach_process_tree(process: subprocess.Popen[str]) -> None:
    if os.name != "nt":
        return

    handle = _create_windows_kill_job(process)

    with _WINDOWS_JOB_GATE:
        if process.pid in _WINDOWS_JOB_HANDLES:
            _close_windows_job(handle)

            raise RuntimeError(
                f"Windows process {process.pid} already has an owned job"
            )

        _WINDOWS_JOB_HANDLES[process.pid] = handle

    try:
        _resume_windows_process(process)
    except BaseException:
        release_process_tree(process)

        raise


def release_process_tree(process: subprocess.Popen[str]) -> None:
    if os.name != "nt":
        return

    with _WINDOWS_JOB_GATE:
        handle = _WINDOWS_JOB_HANDLES.pop(process.pid, None)

    if handle is not None:
        _close_windows_job(handle)


def _warn_cleanup_error(target: object, error: BaseException) -> None:
    print(
        f"hosted-producer analysis cleanup warning ({target}): {error}",
        file=sys.stderr,
    )


def _signal_parent(process: subprocess.Popen[str], *, force: bool) -> None:
    if process.poll() is not None:
        return

    try:
        if force:
            process.kill()
        else:
            process.terminate()
    except ProcessLookupError:
        pass
    except OSError as error:
        _warn_cleanup_error(process.pid, error)


def terminate_processes(
    processes: Sequence[subprocess.Popen[str]],
) -> None:
    live = [process for process in processes if process.poll() is None]

    if os.name == "nt":
        for process in processes:
            try:
                release_process_tree(process)
            except OSError as error:
                _warn_cleanup_error(process.pid, error)

    for process in processes:
        try:
            if os.name == "posix":
                os.killpg(process.pid, signal.SIGTERM)
            elif os.name == "nt":
                completed = subprocess.run(
                    [
                        "taskkill",
                        "/PID",
                        str(process.pid),
                        "/T",
                        "/F",
                    ],
                    stdout=subprocess.DEVNULL,
                    stderr=subprocess.DEVNULL,
                    check=False,
                    timeout=3,
                )

                if completed.returncode != 0 and process.poll() is None:
                    _signal_parent(process, force=False)
            else:
                if process.poll() is None:
                    _signal_parent(process, force=False)
        except ProcessLookupError:
            _signal_parent(process, force=False)
        except (OSError, subprocess.TimeoutExpired) as error:
            _warn_cleanup_error(process.pid, error)

            _signal_parent(process, force=False)

            continue

    terminate_deadline = time.monotonic() + 3

    for process in live:
        try:
            process.wait(
                timeout=max(0.001, terminate_deadline - time.monotonic())
            )
        except subprocess.TimeoutExpired:
            continue
        except OSError as error:
            _warn_cleanup_error(process.pid, error)

    if os.name == "posix":
        for process in processes:
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                continue
            except OSError as error:
                _warn_cleanup_error(process.pid, error)

                _signal_parent(process, force=True)
    else:
        for process in live:
            if process.poll() is None:
                _signal_parent(process, force=True)

    for process in live:
        try:
            process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            continue
        except OSError as error:
            _warn_cleanup_error(process.pid, error)


def _cleanup_after_failure(processes: Sequence[subprocess.Popen[str]]) -> None:
    try:
        terminate_processes(processes)
    except (OSError, RuntimeError, subprocess.TimeoutExpired) as error:
        # Cleanup is secondary: retain the deadline, interruption, or test error.
        _warn_cleanup_error("process trees", error)


def _release_after_run(processes: Sequence[subprocess.Popen[str]]) -> None:
    preserving_error = sys.exc_info()[0] is not None

    release_error: Optional[OSError] = None

    for process in processes:
        try:
            release_process_tree(process)
        except OSError as error:
            _warn_cleanup_error(process.pid, error)

            if release_error is None:
                release_error = error

    if release_error is not None and not preserving_error:
        raise release_error


def _raise_keyboard_interrupt(
    _signal_number: int,
    _frame: object,
) -> None:
    raise KeyboardInterrupt


def require_complete_success(result: AnalysisRunResult) -> int:
    for exit_code in result.exit_codes:
        if exit_code != 0:
            return exit_code

    if result.completed_count != result.expected_count:
        raise RuntimeError(
            "hosted-producer analysis reported "
            f"{result.completed_count} of {result.expected_count} discovered test completions"
        )

    if result.passed_count != result.expected_count:
        raise RuntimeError(
            "hosted-producer analysis completed all mandatory proofs, but "
            f"only {result.passed_count} passed"
        )

    return 0


def _discover(
    dotnet: str,
    project: Path,
    filter_expression: str,
    configuration: Optional[str],
    timeout_seconds: float,
) -> list[DiscoveredMethod]:
    command = [
        dotnet,
        "test",
        str(project),
        "--no-build",
        "--list-tests",
        "--filter",
        filter_expression,
        "--verbosity",
        "quiet",
    ]

    if configuration is not None:
        command.extend(["--configuration", configuration])

    process = subprocess.Popen(
        command,
        stdout=subprocess.PIPE,
        stderr=subprocess.STDOUT,
        text=True,
        start_new_session=os.name == "posix",
        creationflags=process_creation_flags(),
    )

    try:
        attach_process_tree(process)
    except BaseException:
        _cleanup_after_failure([process])

        raise

    try:
        output, _ = process.communicate(timeout=timeout_seconds)
    except subprocess.TimeoutExpired as error:
        _cleanup_after_failure([process])

        raise RuntimeError(
            "hosted-producer analysis discovery exceeded "
            f"{timeout_seconds:.0f} seconds"
        ) from error
    except BaseException:
        _cleanup_after_failure([process])

        raise

    finally:
        if process.poll() is not None:
            _release_after_run([process])

    if process.returncode != 0:
        _cleanup_after_failure([process])

        sys.stderr.write(output)

        raise RuntimeError(
            f"hosted-producer analysis discovery failed with exit code {process.returncode}"
        )

    return discover_methods(output)


def _root_progress_reader(
    path: Path,
    combined_log: TextIO,
    lock: threading.Lock,
    stop: threading.Event,
) -> None:
    offset = 0

    while True:
        records, offset = read_progress_records(path, offset)

        for record in records:
            with lock:
                combined_log.write(f"[root] {record}\n")

                combined_log.flush()

                print(f"[analysis root] {record}", flush=True)

        if stop.is_set():
            return

        stop.wait(0.25)


def _reader(
    shard: AnalysisShard,
    stream: TextIO,
    shard_log: TextIO,
    combined_log: TextIO,
    lock: threading.Lock,
    progress: list[int],
    expected_count: int,
) -> None:
    for line in stream:
        terminal = parse_result_line(line)

        with lock:
            shard_log.write(line)

            shard_log.flush()

            combined_log.write(f"[shard {shard.index + 1:02d}] {line}")

            combined_log.flush()

            if terminal is not None:
                progress[0] += 1

                outcome, name = terminal

                print(
                    f"[analysis {progress[0]}/{expected_count}] {outcome} {name}",
                    flush=True,
                )


def run_shards(
    dotnet: str,
    project: Path,
    results_directory: Path,
    shards: Sequence[AnalysisShard],
    production_shard_index: Optional[int] = None,
    timeout_seconds: float = MAX_ANALYSIS_SECONDS,
    configuration: Optional[str] = None,
    worker_count: int = 1,
    deadline: Optional[float] = None,
) -> AnalysisRunResult:
    results_directory.mkdir(parents=True, exist_ok=True)

    combined_path = results_directory / "hosted-producer-analysis.log"

    expected_count = sum(shard.case_count for shard in shards)

    lock = threading.Lock()

    progress = [0]

    processes: list[subprocess.Popen[str]] = []

    readers: list[threading.Thread] = []

    shard_logs: list[TextIO] = []

    trx_paths: list[Path] = []

    root_progress_stop = threading.Event()

    root_progress_reader: Optional[threading.Thread] = None

    if deadline is None:
        deadline = time.monotonic() + timeout_seconds

    production_workers = min(3, worker_count)

    active: list[Tuple[subprocess.Popen[str], int]] = []

    occupied_slots = 0

    exit_code_list: list[int] = []

    def remaining_time() -> float:
        remaining = deadline - time.monotonic()

        if remaining <= 0:
            raise RuntimeError(
                "hosted-producer analysis exceeded its "
                f"{timeout_seconds:.0f}-second deadline"
            )

        return remaining

    def finish_next() -> int:
        process, slots = active.pop(0)

        try:
            exit_code_list.append(process.wait(timeout=remaining_time()))
        except subprocess.TimeoutExpired as error:
            raise RuntimeError(
                "hosted-producer analysis exceeded its "
                f"{timeout_seconds:.0f}-second deadline"
            ) from error

        return slots

    with combined_path.open("w", encoding="utf-8") as combined_log:
        try:
            for shard in shards:
                production = shard.index == production_shard_index

                slots = production_workers if production else 1

                while occupied_slots + slots > worker_count:
                    occupied_slots -= finish_next()

                remaining_time()

                if production and root_progress_reader is None:
                    root_progress_path = (
                        results_directory / "hosted-producer-roots.log"
                    )

                    root_progress_path.write_text("", encoding="utf-8")

                    root_progress_reader = threading.Thread(
                        target=_root_progress_reader,
                        args=(
                            root_progress_path,
                            combined_log,
                            lock,
                            root_progress_stop,
                        ),
                        daemon=True,
                    )

                    root_progress_reader.start()

                shard_log_path = results_directory / (
                    f"hosted-producer-analysis-shard-{shard.index + 1:02d}.log"
                )

                shard_log = shard_log_path.open("w", encoding="utf-8")

                shard_logs.append(shard_log)

                command = [
                    dotnet,
                    "test",
                    str(project),
                    "--no-build",
                    "--settings",
                    str(write_shard_runsettings(results_directory, shard)),
                    "--logger",
                    (
                        "trx;LogFileName=hosted-producer-analysis-"
                        f"shard-{shard.index + 1:02d}.trx"
                    ),
                    "--logger",
                    "console;verbosity=normal",
                    "--results-directory",
                    str(results_directory),
                ]

                if configuration is not None:
                    command.extend(["--configuration", configuration])

                trx_path = (
                    results_directory
                    / "hosted-producer-analysis-"
                    f"shard-{shard.index + 1:02d}.trx"
                )

                trx_path.unlink(missing_ok=True)

                trx_paths.append(trx_path)

                process = subprocess.Popen(
                    command,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.STDOUT,
                    text=True,
                    bufsize=1,
                    env=shard_environment(
                        results_directory, production, production_workers
                    ),
                    start_new_session=os.name == "posix",
                    creationflags=process_creation_flags(),
                )

                processes.append(process)

                active.append((process, slots))

                occupied_slots += slots

                attach_process_tree(process)

                assert process.stdout is not None

                reader = threading.Thread(
                    target=_reader,
                    args=(
                        shard,
                        process.stdout,
                        shard_log,
                        combined_log,
                        lock,
                        progress,
                        expected_count,
                    ),
                    daemon=True,
                )

                readers.append(reader)

                reader.start()

            while active:
                occupied_slots -= finish_next()

            exit_codes = tuple(exit_code_list)

            for reader in readers:
                reader.join(timeout=max(0, deadline - time.monotonic()))

                if reader.is_alive():
                    raise RuntimeError(
                        "hosted-producer analysis output reader did not finish "
                        "before the deadline"
                    )

            root_progress_stop.set()

            if root_progress_reader is not None:
                root_progress_reader.join(
                    timeout=max(0, deadline - time.monotonic())
                )

                if root_progress_reader.is_alive():
                    raise RuntimeError(
                        "hosted-producer root progress reader did not finish "
                        "before the deadline"
                    )

        except BaseException:
            _cleanup_after_failure(processes)

            raise

        finally:
            root_progress_stop.set()

            if root_progress_reader is not None:
                root_progress_reader.join(timeout=2)

            _release_after_run(processes)

            for shard_log in shard_logs:
                shard_log.close()

    summaries: list[TrxSummary] = []

    for shard, trx_path, exit_code in zip(shards, trx_paths, exit_codes):
        if not trx_path.exists():
            if exit_code != 0:
                summaries.append(TrxSummary(0, 0, ()))

                continue

            raise RuntimeError(
                f"analysis shard {shard.index + 1} produced no TRX evidence"
            )

        summary = read_trx_summary(trx_path, shard.methods)

        if exit_code == 0 and summary.completed_count != shard.case_count:
            raise RuntimeError(
                f"analysis shard {shard.index + 1} reported "
                f"{summary.completed_count} of {shard.case_count} "
                "discovered test results"
            )

        summaries.append(summary)

    return AnalysisRunResult(
        exit_codes=exit_codes,
        completed_count=sum(
            summary.completed_count
            for summary in summaries
        ),
        passed_count=sum(
            summary.passed_count
            for summary in summaries
        ),
        expected_count=expected_count,
        log_path=combined_path,
    )


def _parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Run hosted-producer source analysis with bounded parallelism."
    )

    parser.add_argument("--dotnet", default="dotnet")

    parser.add_argument("--project", type=Path)

    parser.add_argument("--results-directory", type=Path)

    parser.add_argument("--jobs", type=int)

    parser.add_argument("--timeout-seconds", type=int)

    parser.add_argument("--configuration")

    parser.add_argument(
        "--partition",
        choices=("full", "primary", "additional"),
        default="full",
    )

    parser.add_argument("--source-sha")

    parser.add_argument("--aggregate-primary-results", type=Path)

    parser.add_argument("--aggregate-additional-results", type=Path)

    parser.add_argument("--aggregate-summary", type=Path)

    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    arguments = _parser().parse_args(argv)

    aggregate_arguments = (
        arguments.aggregate_primary_results,
        arguments.aggregate_additional_results,
        arguments.aggregate_summary,
    )

    if any(argument is not None for argument in aggregate_arguments):
        if not all(argument is not None for argument in aggregate_arguments):
            print(
                "all aggregate result and summary paths are required",
                file=sys.stderr,
            )

            return 2

        if not arguments.source_sha:
            print("aggregate source SHA is required", file=sys.stderr)

            return 2

        try:
            summary = aggregate_partition_evidence(
                arguments.aggregate_primary_results,
                arguments.aggregate_additional_results,
                arguments.source_sha,
                arguments.aggregate_summary,
            )

            print(
                "Hosted-producer source analysis authority passed: "
                f"{summary['partitionAccounting']['fullUniverse']['passedCases']}/"
                f"{summary['partitionAccounting']['fullUniverse']['discoveredCases']}. "
                f"Summary: {arguments.aggregate_summary}",
                flush=True,
            )

            return 0
        except (OSError, RuntimeError, ValueError) as error:
            print(f"hosted-producer analysis: {error}", file=sys.stderr)

            return 1

    if arguments.project is None or arguments.results_directory is None:
        print(
            "--project and --results-directory are required for analysis execution",
            file=sys.stderr,
        )

        return 2

    if arguments.partition != "full" and not arguments.source_sha:
        print(
            "--source-sha is required for partitioned analysis",
            file=sys.stderr,
        )

        return 2

    configured_jobs = arguments.jobs

    if configured_jobs is None:
        environment_jobs = os.environ.get("ARCANUM_ANALYSIS_JOBS")

        try:
            configured_jobs = (
                int(environment_jobs)
                if environment_jobs is not None
                else default_worker_count()
            )
        except ValueError:
            print(
                "ARCANUM_ANALYSIS_JOBS must be an integer",
                file=sys.stderr,
            )

            return 2

    if configured_jobs < 1 or configured_jobs > MAX_WORKERS:
        print(
            f"analysis worker count must be between 1 and {MAX_WORKERS}",
            file=sys.stderr,
        )

        return 2

    configured_timeout = arguments.timeout_seconds

    if configured_timeout is None:
        environment_timeout = os.environ.get(
            "ARCANUM_ANALYSIS_TIMEOUT_SECONDS"
        )

        try:
            configured_timeout = (
                int(environment_timeout)
                if environment_timeout is not None
                else MAX_ANALYSIS_SECONDS
            )
        except ValueError:
            print(
                "ARCANUM_ANALYSIS_TIMEOUT_SECONDS must be an integer",
                file=sys.stderr,
            )

            return 2

    if configured_timeout < 1 or configured_timeout > MAX_ANALYSIS_SECONDS:
        print(
            "analysis timeout must be between 1 and "
            f"{MAX_ANALYSIS_SECONDS} seconds",
            file=sys.stderr,
        )

        return 2

    try:
        deadline = time.monotonic() + configured_timeout

        def discovery_timeout() -> float:
            remaining = deadline - time.monotonic()

            if remaining <= 0:
                raise RuntimeError(
                    "hosted-producer analysis exceeded its "
                    f"{configured_timeout}-second deadline"
                )

            return min(DISCOVERY_TIMEOUT_SECONDS, remaining)

        production = _discover(
            arguments.dotnet,
            arguments.project,
            PRODUCTION_FILTER,
            arguments.configuration,
            discovery_timeout(),
        )

        additional = _discover(
            arguments.dotnet,
            arguments.project,
            ADDITIONAL_FILTER,
            arguments.configuration,
            discovery_timeout(),
        )

        fixtures = _discover(
            arguments.dotnet,
            arguments.project,
            FIXTURE_FILTER,
            arguments.configuration,
            discovery_timeout(),
        )

        selected_production, selected_fixtures = select_partition(
            production,
            additional,
            fixtures,
            arguments.partition,
        )

        shards = plan_shards(
            selected_production,
            selected_fixtures,
            configured_jobs,
        )

        expected_count = sum(
            method.case_count
            for method in [*selected_production, *selected_fixtures]
        )

        if arguments.partition != "full":
            write_partition_receipt(
                arguments.results_directory,
                arguments.source_sha,
                arguments.partition,
                production,
                additional,
                fixtures,
                selected_production,
                selected_fixtures,
                shards,
            )

        print(
            "Hosted-producer analysis: "
            f"{arguments.partition} partition; "
            f"{expected_count} tests across {len(shards)} shards "
            f"within {configured_jobs} worker slots; "
            f"{sum(method.case_count for method in selected_production)} production tests "
            f"share one process with {min(3, configured_jobs)} traversal workers.",
            flush=True,
        )

        result = run_shards(
            arguments.dotnet,
            arguments.project,
            arguments.results_directory,
            shards,
            production_shard_index=0 if selected_production else None,
            timeout_seconds=max(0.001, deadline - time.monotonic()),
            configuration=arguments.configuration,
            worker_count=configured_jobs,
            deadline=deadline,
        )

        exit_code = require_complete_success(result)

        if exit_code != 0:
            print(
                f"Hosted-producer analysis failed; see {result.log_path}",
                file=sys.stderr,
            )

            return exit_code

        print(
            "Hosted-producer analysis passed: "
            f"{result.passed_count}/{result.expected_count}. "
            f"Full log: {result.log_path}",
            flush=True,
        )

        return 0

    except KeyboardInterrupt:
        print("hosted-producer analysis interrupted", file=sys.stderr)

        return 130

    except (OSError, RuntimeError, ValueError) as error:
        print(f"hosted-producer analysis: {error}", file=sys.stderr)

        return 1


if __name__ == "__main__":
    if os.name == "posix":
        signal.signal(signal.SIGTERM, _raise_keyboard_interrupt)

    raise SystemExit(main())
