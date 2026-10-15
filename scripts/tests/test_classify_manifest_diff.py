from __future__ import annotations

import contextlib
import hashlib
import importlib.util
import io
import re
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parents[2]

SCRIPT_PATH = REPOSITORY / "scripts" / "classify-manifest-diff.py"

SPEC = importlib.util.spec_from_file_location("classify_manifest_diff", SCRIPT_PATH)
assert SPEC is not None and SPEC.loader is not None
CLASSIFIER = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = CLASSIFIER
SPEC.loader.exec_module(CLASSIFIER)

MANIFEST_PATH = (
    REPOSITORY / "tests" / "RetroDownfall.Arcanum.Tests" / "Support" / "hosted-grimoire-producer-capsules-v1.tsv"
)

ACQUISITION = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireConnectionAdmissionGate.TryAcquireWorkLease"

GROUP = "RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.TryBeginExternalEffectGroup"


def site(
    path: str,
    callee: str = "System.Data.Common.DbCommand.ExecuteNonQueryAsync",
    kind: str = "DatabaseAccess",
    member: str = "Run",
    ordinal: str = "0",
    root: str = "Worker",
    operation: str = "Worker.ExecuteAsync",
    work: str = "-",
    effect: str = "-",
    admitted: str = "-",
) -> "CLASSIFIER.Row":
    fingerprint = hashlib.sha256((path + member + callee + ordinal).encode()).hexdigest().upper()

    return CLASSIFIER.Row(
        root,
        operation,
        path,
        "Ns." + Path(path).stem,
        member,
        "M:Ns." + Path(path).stem + "." + member,
        kind,
        callee,
        ordinal,
        fingerprint,
        work,
        effect,
        admitted,
    )


def manifest(*rows: "CLASSIFIER.Row", name: str = "m") -> "CLASSIFIER.Manifest":
    return CLASSIFIER.Manifest(list(rows), name)


def classify(committed, regenerated, edited=(), parity=None):
    labelled = CLASSIFIER.classify(committed, regenerated, frozenset(edited), parity)

    return {(item.side, item.row): item for item in labelled}


