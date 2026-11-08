"""Build a focused CI summary from a validated TRX report.

The caller reads the report bytes; this module never opens a report file.
Only the bytes, their parsed form, and the shared ``TrxEvidence`` index are
used, so the report is validated exactly once, before any selector is checked.
"""
import hashlib
import xml.etree.ElementTree as ET

if __package__:
    from .trx_evidence import TrxEvidence
else:
    from trx_evidence import TrxEvidence


def _require(evidence, selector, names):
    """Every requested selector needs at least one result, all Passed."""
    for name in sorted(set(names)):
        outcomes = evidence.outcomes_for(name) if selector == "class" else evidence.outcomes_for_case(name)
        if not outcomes:
            raise ValueError(f"required {selector} {name!r} has no results")
        bad = [o for o in outcomes if o != "Passed"]
        if bad:
            raise ValueError(f"required {selector} {name!r} not all Passed: {bad}")


def build_summary(report_bytes, required_classes, required_cases=()):
    try:
        root = ET.fromstring(report_bytes)
    except ET.ParseError as exc:
        raise ValueError(f"malformed TRX report: {exc}") from exc

    evidence = TrxEvidence(root)
    required_classes = sorted(set(required_classes))
    required_cases = sorted(set(required_cases))
    _require(evidence, "class", required_classes)
    _require(evidence, "case", required_cases)

    return {
        "reportSha256": hashlib.sha256(report_bytes).hexdigest(),
        "passedTests": evidence.passed_count,
        "requiredClasses": required_classes,
        "requiredCases": required_cases,
    }
