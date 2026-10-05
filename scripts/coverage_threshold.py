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

# Assemblies that are instrumented so they appear in the report, but are held to their own
# per-assembly line floor and are removed from the aggregate that DEFAULT_LINE_TARGET and
# DEFAULT_BRANCH_TARGET apply to. The Cli is large and mostly an interactive surface; Secrets is
# small and platform specific. Neither may move the floors Core, Infrastructure and Api are held to.
# name -> (environment override, default line floor in percent). The defaults sit a few points under
# what the Cli and Security test namespaces alone reached, so the full suite clears them with margin; a
# floor of 0 reports an assembly without gating it.
#
# Measured 2026-10-04 on macOS from a Cli + Security test-namespace subset (not the full suite): Cli
# 80.69% of 32,834 lines, Secrets 66.72% of 568 lines. This is the one place the measurement is recorded;
# docs/Arcanum.DESIGN.md section 13.1 states only the floors, because a percentage in prose goes stale.
REPORTED_ASSEMBLY_LINE_FLOORS = {
    "RetroDownfall.Arcanum.Cli": ("COVERAGE_CLI_LINE_TARGET", 75.0),
    "RetroDownfall.Arcanum.Secrets": ("COVERAGE_SECRETS_LINE_TARGET", 60.0),
}

SECURITY_BRANCH_TARGET_OVERRIDES = {
    "ApiKeyDigestCache": 85.0,
}

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
    "WorkspacePathPolicy",
    # The authenticated-envelope codec. It seals and opens every Covenant fragment, so a branch it
    # never exercises is an authentication path nothing has proved refuses. Held to the security
    # target rather than the general one for the same reason GrimoireKeyDerivation is.
    "CovenantEnvelopeCodec",
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


class PackageStats:
    """One assembly's rates plus the weights needed to recombine assemblies.

    Coverlet writes one ``<package>`` per instrumented assembly with exact ``line-rate`` and
    ``branch-rate`` attributes but no counts, so the weights are rebuilt from the class line
    entries: unique (source file, line) pairs for lines, and the largest condition total any class
    reports for a line for branches. The rates stay exact; only the relative weights are counted.
    """

    def __init__(self, package: ET.Element) -> None:
        self.name = package.attrib.get("name", "")

        lines: dict[tuple[str, str], bool] = {}
        branches: dict[tuple[str, str], tuple[int, int]] = {}

        for cls in package.findall("./classes/class"):
            filename = cls.attrib.get("filename", "")

            for line in cls.findall("./lines/line"):
                key = (filename, line.attrib.get("number", ""))

                lines[key] = lines.get(key, False) or int(line.attrib.get("hits", "0")) > 0

                cond = line.attrib.get("condition-coverage")

                if not cond or "(" not in cond:
                    continue

                covered_s, total_s = cond.split("(", 1)[1].split(")", 1)[0].split("/", 1)

                total = int(total_s)

                if key not in branches or total > branches[key][1]:
                    branches[key] = (int(covered_s), total)

        self.valid_lines = len(lines)
        self.covered_lines = sum(1 for hit in lines.values() if hit)
        self.valid_branches = sum(total for _, total in branches.values())
        self.covered_branches = sum(covered for covered, _ in branches.values())

        self.line_rate = self._rate(
            package.attrib.get("line-rate"), self.covered_lines, self.valid_lines
        )
        self.branch_rate = self._rate(
            package.attrib.get("branch-rate"), self.covered_branches, self.valid_branches
        )

    @staticmethod
    def _rate(attribute: str | None, covered: int, valid: int) -> float:
        if attribute is not None:
            return float(attribute) * 100.0

        return pct(covered, valid)


def aggregate_rates(
    root: ET.Element, packages: list[PackageStats]
) -> tuple[float, float]:
    """Line and branch rate over every assembly that is not a reported-only assembly.

    A report that carries none of the reported assemblies (or nothing else) keeps the root
    attributes, so the aggregate of a report without them is unchanged.
    """
    gate = [p for p in packages if p.name not in REPORTED_ASSEMBLY_LINE_FLOORS]

    if len(gate) == len(packages) or not gate:
        return (
            float(root.attrib.get("line-rate", "0")) * 100.0,
            float(root.attrib.get("branch-rate", "0")) * 100.0,
        )

    line_weight = sum(p.valid_lines for p in gate)
    branch_weight = sum(p.valid_branches for p in gate)

    line_rate = (
        sum(p.line_rate * p.valid_lines for p in gate) / line_weight
        if line_weight
        else 100.0
    )
    branch_rate = (
        sum(p.branch_rate * p.valid_branches for p in gate) / branch_weight
        if branch_weight
        else 100.0
    )

    return line_rate, branch_rate


