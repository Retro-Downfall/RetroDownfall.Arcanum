"""Behavioral controls for the lossless, same-build Coverlet adapter."""

import copy
import contextlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock
import xml.etree.ElementTree as ET

sys.path.insert(0, str(Path(__file__).parent))

import coverage_union
import coverage_threshold


def branch(path, hits, *, ordinal=None):
    return {"Line": 10, "Offset": 2, "EndOffset": 7 + path,
            "Path": path, "Ordinal": path if ordinal is None else ordinal, "Hits": hits}


def modules(first=(1, 0), second=(0, 0)):
    return {"Example.dll": {"/source/Owner.cs": {"Example.Owner": {
        "System.Int32 Example.Owner::First(System.Boolean)": {
            "Lines": {"10": 1}, "Branches": [branch(0, first[0]), branch(1, first[1])]},
        "System.Int32 Example.Owner::Second(System.Boolean)": {
            "Lines": {"10": 1}, "Branches": [branch(0, second[0]), branch(1, second[1])]}}}}}


def methods(value):
    return value["Example.dll"]["/source/Owner.cs"]["Example.Owner"]


def template():
    # Two distinct methods deliberately share line 10; Coverlet retains both occurrences.
    return ET.fromstring('''<coverage line-rate="1" branch-rate="0.25" lines-covered="2" lines-valid="2" branches-covered="1" branches-valid="4" version="1.9" timestamp="0">
      <sources><source>/source/</source></sources><packages><package name="Example" line-rate="1" branch-rate="0.25" complexity="4"><classes>
      <class name="Example.Owner" filename="Owner.cs" line-rate="1" branch-rate="0.25" complexity="4"><methods>
      <method name="First" signature="(System.Boolean)" line-rate="1" branch-rate="0.5" complexity="2"><lines>
      <line number="10" hits="1" branch="True" condition-coverage="50% (1/2)"><conditions><condition number="2" type="jump" coverage="50%"/></conditions></line></lines></method>
      <method name="Second" signature="(System.Boolean)" line-rate="1" branch-rate="0" complexity="2"><lines>
      <line number="10" hits="1" branch="True" condition-coverage="0% (0/2)"><conditions><condition number="2" type="jump" coverage="0%"/></conditions></line></lines></method>
      </methods><lines>
      <line number="10" hits="1" branch="True" condition-coverage="50% (1/2)"><conditions><condition number="2" type="jump" coverage="50%"/></conditions></line>
      <line number="10" hits="1" branch="True" condition-coverage="0% (0/2)"><conditions><condition number="2" type="jump" coverage="0%"/></conditions></line>
      </lines></class></classes></package></packages></coverage>''')