class ClassifyManifestDiffTests(unittest.TestCase):
    def test_a_capsule_id_is_the_sha256_of_the_site_columns_the_checker_hashes(self) -> None:
        # Every Work and Effect column of the committed manifest names the capsule of another row of the same manifest. That
        # holds only if the id computed here is the id the checker computed, so this pins the algorithm against the shipped file.
        reviewed = CLASSIFIER.parse_manifest(MANIFEST_PATH.read_text(encoding="utf-8"), "reviewed")

        references = [reference for row in reviewed.rows for reference in row.references]

        self.assertGreater(len(references), 1000)

        self.assertEqual([], [reference for reference in references if not reviewed.resolve(reference)])

    def test_the_capsule_id_hashes_the_six_site_columns_in_order(self) -> None:
        row = site("src/A.cs", member="Go", ordinal="3")

        evidence = "\n".join((row.source_path, row.member_identity, row.kind, row.callee, "3", row.syntax_sha256))

        self.assertEqual(hashlib.sha256(evidence.encode()).hexdigest().upper(), row.capsule_id)

    def test_an_unchanged_manifest_has_nothing_to_explain(self) -> None:
        rows = [site("src/A.cs"), site("src/B.cs", member="Other")]

        self.assertEqual({}, classify(manifest(*rows), manifest(*rows)))

    def test_a_row_whose_own_site_is_in_an_edited_source_is_a(self) -> None:
        added = site("src/Edited.cs")

        result = classify(manifest(), manifest(added), edited={"src/Edited.cs"})

        self.assertEqual("a", result[("+", added)].label)

    def test_a_removed_row_whose_own_site_is_in_an_edited_source_is_a(self) -> None:
        removed = site("src/Edited.cs")

        result = classify(manifest(removed), manifest(), edited={"src/Edited.cs"})

        self.assertEqual("a", result[("-", removed)].label)

    def test_a_row_whose_work_capsule_names_an_edited_acquisition_is_b(self) -> None:
        acquisition = site("src/Edited.cs", callee=ACQUISITION, kind="EffectFrontier", member="Acquire")

        added = site("src/Unedited.cs", work=acquisition.capsule_id)

        result = classify(manifest(), manifest(acquisition, added), edited={"src/Edited.cs"})

        self.assertEqual("a", result[("+", acquisition)].label)

        self.assertEqual("b", result[("+", added)].label)

    def test_a_row_whose_effect_capsule_names_an_edited_group_is_b(self) -> None:
        group = site("src/Edited.cs", callee=GROUP, kind="EffectFrontier", member="Begin")

        added = site("src/Unedited.cs", effect=group.capsule_id)

        result = classify(manifest(), manifest(group, added), edited={"src/Edited.cs"})

        self.assertEqual("b", result[("+", added)].label)

    def test_a_frame_that_names_an_unedited_site_does_not_explain_a_row(self) -> None:
        acquisition = site("src/Unedited.cs", callee=ACQUISITION, kind="EffectFrontier", member="Acquire")

        added = site("src/Other.cs", work=acquisition.capsule_id)

        result = classify(manifest(acquisition), manifest(acquisition, added), edited={"src/Edited.cs"})

        self.assertEqual("d", result[("+", added)].label)

    def test_the_removed_half_of_a_changed_row_is_b_through_the_frame_of_its_added_half(self) -> None:
        old_acquisition = site("src/Unedited.cs", callee=ACQUISITION, kind="EffectFrontier", member="Acquire")

        new_acquisition = site("src/Edited.cs", callee=ACQUISITION, kind="EffectFrontier", member="Acquire2")

        before = site("src/Other.cs", work=old_acquisition.capsule_id)

        after = site("src/Other.cs", work=new_acquisition.capsule_id)

        result = classify(
            manifest(old_acquisition, before),
            manifest(old_acquisition, new_acquisition, after),
            edited={"src/Edited.cs"},
        )

        self.assertEqual("b", result[("-", before)].label)

        self.assertEqual("b", result[("+", after)].label)

    def test_a_row_that_only_gains_a_lease_marker_is_b_format_after_normalising_it_away(self) -> None:
        before = site("src/Unedited.cs", callee="Ns.Gate.Begin")

        after = before._replace(callee="Ns.Gate.Begin@lease:src/Unedited.cs:12:4")

        result = classify(manifest(before), manifest(after))

        self.assertEqual("b-format", result[("-", before)].label)

        self.assertEqual("b-format", result[("+", after)].label)

    def test_a_marker_change_that_also_changes_something_else_is_not_b_format(self) -> None:
        before = site("src/Unedited.cs", callee="Ns.Gate.Begin")

        after = before._replace(callee="Ns.Gate.Begin@lease:x", member_identity="M:Ns.Other")

        result = classify(manifest(before), manifest(after))

        self.assertEqual("d", result[("-", before)].label)

        self.assertEqual("d", result[("+", after)].label)

    def test_a_row_no_class_explains_is_d_and_makes_the_run_fail(self) -> None:
        added = site("src/Unedited.cs")

        result = classify(manifest(), manifest(added), edited={"src/Edited.cs"})

        self.assertEqual("d", result[("+", added)].label)

        with tempfile.TemporaryDirectory() as directory:
            old = Path(directory) / "old.tsv"
            new = Path(directory) / "new.tsv"
            edited = Path(directory) / "edited.txt"

            write_manifest(old, [])
            write_manifest(new, [added])
            edited.write_text("src/Edited.cs\n", encoding="utf-8")

            output = io.StringIO()

            with contextlib.redirect_stdout(output):
                status = CLASSIFIER.main(
                    ["--committed", str(old), "--regenerated", str(new), "--edited-sources", str(edited)]
                )

            self.assertEqual(1, status)

            self.assertIn("unexplained rows: 1", output.getvalue())

    def test_a_run_with_every_row_explained_succeeds_and_writes_the_report(self) -> None:
        kept = site("src/Kept.cs", member="Kept")

        added = site("src/Edited.cs")

        with tempfile.TemporaryDirectory() as directory:
            old = Path(directory) / "old.tsv"
            new = Path(directory) / "new.tsv"
            edited = Path(directory) / "edited.txt"
            report = Path(directory) / "report.tsv"
            summary = Path(directory) / "summary.json"

            write_manifest(old, [kept])
            write_manifest(new, [kept, added])
            edited.write_text("src/Edited.cs\n", encoding="utf-8")

            output = io.StringIO()

            with contextlib.redirect_stdout(output):
                status = CLASSIFIER.main(
                    [
                        "--committed", str(old),
                        "--regenerated", str(new),
                        "--edited-sources", str(edited),
                        "--report", str(report),
                        "--json", str(summary),
                    ]
                )

            self.assertEqual(0, status)

            lines = report.read_text(encoding="utf-8").split("\n")

            self.assertTrue(lines[0].startswith("class\trule\tstage\tside\tdetail\t"))

            self.assertTrue(lines[1].startswith("a\t\t\t+\t\t"))

            self.assertIn('"unexplained": 0', summary.read_text(encoding="utf-8"))

    def test_a_frame_that_resolves_to_no_site_is_refused_not_explained(self) -> None:
        added = site("src/Edited.cs", work="0" * 64)

        result = classify(manifest(), manifest(added), edited={"src/Edited.cs"})

        self.assertEqual("d", result[("+", added)].label)

        self.assertIn("unresolved", result[("+", added)].detail)

    def test_a_row_the_base_source_already_yields_under_the_same_checker_is_the_checkers(self) -> None:
        # The new checker over the base source (the parity manifest) renders the row, the committed manifest does not: no
        # source edit can have introduced it, even though its file was edited since.
        added = site("src/Edited.cs")

        result = classify(manifest(), manifest(added), edited={"src/Edited.cs"}, parity=manifest(added))

        self.assertEqual("checker", result[("+", added)].stage)

        self.assertEqual("d", result[("+", added)].label)

    def test_a_row_the_base_source_does_not_yield_is_the_sources(self) -> None:
        added = site("src/Edited.cs")

        result = classify(manifest(), manifest(added), edited={"src/Edited.cs"}, parity=manifest())

        self.assertEqual("source", result[("+", added)].stage)

        self.assertEqual("a", result[("+", added)].label)

    def test_an_unedited_site_and_frame_reached_only_by_the_new_source_is_r(self) -> None:
        added = site("src/Unedited.cs")

        result = classify(manifest(), manifest(added), edited={"src/Edited.cs"}, parity=manifest())

        self.assertEqual("r", result[("+", added)].label)

    def test_a_removed_row_the_base_source_still_yields_is_the_sources_and_one_it_does_not_is_the_checkers(self) -> None:
        by_source = site("src/Unedited.cs", member="BySource")

        by_checker = site("src/Unedited.cs", member="ByChecker")

        result = classify(
            manifest(by_source, by_checker),
            manifest(),
            edited={"src/Edited.cs"},
            parity=manifest(by_source),
        )

        self.assertEqual("source", result[("-", by_source)].stage)

        self.assertEqual("r", result[("-", by_source)].label)

        self.assertEqual("checker", result[("-", by_checker)].stage)

        self.assertEqual("d", result[("-", by_checker)].label)

    def test_a_checker_row_is_c_when_a_named_rule_matches_it_and_d_when_none_does(self) -> None:
        matched = site("src/Rule.cs", member="Matched")

        unmatched = site("src/Rule.cs", member="Unmatched")

        rule = CLASSIFIER.Rule("test-rule", "Test lane", "+-", "matches the member Matched", lambda row, side, context: row.member == "Matched")

        original = CLASSIFIER.RULES

        CLASSIFIER.RULES = (rule,)

        try:
            result = classify(manifest(), manifest(matched, unmatched), parity=manifest(matched, unmatched))
        finally:
            CLASSIFIER.RULES = original

        self.assertEqual("c", result[("+", matched)].label)

        self.assertEqual("test-rule", result[("+", matched)].rule)

        self.assertEqual("d", result[("+", unmatched)].label)

    def test_without_parity_a_named_rule_explains_only_a_row_that_no_edit_explains(self) -> None:
        matched = site("src/Rule.cs", member="Matched")

        edited = site("src/Edited.cs", member="Matched")

        rule = CLASSIFIER.Rule("test-rule", "Test lane", "+-", "matches the member Matched", lambda row, side, context: row.member == "Matched")

        original = CLASSIFIER.RULES

        CLASSIFIER.RULES = (rule,)

        try:
            result = classify(manifest(), manifest(matched, edited), edited={"src/Edited.cs"})
        finally:
            CLASSIFIER.RULES = original

        self.assertEqual("c", result[("+", matched)].label)

        self.assertEqual("a", result[("+", edited)].label)

    def test_every_rule_has_a_unique_name_a_lane_a_side_and_a_summary(self) -> None:
        names = [rule.name for rule in CLASSIFIER.RULES]

        self.assertEqual(5, len(names))

        self.assertEqual(len(names), len(set(names)))

        for rule in CLASSIFIER.RULES:
            self.assertTrue(rule.lane.strip(), rule.name)

            self.assertTrue(rule.summary.strip(), rule.name)

            self.assertIn(rule.side, ("+", "-", "+-"), rule.name)

    def test_the_interceptor_rule_matches_the_callback_types_and_nothing_else(self) -> None:
        context = CLASSIFIER.Context(manifest(), manifest(), None)

        rule = rule_named("interceptor-and-state-handler-callbacks")

        callback = site("src/A.cs")._replace(
            enclosing_type="RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantConnectionEnrolmentInterceptor"
        )

        self.assertTrue(rule.matches(callback, "+", context))

        self.assertFalse(rule.matches(callback, "-", context))

        self.assertFalse(rule.matches(site("src/A.cs"), "+", context))

    def test_the_base_constructor_rule_matches_the_framework_stream_constructors_only(self) -> None:
        context = CLASSIFIER.Context(manifest(), manifest(), None)

        rule = rule_named("base-constructor-initializers")

        for callee in ("System.IO.FileStream..ctor", "System.IO.Stream..ctor"):
            self.assertTrue(rule.matches(site("src/A.cs", kind="FileSystemEffect", callee=callee), "+", context), callee)

        self.assertFalse(rule.matches(site("src/A.cs", kind="FileSystemEffect", callee="System.IO.File.Delete"), "+", context))

        self.assertFalse(rule.matches(site("src/A.cs", kind="DatabaseAccess", callee="System.IO.FileStream..ctor"), "+", context))

    def test_the_dispatch_rule_matches_the_reviewed_types_on_the_added_side_only(self) -> None:
        context = CLASSIFIER.Context(manifest(), manifest(), None)

        rule = rule_named("runtime-type-dispatch")

        dispatched = site("src/A.cs")._replace(enclosing_type="RetroDownfall.Arcanum.Infrastructure.Security.OwnerOnlyAtomicFile")

        self.assertTrue(rule.matches(dispatched, "+", context))

        self.assertFalse(rule.matches(dispatched, "-", context))

        self.assertFalse(rule.matches(site("src/A.cs"), "+", context))

    def test_the_lease_rule_matches_an_effect_the_checker_withdrew_from_an_unchanged_site_and_work(self) -> None:
        rule = rule_named("lease-receiver-proof")

        before = site("src/A.cs", work="a" * 64, effect="b" * 64)

        withdrawn = before._replace(effect="-")

        self.assertTrue(rule.matches(before, "-", CLASSIFIER.Context(manifest(before), manifest(), manifest(withdrawn))))

        self.assertTrue(rule.matches(withdrawn, "+", CLASSIFIER.Context(manifest(before), manifest(withdrawn), None)))

        # another Work is another frame, not a withdrawn Effect
        changed = withdrawn._replace(work="c" * 64)

        self.assertFalse(rule.matches(before, "-", CLASSIFIER.Context(manifest(before), manifest(), manifest(changed))))

        # the same row kept by the parity traversal is not withdrawn
        self.assertFalse(rule.matches(before, "-", CLASSIFIER.Context(manifest(before), manifest(), manifest(before))))

        # no parity manifest, no proof
        self.assertFalse(rule.matches(before, "-", CLASSIFIER.Context(manifest(before), manifest(), None)))

    def test_the_lease_rule_matches_a_group_site_the_parity_traversal_no_longer_has(self) -> None:
        rule = rule_named("lease-receiver-proof")

        group = site("src/A.cs", callee=GROUP, kind="EffectFrontier", member="Begin", work="a" * 64, effect="b" * 64)

        self.assertTrue(rule.matches(group, "-", CLASSIFIER.Context(manifest(group), manifest(), manifest())))

        self.assertFalse(rule.matches(group, "-", CLASSIFIER.Context(manifest(group), manifest(), None)))

        self.assertFalse(rule.matches(group._replace(callee=ACQUISITION), "-", CLASSIFIER.Context(manifest(group), manifest(), manifest())))

    def test_the_stream_disposal_rule_matches_a_stream_dispose_the_parity_traversal_no_longer_has(self) -> None:
        rule = rule_named("stream-disposal-by-origin")

        dispose = site("src/A.cs", kind="FileSystemEffect", callee="System.IO.Stream.Dispose")

        self.assertTrue(rule.matches(dispose, "-", CLASSIFIER.Context(manifest(dispose), manifest(), manifest())))

        self.assertFalse(rule.matches(dispose, "-", CLASSIFIER.Context(manifest(dispose), manifest(), manifest(dispose))))

        self.assertFalse(rule.matches(dispose, "+", CLASSIFIER.Context(manifest(), manifest(dispose), manifest())))

        self.assertFalse(
            rule.matches(dispose._replace(callee="System.IO.File.Delete"), "-", CLASSIFIER.Context(manifest(dispose), manifest(), manifest()))
        )

    def test_every_type_a_rule_names_is_declared_in_the_product_source(self) -> None:
        declared = declared_types()

        named = set(CLASSIFIER.INTERCEPTOR_CALLBACK_TYPES) | set(CLASSIFIER.SLOT_DISPATCH_TYPES)

        self.assertEqual([], sorted(name for name in named if name not in declared))

    def test_the_interceptor_rule_names_an_ef_interceptor_and_the_types_it_calls(self) -> None:
        sources = declaring_sources()

        enrolment = sources["RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantConnectionEnrolmentInterceptor"]

        self.assertRegex(enrolment, r"class\s+CovenantConnectionEnrolmentInterceptor\s*:\s*DbConnectionInterceptor\b")

        self.assertRegex(
            sources["RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantSqliteConnectionInitializer"],
            r"class\s+CovenantSqliteConnectionInitializer\s*:\s*ICovenantSqliteConnectionInitializer\b",
        )

        self.assertRegex(
            sources["RetroDownfall.Arcanum.Infrastructure.Data.Covenant.CovenantConnectionDrain"],
            r"class\s+CovenantConnectionDrain\s*:\s*ICovenantConnectionDrain\b",
        )

    def test_the_dispatch_rule_names_types_behind_a_slot_the_source_still_declares(self) -> None:
        text = "\n".join(path.read_text(encoding="utf-8") for path in SOURCE_ROOT.rglob("*.cs"))

        for declaration in (
            r"public\s+interface\s+ISecretStore\b",
            r"public\s+interface\s+ISagaMemoryStore\b",
            r"public\s+interface\s+IGrimoireRepository\b",
            r"abstract\s+class\s+CredentialMirror\b",
            r"class\s+ProtectedFileCredentialMirror\b",
            r"class\s+DataProtectionStoreCredentialMirror\b",
        ):
            self.assertRegex(text, declaration)

    def test_a_manifest_with_another_header_or_row_width_is_refused(self) -> None:
        good = CLASSIFIER.COLUMNS

        with self.assertRaises(CLASSIFIER.ManifestError):
            CLASSIFIER.parse_manifest("# something else\n" + good + "\n", "m")

        with self.assertRaises(CLASSIFIER.ManifestError):
            CLASSIFIER.parse_manifest(CLASSIFIER.HEADER + "\n# other columns\n", "m")

        with self.assertRaises(CLASSIFIER.ManifestError):
            CLASSIFIER.parse_manifest(CLASSIFIER.HEADER + "\n" + good + "\na\tb\n", "m")

    def test_the_edited_sources_are_the_src_paths_git_reports_between_two_revisions(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            repo = Path(directory)

            def git(*arguments: str) -> str:
                return subprocess.run(
                    ["git", "-C", directory, "-c", "user.name=t", "-c", "user.email=t@t", *arguments],
                    check=True,
                    capture_output=True,
                    text=True,
                ).stdout.strip()

            git("init", "-q")
            (repo / "src").mkdir()
            (repo / "tests").mkdir()
            (repo / "src" / "Kept.cs").write_text("a", encoding="utf-8")
            (repo / "src" / "Edited.cs").write_text("a", encoding="utf-8")
            git("add", ".")
            git("commit", "-q", "-m", "base")
            base = git("rev-parse", "HEAD")
            (repo / "src" / "Edited.cs").write_text("b", encoding="utf-8")
            (repo / "src" / "Added.cs").write_text("b", encoding="utf-8")
            (repo / "tests" / "Ignored.cs").write_text("b", encoding="utf-8")
            git("add", ".")
            git("commit", "-q", "-m", "head")

            self.assertEqual(
                frozenset({"src/Edited.cs", "src/Added.cs"}),
                CLASSIFIER.edited_sources_from_git(directory, base, "HEAD"),
            )

    def test_an_unframed_provider_call_the_regeneration_adds_violates_the_invariants(self) -> None:
        framed = site("src/A.cs", kind="ProviderCall", work="a" * 64, effect="b" * 64)

        unframed = site("src/B.cs", kind="ProviderCall", member="Other")

        existing = site("src/C.cs", kind="ProviderCall", member="Lifecycle")

        violations = {
            invariant.name: invariant.violations
            for invariant in CLASSIFIER.check_invariants(manifest(existing), manifest(existing, framed, unframed))
        }

        self.assertEqual(1, len(violations["provider-call-frames"]))

        self.assertIn("Other", violations["provider-call-frames"][0])

        self.assertEqual([], violations["new-roots"])

    def test_a_new_root_that_is_not_a_long_rest_root_violates_the_invariants(self) -> None:
        old = site("src/A.cs")

        foreign = site("src/B.cs", root="Other", operation="Other.ExecuteAsync")

        longrest = site(
            "src/C.cs",
            root="LongRestSweepHostedService",
            operation="Ns.LongRestSweepHostedService.ExecuteAsync",
        )

        violations = {
            invariant.name: invariant.violations
            for invariant in CLASSIFIER.check_invariants(manifest(old), manifest(old, foreign, longrest))
        }

        self.assertEqual(["Other|Other.ExecuteAsync"], violations["new-roots"])

    def test_a_root_lease_formal_without_a_handoff_binding_violates_the_invariants(self) -> None:
        formal = site(
            "src/Handoff.cs",
            kind="EffectFrontier",
            callee="RetroDownfall.Arcanum.Infrastructure.Data.IGrimoireWorkLease.RootLeaseFormal",
            root="LongRestSweepHostedService",
            operation="Ns.Maintenance.ProcessAdmittedAsync",
        )

        binding = site(
            "src/Worker.cs",
            kind="LeaseHandoff",
            callee="Ns.Maintenance.ProcessAdmittedAsync",
            root="LongRestSweepHostedService",
            operation="Ns.LongRestSweepHostedService.ExecuteAsync",
        )

        unbound = {i.name: i.violations for i in CLASSIFIER.check_invariants(manifest(), manifest(formal))}

        bound = {i.name: i.violations for i in CLASSIFIER.check_invariants(manifest(), manifest(formal, binding))}

        self.assertEqual(1, len(unbound["handoff-binding"]))

        self.assertEqual([], bound["handoff-binding"])


    def test_a_long_rest_admission_of_another_kind_or_a_provider_call_outside_the_handoff_root_violates_the_invariants(self) -> None:
        wrong_kind = site(
            "src/Worker.cs",
            kind="EffectFrontier",
            callee=ACQUISITION,
            root="LongRestSweepHostedService",
            operation="Ns.LongRestSweepHostedService.ExecuteAsync",
            admitted="BatchProcessing",
        )

        stray = site(
            "src/Worker.cs",
            kind="ProviderCall",
            member="Stray",
            root="LongRestSweepHostedService",
            operation="Ns.LongRestSweepHostedService.ExecuteAsync",
            work="a" * 64,
            effect="b" * 64,
        )

        violations = {i.name: i.violations for i in CLASSIFIER.check_invariants(manifest(), manifest(wrong_kind, stray))}

        self.assertEqual(2, len(violations["long-rest-structure"]))


SOURCE_ROOT = REPOSITORY / "src"

TYPE_DECLARATION = re.compile(r"\b(?:class|struct|record|interface)\s+([A-Za-z_][A-Za-z0-9_]*)")

NAMESPACE_DECLARATION = re.compile(r"^\s*namespace\s+([A-Za-z0-9_.]+)\s*;", re.MULTILINE)


def rule_named(name: str) -> "CLASSIFIER.Rule":
    return next(rule for rule in CLASSIFIER.RULES if rule.name == name)


def declaring_sources() -> dict:
    """Fully qualified type name to the text of the file that declares it, for every top-level type under src/."""
    sources = {}

    for path in SOURCE_ROOT.rglob("*.cs"):
        text = path.read_text(encoding="utf-8")

        namespace = NAMESPACE_DECLARATION.search(text)

        if namespace is None:
            continue

        for declared in TYPE_DECLARATION.findall(text):
            sources.setdefault(namespace.group(1) + "." + declared, text)

    return sources


def declared_types() -> set:
    return set(declaring_sources())


def write_manifest(path: Path, rows) -> None:
    lines = [CLASSIFIER.HEADER, CLASSIFIER.COLUMNS, *("\t".join(row) for row in sorted(rows))]

    path.write_text("\n".join(lines) + "\n", encoding="utf-8")


if __name__ == "__main__":
    unittest.main()
