#!/usr/bin/env python3
"""Preserve Coverlet's exact method and branch identities across runtime lanes."""

import argparse
import copy
import hashlib
import json
import math
import sys
from pathlib import Path
import xml.etree.ElementTree as ET

BRANCH_KEY = ("Line", "Offset", "EndOffset", "Ordinal", "Path")
BRANCH_FIELDS = frozenset((*BRANCH_KEY, "Hits"))
INT32_MAX = 2**31 - 1
UINT32_MAX = 2**32 - 1


def _object(value, context):
    if type(value) is not dict or any(type(k) is not str or not k for k in value):
        raise ValueError(f"invalid object in {context}")
    return value


def _count(value, maximum=INT32_MAX):
    if type(value) is not int or not 0 <= value <= maximum:
        raise ValueError("coverage count/position is outside its pinned CLR integer range")
    return value


def _branch_key(branch):
    return tuple(branch[k] for k in BRANCH_KEY)


def _methods(modules):
    for module, documents in _object(modules, "modules").items():
        for document, classes in _object(documents, "documents").items():
            for class_name, methods in _object(classes, "classes").items():
                for method_name, method in _object(methods, "methods").items():
                    yield (module, document, class_name, method_name), method


def validate_modules(modules):
    """Admit only the pinned Coverlet Lines/Branches schema, without coercion."""
    result = {}
    for identity, method in _methods(modules):
        if set(_object(method, "method")) != {"Lines", "Branches"}:
            raise ValueError("unexpected Coverlet method fields")
        for line, hits in _object(method["Lines"], "lines").items():
            if not line.isascii() or not line.isdecimal() or str(int(line)) != line or not 1 <= int(line) <= INT32_MAX:
                raise ValueError("noncanonical source line number")
            _count(hits)
        if type(method["Branches"]) is not list:
            raise ValueError("branches must be a list")
        seen = set()
        for branch in method["Branches"]:
            if set(_object(branch, "branch")) != BRANCH_FIELDS:
                raise ValueError("unexpected Coverlet branch fields")
            for field, value in branch.items():
                _count(value, UINT32_MAX if field == "Ordinal" else INT32_MAX)
            key = _branch_key(branch)
            if key in seen:
                raise ValueError("duplicate full branch identity")
            seen.add(key)
        result[identity] = method
    return result


def _unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON property")
        result[key] = value
    return result


def load_modules(path):
    value = json.loads(Path(path).read_text(encoding="utf-8"), object_pairs_hook=_unique_pairs)
    validate_modules(value)
    return value


def merge_modules(left, right):
    left_methods = validate_modules(left)
    right_methods = validate_modules(right)
    if set(left_methods) != set(right_methods):
        raise ValueError("coverage method inventories differ")
    # Empty containers are structure too; do not silently drop an empty class/document/module.
    def containers(value, depth=0):
        return {k: containers(v, depth + 1) for k, v in value.items()} if depth < 3 else set(value)
    if containers(left) != containers(right):
        raise ValueError("coverage container inventories differ")
    merged = copy.deepcopy(left)
    for identity, target in _methods(merged):
        other = right_methods[identity]
        if set(target["Lines"]) != set(other["Lines"]):
            raise ValueError("coverage line inventories differ")
        branches = {_branch_key(b): b for b in other["Branches"]}
        if {_branch_key(b) for b in target["Branches"]} != set(branches):
            raise ValueError("coverage branch inventories differ")
        for line in target["Lines"]:
            target["Lines"][line] = _count(target["Lines"][line] + other["Lines"][line])
        for branch in target["Branches"]:
            branch["Hits"] = _count(branch["Hits"] + branches[_branch_key(branch)]["Hits"])
    return merged


def project_cobertura(modules, template):
    records = validate_modules(modules)
    _template_shape(template)
    root = copy.deepcopy(template)
    if root.tag != "coverage" or root.get("version") != "1.9":
        raise ValueError("expected pinned Coverlet Cobertura shape")
    sources = [e.text or "" for e in root.findall("./sources/source")]
    packages = root.findall("./packages/package")
    by_package = {}
    for module in modules:
        name = module.replace("\\", "/").rsplit("/", 1)[-1].rsplit(".", 1)[0]
        if name in by_package:
            raise ValueError("ambiguous module display name")
        by_package[name] = module
    if len(packages) != len(by_package) or {p.get("name") for p in packages} != set(by_package):
        raise ValueError("Cobertura package inventory differs")
    for package in packages:
        module = by_package[package.get("name")]
        documents = modules[module]
        display = {}
        for document, classes in documents.items():
            path = document.replace("\\", "/")
            names = {path}
            for source in sources:
                base = source.replace("\\", "/")
                if base and path.lower().startswith(base.lower()):
                    names.add(path[len(base):])
            for class_name in classes:
                for filename in names:
                    display.setdefault((class_name, filename), set()).add(document)
        seen_classes = set()
        for cls in package.findall("./classes/class"):
            owners = display.get((cls.get("name"), cls.get("filename", "").replace("\\", "/")), set())
            if len(owners) != 1:
                raise ValueError("ambiguous or unknown class/document identity")
            document = next(iter(owners))
            class_name = cls.get("name")
            identity = (document, class_name)
            if identity in seen_classes:
                raise ValueError("duplicate Cobertura class/document")
            seen_classes.add(identity)
            _project_class(cls, documents[document][class_name])
        if seen_classes != {(d, c) for d, classes in documents.items() for c in classes}:
            raise ValueError("Cobertura class inventory differs")
        _rates(package, [m for (mod, *_), m in records.items() if mod == module])
    totals = _rates(root, list(records.values()), empty_modules=not modules)
    for name, value in zip(("lines-covered", "lines-valid", "branches-covered", "branches-valid"), totals):
        root.set(name, str(value))
    return root


