#!/usr/bin/env python3
"""Enforce tiered coverage thresholds from a Cobertura XML report."""

from __future__ import annotations

import sys
import xml.etree.ElementTree as ET

LINE_TARGET = 85.0

BRANCH_TARGET = 75.0

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


def main(argv: list[str] | None = None) -> int:
    args = argv if argv is not None else sys.argv[1:]

    if len(args) != 1:
        print("usage: coverage_threshold.py <coverage.cobertura.xml>", file=sys.stderr)
        return 2

    root = ET.parse(args[0]).getroot()

    line_rate = float(root.attrib.get("line-rate", "0")) * 100.0

    branch_rate = float(root.attrib.get("branch-rate", "0")) * 100.0

    failures: list[str] = []
    seen_security_types: set[str] = set()

    if line_rate < LINE_TARGET:
        failures.append(f"line coverage {line_rate:.2f}% < {LINE_TARGET:.0f}%")

    if branch_rate < BRANCH_TARGET:
        failures.append(f"branch coverage {branch_rate:.2f}% < {BRANCH_TARGET:.0f}%")

    for cls in root.findall(".//class"):
        name = cls.attrib.get("name", "")

        short = name.rsplit(".", 1)[-1]

        if short not in SECURITY_TYPES:
            continue

        seen_security_types.add(short)

        # Cobertura class branch-rate is a fraction; use lines with condition-coverage when present.
        lines = cls.findall(".//line")

        line_branch_best: dict[str, tuple[int, int]] = {}

        for line in lines:
            cond = line.attrib.get("condition-coverage")

            if not cond or "(" not in cond:
                continue

            line_no = line.attrib.get("number", "")

            part = cond.split("(", 1)[1].split(")", 1)[0]

            covered_s, total_s = part.split("/", 1)

            covered_i = int(covered_s)

            total_i = int(total_s)

            if line_no not in line_branch_best:
                line_branch_best[line_no] = (covered_i, total_i)

                continue

            prev_covered, prev_total = line_branch_best[line_no]

            prev_rate = prev_covered / prev_total if prev_total else 1.0

            new_rate = covered_i / total_i if total_i else 1.0

            if new_rate > prev_rate:
                line_branch_best[line_no] = (covered_i, total_i)

        branch_covered = sum(c for c, _ in line_branch_best.values())

        branch_count = sum(t for _, t in line_branch_best.values())

        if branch_count == 0:
            # Fall back to class branch-rate attribute.
            rate = float(cls.attrib.get("branch-rate", "1")) * 100.0

            if rate < SECURITY_BRANCH_TARGET:
                failures.append(
                    f"security type {short}: branch coverage {rate:.2f}% < {SECURITY_BRANCH_TARGET:.0f}%"
                )

            continue

        rate = pct(branch_covered, branch_count)

        if rate < SECURITY_BRANCH_TARGET:
            failures.append(
                f"security type {short}: branch coverage {rate:.2f}% < {SECURITY_BRANCH_TARGET:.0f}%"
            )

    for missing in sorted(SECURITY_TYPES - seen_security_types):
        failures.append(
            f"required security type {missing} is absent from the coverage report"
        )

    print(f"Overall line coverage:   {line_rate:.2f}% (target >= {LINE_TARGET:.0f}%)")

    print(f"Overall branch coverage: {branch_rate:.2f}% (target >= {BRANCH_TARGET:.0f}%)")

    if failures:
        print("Threshold failures:", file=sys.stderr)

        for f in failures:
            print(f"  - {f}", file=sys.stderr)

        return 1

    print("All coverage thresholds met.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
