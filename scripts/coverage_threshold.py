#!/usr/bin/env python3
"""Enforce tiered coverage thresholds from a Cobertura XML report."""

from __future__ import annotations

import math
import os
import sys
import xml.etree.ElementTree as ET

DEFAULT_LINE_TARGET = 80.0

DEFAULT_BRANCH_TARGET = 70.0

SECURITY_BRANCH_TARGET = 100.0

SECURITY_TYPES = {
    "ApiKeyEndpointFilter",
    "ApiKeyDigestCache",
    "DataProtectionSecretStore",
    "GrimoireKeyDerivation",
    "McpSecurityLimits",
    "TrustedMcpWorkspaceStore",
    "SandboxedFileIo",
    "SecureFileReader",
    "IdentityOwnedFileSystemCleanup",
    "SanctumGuard",
    "OutboundUrlGuard",
    "HostProcessToolPolicy",
    "IdempotencyClaimStore",
    "BudgetReservationService",
    "WardGate",
}


def pct(covered: float, total: float) -> float:
    if total <= 0:
        return 100.0
    return (covered / total) * 100.0


def declaring_type_name(name: str) -> str:
    """Fold a Cobertura class name onto the short name of its declaring type.

    Coverlet keeps async/iterator state machines as nested classes, e.g.
    ``Namespace.OutboundUrlGuard/<EgressConnectCallbackAsync>d__17``. Matching on the
    substring after the last '.' would yield ``OutboundUrlGuard/<...>d__17`` and skip
    every async body, so strip the nested suffix before stripping the namespace.
    """
    outer = name.split("/", 1)[0]

    return outer.rsplit(".", 1)[-1]


def read_target(name: str, default: float) -> float:
    raw = os.environ.get(name)

    if raw is None or raw.strip() == "":
        return default

    try:
        value = float(raw)
    except ValueError as exc:
        raise ValueError(f"{name} must be a number from 0 through 100") from exc

    if not math.isfinite(value) or value < 0.0 or value > 100.0:
        raise ValueError(f"{name} must be a number from 0 through 100")

    return value


def main(argv: list[str] | None = None) -> int:
    args = argv if argv is not None else sys.argv[1:]

    if len(args) != 1:
        print("usage: coverage_threshold.py <coverage.cobertura.xml>", file=sys.stderr)
        return 2

    try:
        line_target = read_target(
            "COVERAGE_LINE_TARGET",
            DEFAULT_LINE_TARGET,
        )
        branch_target = read_target(
            "COVERAGE_BRANCH_TARGET",
            DEFAULT_BRANCH_TARGET,
        )
    except ValueError as exc:
        print(str(exc), file=sys.stderr)
        return 2

    root = ET.parse(args[0]).getroot()

    line_rate = float(root.attrib.get("line-rate", "0")) * 100.0

    branch_rate = float(root.attrib.get("branch-rate", "0")) * 100.0

    failures: list[str] = []
    seen_security_types: set[str] = set()

    if line_rate < line_target:
        failures.append(f"line coverage {line_rate:.2f}% < {line_target:g}%")

    if branch_rate < branch_target:
        failures.append(f"branch coverage {branch_rate:.2f}% < {branch_target:g}%")

    # One branch tally per security type, aggregated over the declaring class *and*
    # every compiler-generated state machine nested inside it. Keyed by
    # (source file, line number) so a line reported by both the synchronous shell and
    # its async state machine is counted once, at its best observed condition coverage.
    security_lines: dict[str, dict[tuple[str, str], tuple[int, int]]] = {}

    security_class_rates: dict[str, list[float]] = {}

    for cls in root.findall(".//class"):
        name = cls.attrib.get("name", "")

        short = declaring_type_name(name)

        if short not in SECURITY_TYPES:
            continue

        seen_security_types.add(short)

        filename = cls.attrib.get("filename", "")

        line_branch_best = security_lines.setdefault(short, {})

        security_class_rates.setdefault(short, []).append(
            float(cls.attrib.get("branch-rate", "1")) * 100.0
        )

        # Cobertura class branch-rate is a fraction; use lines with condition-coverage when present.
        lines = cls.findall(".//line")

        for line in lines:
            cond = line.attrib.get("condition-coverage")

            if not cond or "(" not in cond:
                continue

            key = (filename, line.attrib.get("number", ""))

            part = cond.split("(", 1)[1].split(")", 1)[0]

            covered_s, total_s = part.split("/", 1)

            covered_i = int(covered_s)

            total_i = int(total_s)

            if key not in line_branch_best:
                line_branch_best[key] = (covered_i, total_i)

                continue

            prev_covered, prev_total = line_branch_best[key]

            prev_rate = prev_covered / prev_total if prev_total else 1.0

            new_rate = covered_i / total_i if total_i else 1.0

            if new_rate > prev_rate:
                line_branch_best[key] = (covered_i, total_i)

    for short in sorted(seen_security_types):
        line_branch_best = security_lines[short]

        branch_covered = sum(c for c, _ in line_branch_best.values())

        branch_count = sum(t for _, t in line_branch_best.values())

        if branch_count == 0:
            # Fall back to the class branch-rate attributes; take the worst so a fully
            # covered shell can never mask an uncovered state machine.
            rate = min(security_class_rates[short])
        else:
            rate = pct(branch_covered, branch_count)

        if rate < SECURITY_BRANCH_TARGET:
            failures.append(
                f"security type {short}: branch coverage {rate:.2f}% < {SECURITY_BRANCH_TARGET:.0f}%"
            )

    for missing in sorted(SECURITY_TYPES - seen_security_types):
        failures.append(
            f"required security type {missing} is absent from the coverage report"
        )

    print(f"Overall line coverage:   {line_rate:.2f}% (target >= {line_target:g}%)")

    print(f"Overall branch coverage: {branch_rate:.2f}% (target >= {branch_target:g}%)")

    if failures:
        print("Threshold failures:", file=sys.stderr)

        for f in failures:
            print(f"  - {f}", file=sys.stderr)

        return 1

    print("All coverage thresholds met.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