def _template_shape(root):
    """The finite Coverlet 10 Cobertura shape; no unbound descendant can reach a gate."""
    rates = {"line-rate", "branch-rate", "complexity"}
    shapes = {
        "coverage": ({"line-rate", "branch-rate", "version", "timestamp", "lines-covered", "lines-valid", "branches-covered", "branches-valid"}, {"sources": (1, 1), "packages": (1, 1)}),
        "sources": (set(), {"source": (0, None)}),
        "source": (set(), {}),
        "packages": (set(), {"package": (0, None)}),
        "package": (rates | {"name"}, {"classes": (1, 1)}),
        "classes": (set(), {"class": (0, None)}),
        "class": (rates | {"name", "filename"}, {"methods": (1, 1), "lines": (1, 1)}),
        "methods": (set(), {"method": (0, None)}),
        "method": (rates | {"name", "signature"}, {"lines": (1, 1)}),
        "lines": (set(), {"line": (0, None)}),
        "conditions": (set(), {"condition": (1, None)}),
        "condition": ({"number", "type", "coverage"}, {}),
    }
    def visit(element):
        if element.tag == "line":
            if element.get("branch") not in ("True", "False"):
                raise ValueError("invalid Coverlet branch Boolean")
            is_branch = element.get("branch") == "True"
            attrs = {"number", "hits", "branch"} | ({"condition-coverage"} if is_branch else set())
            children = {"conditions": (1, 1)} if is_branch else {}
        elif element.tag in shapes:
            attrs, children = shapes[element.tag]
        else:
            raise ValueError("unexpected Cobertura element")
        if set(element.attrib) != attrs:
            raise ValueError("unexpected or missing Cobertura attributes")
        if element.tag != "source" and (element.text or "").strip():
            raise ValueError("unexpected Cobertura text")
        counts = {}
        for child in element:
            if child.tag not in children or (child.tail or "").strip():
                raise ValueError("unexpected Cobertura child position")
            counts[child.tag] = counts.get(child.tag, 0) + 1
            visit(child)
        for tag, (minimum, maximum) in children.items():
            if counts.get(tag, 0) < minimum or maximum is not None and counts.get(tag, 0) > maximum:
                raise ValueError("invalid Cobertura child cardinality")
    if root.tag != "coverage":
        raise ValueError("expected Coverlet coverage root")
    visit(root)


def _percent(covered, total):
    # Coverlet 10.0.1 CoverageDetails.Percent: floor binary-double arithmetic in this order.
    return math.floor((covered / total) * 10000) / 100 if total else 100.0


def _number(value):
    return str(int(value)) if value.is_integer() else repr(value)


def _rates(element, methods, *, empty_modules=False):
    lines = [hits for method in methods for hits in method["Lines"].values()]
    branches = [branch for method in methods for branch in method["Branches"]]
    covered_lines, covered_branches = sum(h > 0 for h in lines), sum(b["Hits"] > 0 for b in branches)
    element.set("line-rate", "0" if empty_modules else _number(_percent(covered_lines, len(lines)) / 100))
    element.set("branch-rate", "0" if empty_modules else _number(_percent(covered_branches, len(branches)) / 100))
    if element.tag != "coverage":
        element.set("complexity", str(sum(max(1, len(m["Branches"])) for m in methods)))
    return covered_lines, len(lines), covered_branches, len(branches)


def _method_display(name):
    # Same derivation as the pinned reporter; it is a display key, not full method identity.
    tail = name.split(":")[-1]
    return tail.split("(")[0], "(" + tail.split("(")[-1]


def _line_number(element):
    number = element.get("number", "")
    if not number.isascii() or not number.isdecimal() or str(int(number)) != number or int(number) < 1:
        raise ValueError("invalid Cobertura line number")
    return number


