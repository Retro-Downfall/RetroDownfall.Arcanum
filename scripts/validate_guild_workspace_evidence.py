#!/usr/bin/env python3
"""Admit focused Guild workspace CI evidence, including required executed fixtures."""
import argparse
import json
import os
from pathlib import Path
import sys
import tempfile

if __package__:
    from .guild_workspace_summary import build_summary
else:
    from guild_workspace_summary import build_summary


def required_tests_passed(outcomes):
    return len(outcomes) > 0 and all(o == "Passed" for o in outcomes)


def validate_report(path, required_classes, required_cases=()):
    """Validate one immutable byte snapshot and report its hash and required contracts."""
    try:
        report_bytes = Path(path).read_bytes()
    except OSError as error:
        raise ValueError(f"Cannot read TRX evidence: {error}") from error
    return build_summary(report_bytes, required_classes, required_cases)


def validate(path, required_classes, required_cases=()):
    """Return the passed count, rejecting missing, malformed or incomplete evidence."""
    return validate_report(path, required_classes, required_cases)["passedTests"]


def _write_summary(path, summary):
    """Publish a complete summary in one rename; failed publication leaves no temporary file."""
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(mode="w", encoding="utf-8", dir=path.parent,
                                         prefix=f".{path.name}.", suffix=".tmp", delete=False) as stream:
            temporary = Path(stream.name)
            json.dump(summary, stream, sort_keys=True, indent=2)
            stream.write("\n")
        os.replace(temporary, path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report")
    parser.add_argument("required_class", nargs="+")
    parser.add_argument(
        "--required-case",
        dest="required_cases",
        action="append",
        default=[],
        metavar="NAME",
        help="full className.methodName case that must pass in every data row (repeatable)",
    )
    parser.add_argument("--summary-json", metavar="PATH", help="atomically publish validated evidence metadata")
    arguments = parser.parse_args(argv)
    try:
        summary = validate_report(arguments.report, arguments.required_class, arguments.required_cases)
        if arguments.summary_json:
            destination = Path(arguments.summary_json)
            if destination.resolve() == Path(arguments.report).resolve():
                raise ValueError("Summary output must not overwrite the input TRX report.")
            _write_summary(destination, summary)
        count = summary["passedTests"]
    except (ValueError, OSError) as error:
        print(error, file=sys.stderr)
        return 1
    print(f"Focused Guild workspace evidence: {count} passed tests; all required contracts passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