class CoverageUnionTests(unittest.TestCase):
    def test_projection_preserves_method_line_denominator_and_updates_complete_edge_union(self):
        source = template()
        merged = coverage_union.merge_modules(modules(), modules(first=(0, 1), second=(0, 1)))
        projected = coverage_union.project_cobertura(merged, source)

        self.assertEqual("0.75", projected.get("branch-rate"))
        self.assertEqual("3", projected.get("branches-covered"))
        self.assertEqual("4", projected.get("branches-valid"))
        self.assertEqual("2", projected.get("lines-valid"))
        self.assertEqual(["100% (2/2)", "50% (1/2)"],
                         [e.get("condition-coverage") for e in projected.findall("./packages/package/classes/class/lines/line")])
        self.assertEqual("0.25", source.get("branch-rate"))

    def test_raw_async_class_and_never_covered_edges_retain_security_worst_line(self):
        value = modules(first=(1, 1), second=(0, 0))
        records = list(methods(value).values())
        classes = value["Example.dll"]["/source/Owner.cs"]
        classes.clear()
        classes["Example.ApiKeyDigestCache"] = {"System.Int32 Example.ApiKeyDigestCache::First(System.Boolean)": records[0]}
        classes["Example.ApiKeyDigestCache/<Async>d__1"] = {"System.Void Example.ApiKeyDigestCache/<Async>d__1::MoveNext(System.Boolean)": records[1]}
        source = template()
        container = source.find(".//classes")
        outer = container[0]
        nested = copy.deepcopy(outer)
        outer.set("name", "Example.ApiKeyDigestCache")
        nested.set("name", "Example.ApiKeyDigestCache/<Async>d__1")
        outer.find("methods").remove(outer.find("methods")[1])
        outer.find("lines").remove(outer.find("lines")[1])
        nested.find("methods").remove(nested.find("methods")[0])
        nested.find("lines").remove(nested.find("lines")[0])
        nested.find(".//method").set("name", "MoveNext")
        container.append(nested)
        projected = coverage_union.project_cobertura(value, source)
        self.assertEqual(["100% (2/2)", "0% (0/2)"],
                         [c.find("./lines/line").get("condition-coverage") for c in projected.findall(".//class")])
        self.assertEqual("4", projected.get("branches-valid"))
        self.assertEqual("2", projected.get("branches-covered"))
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "coverage.xml"
            for covered, expected in [(False, 1), (True, 0)]:
                if covered:
                    for edge in records[1]["Branches"]:
                        edge["Hits"] = 1
                ET.ElementTree(coverage_union.project_cobertura(value, source)).write(path)
                with mock.patch.object(coverage_threshold, "SECURITY_TYPES", {"ApiKeyDigestCache"}), \
                     mock.patch.object(coverage_threshold, "REPORTED_ASSEMBLY_LINE_FLOORS", {}), \
                     mock.patch.dict("os.environ", {"COVERAGE_LINE_TARGET": "0", "COVERAGE_BRANCH_TARGET": "0"}), \
                     contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()) as output:
                    self.assertEqual(expected, coverage_threshold.main([str(path)]))
                    if not covered:
                        self.assertIn("ApiKeyDigestCache", output.getvalue())
                        self.assertIn("0.00%", output.getvalue())

    def test_single_input_oracle_mismatch_and_missing_duplicate_line_are_refused(self):
        source = template()
        coverage_union.validate_pair(modules(), source)
        source.set("branches-valid", "3")
        with self.assertRaises(ValueError):
            coverage_union.validate_pair(modules(), source)

        source = template()
        lines = source.find("./packages/package/classes/class/lines")
        lines.remove(list(lines)[1])
        with self.assertRaises(ValueError):
            coverage_union.project_cobertura(modules(), source)

    def test_unconsumed_xml_cannot_inject_a_security_type_or_branch_occurrence(self):
        for location in ["root", "conditions", "class"]:
            with self.subTest(location=location):
                source = template()
                if location == "root":
                    extra = ET.SubElement(source, "unexpected")
                    ET.SubElement(extra, "class", name="Example.ApiKeyEndpointFilter", filename="forged.cs", **{"branch-rate": "1"})
                elif location == "conditions":
                    extra = source.find(".//conditions")
                    ET.SubElement(extra, "line", number="999", hits="1", **{"condition-coverage": "100% (2/2)"})
                else:
                    source.find(".//class").set("future-identity", "unchecked")
                with self.assertRaises(ValueError):
                    coverage_union.validate_pair(modules(), source)

    def test_producer_integer_ranges_and_merged_hit_overflow_are_refused(self):
        for field in ["Line", "Offset", "EndOffset", "Path", "Hits", "Ordinal", "line-hits", "line-key"]:
            with self.subTest(field=field):
                value = modules()
                method = next(iter(methods(value).values()))
                if field == "line-hits":
                    method["Lines"]["10"] = 2**31
                elif field == "line-key":
                    method["Lines"] = {str(2**31): 0}
                else:
                    method["Branches"][0][field] = 2**32 if field == "Ordinal" else 2**31
                with self.assertRaises(ValueError):
                    coverage_union.merge_modules(value, value)

        with self.assertRaises(ValueError):
            coverage_union.merge_modules(modules(first=(2**31 - 1, 0)), modules())

        value = modules()
        next(iter(methods(value).values()))["Lines"]["10"] = 2**31 - 1
        with self.assertRaises(ValueError):
            coverage_union.merge_modules(value, modules())

    def test_exact_producer_maxima_are_admitted_without_clamping(self):
        for field in ["Line", "Offset", "EndOffset", "Path", "Hits", "Ordinal", "line-hits", "line-key"]:
            with self.subTest(field=field):
                value = modules()
                method = next(iter(methods(value).values()))
                maximum = 2**32 - 1 if field == "Ordinal" else 2**31 - 1
                if field == "line-hits":
                    method["Lines"]["10"] = maximum
                elif field == "line-key":
                    method["Lines"] = {str(maximum): 0}
                else:
                    method["Branches"][0][field] = maximum
                coverage_union.validate_modules(value)
                zero = copy.deepcopy(value)
                for record in methods(zero).values():
                    record["Lines"] = {line: 0 for line in record["Lines"]}
                    for edge in record["Branches"]:
                        edge["Hits"] = 0
                self.assertEqual(value, coverage_union.merge_modules(value, zero))

    def test_observed_generic_method_display_collision_uses_footprint_and_refuses_ambiguity(self):
        value = modules()
        original = list(methods(value).values())
        methods(value).clear()
        # Actual collector collision shape: Inert() and Inert<T>() have the same XML
        # display name/signature but distinct source footprints.
        methods(value)["System.Void Example.Owner::Inert()"] = original[0]
        methods(value)["Example.Result`1<T> Example.Owner::Inert()"] = original[1]
        original[1]["Lines"] = {"11": 1}
        for edge in original[1]["Branches"]:
            edge["Line"] = 11
        source = template()
        for method in source.findall(".//method"):
            method.set("name", "Inert")
            method.set("signature", "()")
        source.findall(".//method")[1].find("./lines/line").set("number", "11")
        source.find("./packages/package/classes/class/lines")[1].set("number", "11")
        coverage_union.validate_pair(value, source)
        owners = source.find(".//methods")
        owners[:] = list(reversed(list(owners)))
        copies = source.find("./packages/package/classes/class/lines")
        copies[:] = list(reversed(list(copies)))
        coverage_union.validate_pair(value, source)
        original[1]["Lines"] = {"10": 1}
        for edge in original[1]["Branches"]:
            edge["Line"] = 10
        with self.assertRaises(ValueError):
            for line in source.findall(".//line"):
                line.set("number", "10")
            coverage_union.project_cobertura(value, source)

    def test_complementary_edges_are_covered_once_with_both_original_method_identities(self):
        left = modules(first=(1, 0), second=(0, 0))
        right = modules(first=(0, 0), second=(0, 3))
        original = copy.deepcopy(left)

        merged = coverage_union.merge_modules(left, right)

        self.assertEqual([1, 0], [b["Hits"] for b in methods(merged)[next(iter(methods(merged)))]["Branches"]])
        self.assertEqual([0, 3], [b["Hits"] for b in list(methods(merged).values())[1]["Branches"]])
        self.assertEqual(4, sum(len(m["Branches"]) for m in methods(merged).values()))
        self.assertEqual(2, sum(b["Hits"] > 0 for m in methods(merged).values() for b in m["Branches"]))
        self.assertEqual(original, left)
        self.assertEqual(merged, coverage_union.merge_modules(right, left))

    def test_matching_edges_add_hits_but_changed_ordinal_is_not_the_same_edge(self):
        left = modules()
        right = modules(first=(2, 1))
        merged = coverage_union.merge_modules(left, right)
        self.assertEqual([3, 1], [b["Hits"] for b in next(iter(methods(merged).values()))["Branches"]])

        next(iter(methods(right).values()))["Branches"][0]["Ordinal"] = 99
        with self.assertRaises(ValueError):
            coverage_union.merge_modules(left, right)

    def test_boolean_negative_and_fractional_hits_are_not_integer_evidence(self):
        for invalid in [True, False, -1, 0.5, "1", None]:
            for field in ["line", "branch"]:
                with self.subTest(invalid=invalid, field=field):
                    value = modules()
                    method = next(iter(methods(value).values()))
                    if field == "line":
                        method["Lines"]["10"] = invalid
                    else:
                        method["Branches"][0]["Hits"] = invalid
                    with self.assertRaises(ValueError):
                        coverage_union.merge_modules(value, modules())

    def test_duplicate_json_property_and_duplicate_full_branch_identity_are_refused(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "coverage.json"
            path.write_text('{"Example.dll":{},"Example.dll":{}}', encoding="utf-8")
            with self.assertRaises(ValueError):
                coverage_union.load_modules(path)

        value = modules()
        method = next(iter(methods(value).values()))
        method["Branches"].append(copy.deepcopy(method["Branches"][0]))
        with self.assertRaises(ValueError):
            coverage_union.merge_modules(value, modules())

    def test_receipt_union_binds_two_joined_lanes_and_refuses_tampered_or_incomplete_proof(self):
        import hashlib
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory).resolve()
            def recorded(path):
                return {"path": str(path), "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
            settings = root / "coverage.runsettings"
            settings.write_text("settings")
            binary = root / "Tests.dll"
            binary.write_bytes(b"same build")
            lanes = []
            for name, data in [("resource", modules()), ("remainder", modules(first=(0, 1)))]:
                lane = root / name
                reports = lane / "deployment/In/machine"
                reports.mkdir(parents=True)
                json_path = reports / "coverage.json"
                xml_path = reports / "coverage.cobertura.xml"
                json_path.write_text(json.dumps(data))
                ET.ElementTree(coverage_union.project_cobertura(data, template())).write(xml_path)
                trx = lane / "runtime.trx"
                trx.write_text('<TestRun><TestSettings><Deployment runDeploymentRoot="deployment"/></TestSettings><Collector uri="datacollector://microsoft/CoverletCodeCoverage/1.0"><UriAttachment><A href="machine/coverage.json"/></UriAttachment><UriAttachment><A href="machine/coverage.cobertura.xml"/></UriAttachment></Collector></TestRun>')
                lanes.append({"name": name, "joined": True, "exitCode": 0, "trx": recorded(trx),
                              "coverage": {"json": recorded(json_path), "cobertura": recorded(xml_path)}})
            receipt = {"schemaVersion": 1, "status": "passed", "sourceSha": "a" * 40, "sourceState": {"sha": "a" * 40},
                       "configuration": "Debug", "testAssembly": str(binary),
                       "buildIdentity": {"files": {str(binary): recorded(binary)["sha256"]}, "provenance": "runner-built"},
                       "coverageSettings": recorded(settings), "lanes": lanes}
            manifest = root / "runtime-summary.json"
            manifest.write_text(json.dumps(receipt))
            output = root / "merged.xml"
            proof = coverage_union.merge_receipt(manifest, output)
            self.assertEqual("passed", proof["status"])
            self.assertEqual("2", ET.parse(output).getroot().get("branches-covered"))
            manifest.write_text(json.dumps(receipt))
            original_load = coverage_union.load_modules
            def mutate_receipt(path):
                data = original_load(path)
                changed = copy.deepcopy(receipt)
                changed["sourceSha"] = "b" * 40
                manifest.write_text(json.dumps(changed))
                return data
            with mock.patch.object(coverage_union, "load_modules", side_effect=mutate_receipt):
                with self.assertRaises(ValueError):
                    coverage_union.merge_receipt(manifest, root / "receipt-race.xml")
            self.assertFalse((root / "receipt-race.xml").exists())

            for mutation in ["joined", "exit", "missing", "hash", "build", "settings", "reuse", "source"]:
                changed = copy.deepcopy(receipt)
                if mutation == "joined": changed["lanes"][1]["joined"] = False
                elif mutation == "exit": changed["lanes"][1]["exitCode"] = 1
                elif mutation == "missing": changed["lanes"].pop()
                elif mutation == "hash": changed["lanes"][0]["coverage"]["json"]["sha256"] = "0" * 64
                elif mutation == "build": changed["buildIdentity"]["files"][str(binary)] = "0" * 64
                elif mutation == "settings": changed["coverageSettings"]["sha256"] = "0" * 64
                elif mutation == "source": changed["sourceState"]["sha"] = "b" * 40
                else:
                    changed["lanes"][1] = copy.deepcopy(changed["lanes"][0])
                    changed["lanes"][1]["name"] = "remainder"
                manifest.write_text(json.dumps(changed))
                with self.subTest(mutation=mutation), self.assertRaises(ValueError):
                    coverage_union.merge_receipt(manifest, root / (mutation + ".xml"))
                self.assertFalse((root / (mutation + ".xml")).exists())
            changed = copy.deepcopy(receipt)
            left = Path(changed["lanes"][0]["coverage"]["json"]["path"])
            right = Path(changed["lanes"][1]["coverage"]["json"]["path"])
            right.unlink()
            right.hardlink_to(left)
            changed["lanes"][1]["coverage"]["json"] = recorded(right)
            left_xml = Path(changed["lanes"][0]["coverage"]["cobertura"]["path"])
            right_xml = Path(changed["lanes"][1]["coverage"]["cobertura"]["path"])
            right_xml.write_bytes(left_xml.read_bytes())
            changed["lanes"][1]["coverage"]["cobertura"] = recorded(right_xml)
            manifest.write_text(json.dumps(changed))
            with self.assertRaises(ValueError):
                coverage_union.merge_receipt(manifest, root / "physical-alias.xml")
            self.assertFalse((root / "physical-alias.xml").exists())

    def test_missing_method_or_line_and_extra_shape_fields_are_refused(self):
        for mutation in ["method", "line", "field"]:
            with self.subTest(mutation=mutation):
                changed = modules()
                if mutation == "method":
                    methods(changed).pop(next(iter(methods(changed))))
                elif mutation == "line":
                    next(iter(methods(changed).values()))["Lines"]["11"] = 0
                else:
                    next(iter(methods(changed).values()))["FutureField"] = True
                with self.assertRaises(ValueError):
                    coverage_union.merge_modules(modules(), changed)


if __name__ == "__main__":
    unittest.main()