def _project_class(element, methods):
    # Generic/non-generic helpers can share the XML name/signature. Their unchanged line
    # footprints disambiguate the full JSON owner; never use hits or XML position as a tie-break.
    candidates = {}
    for name, method in methods.items():
        if method["Lines"]:
            key = (*_method_display(name), tuple(sorted(map(int, method["Lines"]))))
            candidates.setdefault(key, []).append((name, method))
    seen_methods = set()
    class_occurrences = []
    for xml_method in element.findall("./methods/method"):
        lines = xml_method.findall("./lines/line")
        footprint = tuple(sorted(int(_line_number(line)) for line in lines))
        key = (xml_method.get("name"), xml_method.get("signature"), footprint)
        owners = candidates.get(key, [])
        if len(owners) != 1 or owners[0][0] in seen_methods:
            raise ValueError("ambiguous or unknown full method identity")
        name, method = owners[0]
        seen_methods.add(name)
        if len(lines) != len(method["Lines"]):
            raise ValueError("method line occurrence inventory differs")
        for line in lines:
            number = _line_number(line)
            _project_line(line, method, number)
            class_occurrences.append((method, number))
        _rates(xml_method, [method])
    if seen_methods != {name for name, m in methods.items() if m["Lines"]}:
        raise ValueError("Cobertura method inventory differs")
    class_lines = element.findall("./lines/line")
    if len(class_lines) != len(class_occurrences):
        raise ValueError("class line occurrence inventory differs")
    # The official reporter appends class copies in method/line traversal order, retaining
    # repeated source numbers. This is not a global source-line de-duplication.
    for line, (method, number) in zip(class_lines, class_occurrences):
        if _line_number(line) != number:
            raise ValueError("class line copies disagree with their method occurrences")
        _project_line(line, method, number)
    _rates(element, list(methods.values()))


def _project_line(element, method, number):
    element.set("hits", str(method["Lines"][number]))
    branches = [b for b in method["Branches"] if b["Line"] == int(number)]
    if element.get("branch") != str(bool(branches)):
        raise ValueError("Cobertura branch-point inventory differs")
    if not branches:
        if "condition-coverage" in element.attrib or list(element):
            raise ValueError("unexpected conditions on a non-branch line")
        return
    covered = sum(b["Hits"] > 0 for b in branches)
    element.set("condition-coverage", f"{_number(_percent(covered, len(branches)))}% ({covered}/{len(branches)})")
    groups = {}
    for branch in branches:
        groups.setdefault(str(branch["Offset"]), []).append(branch)
    conditions = element.findall("./conditions/condition")
    if len(conditions) != len(groups) or {c.get("number") for c in conditions} != set(groups):
        raise ValueError("Cobertura decision inventory differs")
    for condition in conditions:
        group = groups[condition.get("number")]
        if condition.get("type") != ("switch" if len(group) > 2 else "jump"):
            raise ValueError("Cobertura decision shape differs")
        condition.set("coverage", _number(_percent(sum(b["Hits"] > 0 for b in group), len(group))) + "%")


def validate_pair(modules, template):
    projected = project_cobertura(modules, template)
    def comparable(element):
        return (element.tag, tuple(sorted(element.attrib.items())), (element.text or "").strip(),
                tuple(comparable(child) for child in element))
    if comparable(projected) != comparable(template):
        raise ValueError("JSON projection disagrees with its official single-run Cobertura oracle")


def _artifact(record):
    if type(record) is not dict or set(record) != {"path", "sha256"}:
        raise ValueError("invalid coverage artifact record")
    path = Path(record["path"])
    if not path.is_absolute() or not path.is_file() or not path.stat().st_size:
        raise ValueError("missing coverage proof artifact")
    with path.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
    if digest != record["sha256"]:
        raise ValueError("coverage proof artifact hash changed")
    return path.resolve()


