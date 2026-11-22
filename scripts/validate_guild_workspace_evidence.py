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


def _add_requirements(parser, class_count):
    parser.add_argument("required_class", nargs=class_count)
    parser.add_argument(
        "--required-case",
        dest="required_cases",
        action="append",
        default=[],
        metavar="NAME",
        help="full className.methodName case that must pass in every data row (repeatable)",
    )


def _read_requirements():
    tokens = sys.stdin.buffer.read().decode("utf-8").split("\0")
    if len(tokens) < 2 or tokens[-1] or not all(tokens[:-1]):
        raise ValueError("Expected a nonempty, NUL-terminated selector stream.")

    parser = argparse.ArgumentParser(add_help=False, allow_abbrev=False)
    _add_requirements(parser, "+")
    return parser.parse_args(tokens[:-1])


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report")
    _add_requirements(parser, "*")
    parser.add_argument("--summary-json", metavar="PATH", help="atomically publish validated evidence metadata")
    parser.add_argument("--requirements-stdin", action="store_true", help="read NUL-terminated UTF-8 selector tokens from stdin")
    arguments = parser.parse_intermixed_args(argv)
    if arguments.requirements_stdin and (arguments.required_class or arguments.required_cases):
        parser.error("--requirements-stdin cannot be combined with command-line selectors")
    if not arguments.requirements_stdin and not arguments.required_class:
        parser.error("the following arguments are required: required_class")

    try:
        requirements = _read_requirements() if arguments.requirements_stdin else arguments
        summary = validate_report(arguments.report, requirements.required_class, requirements.required_cases)
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
