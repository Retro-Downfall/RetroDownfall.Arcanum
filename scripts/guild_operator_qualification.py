#!/usr/bin/env python3
"""Select explicit focused Guild witnesses and admit their completed TRX evidence."""
import argparse
from collections import Counter
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import sys
import tempfile
import xml.etree.ElementTree as ET

if __package__:
    from .trx_evidence import NS, TrxEvidence
    from .validate_guild_workspace_evidence import _write_summary
else:
    from trx_evidence import NS, TrxEvidence
    from validate_guild_workspace_evidence import _write_summary

MANIFEST = Path(__file__).resolve().parents[1] / "tests/RetroDownfall.Arcanum.Tests/TestData/Contracts/Arcanum.GuildOperatorQualification.json"
NAME = re.compile(r"[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+")


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate manifest field {key!r}")
        result[key] = value
    return result


def _strings(values, *, names=False):
    if not isinstance(values, list) or any(not isinstance(value, str) or not value for value in values):
        raise ValueError("manifest selectors must be nonempty strings in an array")
    if len(set(values)) != len(values):
        raise ValueError("duplicate manifest selector")
    if names and any(NAME.fullmatch(value) is None for value in values):
        raise ValueError("invalid fully qualified witness name")


def load_manifest(path=MANIFEST):
    raw = Path(path).read_bytes()
    try:
        manifest = json.loads(raw, object_pairs_hook=_unique_object)
    except (UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"invalid operator manifest: {error}") from error
    fields = {"schemaVersion", "profile", "projects", "platformCases", "allowedSkips", "omissions"}
    if not isinstance(manifest, dict) or set(manifest) != fields:
        raise ValueError("unknown or missing operator manifest fields")
    if type(manifest["schemaVersion"]) is not int or manifest["schemaVersion"] != 1 or manifest["profile"] not in ("guild-operator", "guild-a2a"):
        raise ValueError("unsupported operator manifest version or profile")
    expected_projects = {"core", "compendium"} if manifest["profile"] == "guild-operator" else {"core"}
    if not isinstance(manifest["projects"], dict) or set(manifest["projects"]) != expected_projects:
        raise ValueError("manifest must declare the profile project inventory")
    for declaration in manifest["projects"].values():
        if not isinstance(declaration, dict) or set(declaration) != {"requiredClasses", "requiredCases", "caseSources", "requiredCaseRows", "requiredCaseDisplays"}:
            raise ValueError("unknown or missing project witness fields")
        for key in ("requiredClasses", "requiredCases"):
            _strings(declaration[key], names=True)
        if not declaration["requiredClasses"]:
            raise ValueError("every project needs required suites")
        rows = declaration["requiredCaseRows"]
        if not isinstance(rows, dict):
            raise ValueError("required case rows must be an object")
        _strings(list(rows), names=True)
        if not set(rows) <= set(declaration["requiredCases"]) or any(type(count) is not int or count < 1 for count in rows.values()):
            raise ValueError("invalid required theory row inventory")
        if manifest["profile"] == "guild-a2a" and (not declaration["requiredCases"] or set(rows) != set(declaration["requiredCases"])):
            raise ValueError("A2A qualification requires a complete named case and row inventory")
        displays = declaration["requiredCaseDisplays"]
        if not isinstance(displays, dict) or not set(displays) <= set(rows):
            raise ValueError("invalid required case display inventory")
        for name, variants in displays.items():
            _strings(variants)
            if len(variants) != rows[name] or any(value != name and not value.startswith(name + "(") for value in variants):
                raise ValueError("invalid required variant identity")
        _strings(declaration["caseSources"])
        for source in declaration["caseSources"]:
            path = PurePosixPath(source)
            if path.is_absolute() or ".." in path.parts or path.parts[0] != "tests" or path.suffix != ".cs":
                raise ValueError("case source must be a repository test source path")
    for key in ("platformCases", "allowedSkips"):
        if not isinstance(manifest[key], dict) or set(manifest[key]) != {"macos", "windows"}:
            raise ValueError("both native platforms must be declared")
        for values in manifest[key].values():
            _strings(values, names=key == "platformCases")
    _strings(manifest["omissions"])
    if not manifest["omissions"]:
        raise ValueError("focused qualification must declare its omissions")
    return manifest, raw


def witnesses(manifest, project, platform):
    if project not in manifest["projects"] or platform not in manifest["platformCases"]:
        raise ValueError("unknown project or platform")
    declaration = manifest["projects"][project]
    cases = set(declaration["requiredCases"])
    if project == "core":
        cases.update(manifest["platformCases"][platform])
    return sorted(declaration["requiredClasses"]), sorted(cases)


def make_filter(manifest, project, platform):
    classes, cases = witnesses(manifest, project, platform)
    # A trailing dot distinguishes the complete class from similarly named classes.
    suites = "|".join("FullyQualifiedName~" + name + "." for name in classes)
    # Named source-analysis witnesses are explicit exceptions to the broad category exclusion.
    exact = "|".join("FullyQualifiedName=" + name for name in cases)
    selection = "(Category!=Perf&Category!=HostedProducerAnalysis&(" + suites + "))"
    return selection + ("|" + exact if exact else "")