def merge_receipt(receipt_path, output_path, *, verify_source=False):
    """Consume a successful coordinator receipt, revalidating its exact paired artifacts."""
    import runtime_test_runner as runner
    receipt_path, output_path = Path(receipt_path).resolve(), Path(output_path).resolve()
    if output_path.exists():
        raise ValueError("merged coverage output must be fresh")
    receipt_bytes = receipt_path.read_bytes()
    receipt_snapshot = {"path": str(receipt_path), "sha256": hashlib.sha256(receipt_bytes).hexdigest()}
    receipt = json.loads(receipt_bytes, object_pairs_hook=_unique_pairs)
    if receipt.get("schemaVersion") != 1 or receipt.get("status") != "passed":
        raise ValueError("coverage requires a successful runtime coordinator receipt")
    source = receipt.get("sourceSha", "")
    if type(source) is not str or len(source) != 40 or any(c not in "0123456789abcdef" for c in source):
        raise ValueError("invalid runtime source identity")
    if receipt.get("sourceState", {}).get("sha") != source:
        raise ValueError("runtime source SHA disagrees with its pinned source state")
    if verify_source and runner.source_state(receipt_path.parent) != receipt["sourceState"]:
        raise ValueError("source changed since runtime qualification")
    if not receipt.get("configuration") or not receipt.get("testAssembly"):
        raise ValueError("missing runtime executable/configuration binding")
    identity = receipt.get("buildIdentity", {})
    files = _object(identity.get("files"), "built closure")
    if not files or receipt["testAssembly"] not in files or identity.get("provenance") not in ("runner-built", "caller-owned"):
        raise ValueError("missing runtime build identity/provenance")
    for path, digest in files.items():
        _artifact({"path": path, "sha256": digest})
    _artifact(receipt.get("coverageSettings"))
    lanes = receipt.get("lanes")
    if type(lanes) is not list or [l.get("name") for l in lanes] != ["resource", "remainder"]:
        raise ValueError("both ordered runtime coverage lanes are required")
    inputs, templates, physical = [], [], set()
    for lane in lanes:
        if lane.get("joined") is not True or type(lane.get("exitCode")) is not int or lane["exitCode"] != 0:
            raise ValueError("runtime coverage lane failed or was not joined")
        trx = _artifact(lane.get("trx"))
        lane_directory = (receipt_path.parent / lane["name"]).resolve()
        if trx.parent != lane_directory:
            raise ValueError("TRX is not owned by its declared runtime lane")
        reports = lane.get("coverage")
        if type(reports) is not dict or set(reports) != {"json", "cobertura"}:
            raise ValueError("paired rich coverage reports are required")
        paths = {name: _artifact(record) for name, record in reports.items()}
        for path in (trx, *paths.values()):
            if not path.is_relative_to(lane_directory):
                raise ValueError("coverage artifact is not owned by its declared lane")
            stat = path.stat()
            identity_key = (stat.st_dev, stat.st_ino)
            if identity_key in physical:
                raise ValueError("runtime lanes reuse the same physical proof artifact")
            physical.add(identity_key)
        if runner.coverage_artifacts(ET.parse(trx).getroot(), trx.parent) != reports:
            raise ValueError("coverage pair differs from declared TRX collector attachments")
        modules = load_modules(paths["json"])
        template = ET.parse(paths["cobertura"]).getroot()
        validate_pair(modules, template)
        inputs.append(modules)
        templates.append(template)
    merged = merge_modules(*inputs)
    projected = project_cobertura(merged, templates[0])
    # No input, executable, symbol or settings mutation is admitted while merging.
    for path, digest in files.items():
        _artifact({"path": path, "sha256": digest})
    _artifact(receipt["coverageSettings"])
    for lane in lanes:
        _artifact(lane["trx"])
        for record in lane["coverage"].values():
            _artifact(record)
    _artifact(receipt_snapshot)
    if verify_source and runner.source_state(receipt_path.parent) != receipt["sourceState"]:
        raise ValueError("source changed during coverage union")
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("xb") as stream:
        ET.ElementTree(projected).write(stream, encoding="utf-8", xml_declaration=True)
    try:
        _artifact(receipt_snapshot)
    except ValueError:
        output_path.unlink()
        raise
    return {"schemaVersion": 1, "status": "passed", "sourceSha": source,
            "configuration": receipt["configuration"], "testAssembly": receipt["testAssembly"],
            "buildIdentity": identity, "coverageSettings": receipt["coverageSettings"],
            "runtimeReceipt": receipt_snapshot, "sourceState": receipt["sourceState"],
            "inputs": [{"name": l["name"], "trx": l["trx"], "coverage": l["coverage"]} for l in lanes],
            "mergedCobertura": runner.artifact(output_path),
            "counts": {key: int(projected.get(key)) for key in
                       ("lines-covered", "lines-valid", "branches-covered", "branches-valid")}}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime-summary", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args(argv)
    try:
        import runtime_test_runner as runner
        proof_path = args.output.with_suffix(args.output.suffix + ".receipt.json")
        if proof_path.exists():
            raise ValueError("coverage union receipt must be fresh")
        proof = merge_receipt(args.runtime_summary, args.output, verify_source=True)
        try:
            _artifact(proof["runtimeReceipt"])
            if runner.source_state(args.runtime_summary.resolve().parent) != proof["sourceState"]:
                raise ValueError("source changed before coverage proof publication")
        except ValueError:
            args.output.unlink()
            raise
        with proof_path.open("x", encoding="utf-8") as stream:
            json.dump(proof, stream, indent=2)
            stream.write("\n")
        print(f"Merged coverage from validated lane receipt: {args.output}")
        return 0
    except (OSError, ValueError, KeyError, TypeError, RuntimeError, ET.ParseError) as error:
        print(f"coverage union refused: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