def security_branch_target(name: str) -> float:
    return SECURITY_BRANCH_TARGET_OVERRIDES.get(name, SECURITY_BRANCH_TARGET)


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

    try:
        assembly_floors = {
            name: read_target(env_name, default)
            for name, (env_name, default) in REPORTED_ASSEMBLY_LINE_FLOORS.items()
        }
    except ValueError as exc:
        print(str(exc), file=sys.stderr)
        return 2

    root = ET.parse(args[0]).getroot()

    packages = [PackageStats(p) for p in root.findall("./packages/package")]

    line_rate, branch_rate = aggregate_rates(root, packages)

    failures: list[str] = []
    seen_security_types: set[str] = set()

    if line_rate < line_target:
        failures.append(f"line coverage {line_rate:.2f}% < {line_target:g}%")

    if branch_rate < branch_target:
        failures.append(f"branch coverage {branch_rate:.2f}% < {branch_target:g}%")

    # One branch tally per security type, aggregated over the declaring class *and*
    # every compiler-generated state machine nested inside it. Keyed by
    # (source file, line number) so a line reported by both the synchronous shell and
    # its async state machine is counted once, at its *worst* observed condition coverage:
    # keeping the best let a fully covered shell mask an uncovered state machine on the
    # same line, which is the async body this gate exists to inspect.
    security_lines: dict[str, dict[tuple[str, str], tuple[int, int]]] = {}

    security_class_rates: dict[str, list[float]] = {}

    for cls in root.findall(".//class"):
        name = cls.attrib.get("name", "")

        short = declaring_type_name(name)

        if short not in SECURITY_TYPES:
            continue

        seen_security_types.add(short)

        filename = cls.attrib.get("filename", "")

        line_branch_worst = security_lines.setdefault(short, {})

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

            if key not in line_branch_worst:
                line_branch_worst[key] = (covered_i, total_i)

                continue

            prev_covered, prev_total = line_branch_worst[key]

            prev_rate = prev_covered / prev_total if prev_total else 1.0

            new_rate = covered_i / total_i if total_i else 1.0

            if new_rate < prev_rate:
                line_branch_worst[key] = (covered_i, total_i)

    for short in sorted(seen_security_types):
        line_branch_worst = security_lines[short]

        branch_covered = sum(c for c, _ in line_branch_worst.values())

        branch_count = sum(t for _, t in line_branch_worst.values())

        if branch_count == 0:
            # Fall back to the class branch-rate attributes; take the worst so a fully
            # covered shell can never mask an uncovered state machine.
            rate = min(security_class_rates[short])
        else:
            rate = pct(branch_covered, branch_count)

        target = security_branch_target(short)

        if rate < target:
            failures.append(
                f"security type {short}: branch coverage {rate:.2f}% < {target:.0f}%"
            )

    for missing in sorted(SECURITY_TYPES - seen_security_types):
        failures.append(
            f"required security type {missing} is absent from the coverage report"
        )

    reported = {p.name: p for p in packages if p.name in REPORTED_ASSEMBLY_LINE_FLOORS}

    for name, floor in assembly_floors.items():
        package = reported.get(name)

        if package is None:
            failures.append(f"required assembly {name} is absent from the coverage report")

            continue

        if package.line_rate < floor:
            failures.append(
                f"assembly {name}: line coverage {package.line_rate:.2f}% < {floor:g}%"
            )

    print(f"Overall line coverage:   {line_rate:.2f}% (target >= {line_target:g}%)")

    print(f"Overall branch coverage: {branch_rate:.2f}% (target >= {branch_target:g}%)")

    for name in sorted(reported):
        package = reported[name]

        print(
            f"Reported assembly {name}: line {package.line_rate:.2f}%, "
            f"branch {package.branch_rate:.2f}% "
            f"(line floor >= {assembly_floors[name]:g}%; outside the aggregate)"
        )

    if failures:
        print("Threshold failures:", file=sys.stderr)

        for f in failures:
            print(f"  - {f}", file=sys.stderr)

        return 1

    print("All coverage thresholds met.")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