def validate_evidence(report_bytes, manifest, project, platform):
    try:
        root = ET.fromstring(report_bytes)
    except ET.ParseError as error:
        raise ValueError(f"malformed TRX report: {error}") from error
    evidence = TrxEvidence(root)
    classes, cases = witnesses(manifest, project, platform)
    allowed = set(manifest["allowedSkips"][platform]) if project == "core" else set()
    definitions = {}
    for unit in root.findall(".//t:UnitTest", NS):
        method = unit.find("t:TestMethod", NS)
        if method is not None:
            definitions[unit.get("id")] = method.get("className", "") + "." + method.get("name", "")
    skipped = []
    for row in root.findall(".//t:UnitTestResult", NS):
        if row.get("outcome") == "NotExecuted":
            display = row.get("testName", "")
            identity = definitions.get(row.get("testId"), "")
            if display not in allowed or not display.startswith(identity + "("):
                raise ValueError(f"unexpected skipped operator witness {display!r}")
            skipped.append(display)
    if manifest["profile"] == "guild-a2a":
        displays = [row.get("testName", "") for row in root.findall(".//t:UnitTestResult", NS)]
        if len(displays) != len(set(displays)):
            raise ValueError("duplicate A2A scenario identity")
    for name, expected in manifest["projects"][project]["requiredCaseDisplays"].items():
        actual = [row.get("testName", "") for row in root.findall(".//t:UnitTestResult", NS)
            if definitions.get(row.get("testId")) == name]
        if Counter(actual) != Counter(expected):
            raise ValueError(f"required case {name!r} has missing, duplicate or unexpected scenario identities")
    for name in classes:
        outcomes = evidence.outcomes_for(name)
        if not outcomes or "Passed" not in outcomes:
            raise ValueError(f"required suite {name!r} has no passed results")
    for name in cases:
        outcomes = evidence.outcomes_for_case(name)
        if not outcomes or any(outcome != "Passed" for outcome in outcomes):
            raise ValueError(f"required case {name!r} has missing or non-passing results")
        expected = manifest["projects"][project]["requiredCaseRows"].get(name)
        if expected is not None and len(outcomes) != expected:
            raise ValueError(f"required case {name!r} needs {expected} rows, found {len(outcomes)}")
    selection = make_filter(manifest, project, platform)
    return {"qualification": manifest["profile"], "project": project, "platform": platform,
        "reportSha256": hashlib.sha256(report_bytes).hexdigest(), "passedTests": evidence.passed_count,
        "requiredClasses": classes, "requiredCases": cases,
        "requiredCaseRows": manifest["projects"][project]["requiredCaseRows"],
        "requiredCaseDisplays": manifest["projects"][project]["requiredCaseDisplays"], "allowedSkippedTests": sorted(skipped),
        "filterSha256": hashlib.sha256(selection.encode()).hexdigest(), "omissions": manifest["omissions"]}


def write_runsettings(path, selection):
    root = ET.Element("RunSettings")
    configuration = ET.SubElement(root, "RunConfiguration")
    ET.SubElement(configuration, "TestCaseFilter").text = selection
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, prefix="." + path.name + ".", suffix=".tmp", delete=False) as output:
            temporary = Path(output.name)
            output.write(ET.tostring(root, encoding="utf-8", xml_declaration=True))
        os.replace(temporary, path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=("filter", "validate"))
    parser.add_argument("--project", choices=("core", "compendium"), required=True)
    parser.add_argument("--platform", choices=("macos", "windows"), required=True)
    parser.add_argument("--manifest", type=Path, default=MANIFEST)
    parser.add_argument("--report", type=Path)
    parser.add_argument("--summary-json", type=Path)
    parser.add_argument("--settings-output", type=Path)
    arguments = parser.parse_args(argv)
    try:
        manifest, raw = load_manifest(arguments.manifest)
        if arguments.action == "filter":
            selection = make_filter(manifest, arguments.project, arguments.platform)
            if arguments.settings_output:
                if arguments.settings_output.resolve() == arguments.manifest.resolve():
                    raise ValueError("runsettings must not overwrite the witness manifest")
                write_runsettings(arguments.settings_output, selection)
            print(selection)
        else:
            if arguments.report is None:
                raise ValueError("validation requires --report")
            if arguments.summary_json and arguments.summary_json.resolve() in (arguments.report.resolve(), arguments.manifest.resolve()):
                raise ValueError("summary must not overwrite its evidence inputs")
            summary = validate_evidence(arguments.report.read_bytes(), manifest, arguments.project, arguments.platform)
            summary["manifestSha256"] = hashlib.sha256(raw).hexdigest()
            if arguments.summary_json:
                _write_summary(arguments.summary_json, summary)
            print(f"{manifest['profile']} {arguments.project}: {summary['passedTests']} passed; all required witnesses admitted.")
    except (ValueError, OSError) as error:
        print(error, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
