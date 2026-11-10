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

# a root the reviewed set of Work-less ProviderCall roots names
PID_ROOT = "PidFileService"

PID_OPERATION = "RetroDownfall.Arcanum.Infrastructure.Hosting.PidFileService.StartAsync"


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


def classify(committed, regenerated, edited=(), parity=None, sources=None):
    labelled = CLASSIFIER.classify(committed, regenerated, frozenset(edited), parity, sources)

    return {(item.side, item.row): item for item in labelled}


class FakeSources:
    """The text of source files at the base and at the head, as a test states them."""

    def __init__(self, base=None, head=None) -> None:
        self.base_texts = base or {}
        self.head_texts = head or {}

    def base(self, path):
        return self.base_texts.get(path)

    def head(self, path):
        return self.head_texts.get(path)


@contextlib.contextmanager
def patched(name, value):
    original = getattr(CLASSIFIER, name)

    setattr(CLASSIFIER, name, value)

    try:
        yield
    finally:
        setattr(CLASSIFIER, name, original)



def scheduler_cancellation_handoff_fixture():
    caller = "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildLocalQuestScheduler.cs"

    catalog = "tests/RetroDownfall.Arcanum.Tests/Support/HostedGrimoireProducerInventory.cs"

    prefix = "RetroDownfall.Arcanum.Infrastructure.Guild."

    rows = []

    for source, owner, member, parameters, callee, fingerprint in (
        ("GuildLocalHostPolicy.Canonical.cs", "GuildLocalHostPolicy", "OperationMatchesAsync", "Microsoft.Data.Sqlite.SqliteConnection,Microsoft.Data.Sqlite.SqliteTransaction,System.Guid,System.Guid,System.Threading.CancellationToken", "System.Data.Common.DbCommand.ExecuteScalarAsync", "8FAA042C7137E09C06B26DD33746F64FF464D3DDBF792DAACA4FAA7FAD12B6F2"),
        ("GuildSignetStore.cs", "GuildSignetStore", "ReadAsync", "Microsoft.Data.Sqlite.SqliteConnection,Microsoft.Data.Sqlite.SqliteTransaction,System.Guid,System.Boolean,System.Threading.CancellationToken", "Microsoft.Data.Sqlite.SqliteCommand.ExecuteReaderAsync", "885CC089D5C8A0D70513D923924F72E692666546DE423395648201E632DF6912"),
        ("GuildSignetStore.cs", "GuildSignetStore", "ReadAsync", "Microsoft.Data.Sqlite.SqliteConnection,Microsoft.Data.Sqlite.SqliteTransaction,System.Guid,System.Boolean,System.Threading.CancellationToken", "System.Data.Common.DbDataReader.ReadAsync", "3B2939BA3EA89FCFE52B2D385C8FBA1ACAEB53B8728660D32646CDAFF49D1F39"),
    ):
        rows.append(site("src/RetroDownfall.Arcanum.Infrastructure/Guild/" + source, callee=callee, member=member,
            root="GuildLocalQuestScheduler", operation=prefix + "GuildLocalQuestScheduler.ExecuteAsync",
            work="9BF6CCE78957077CC7AFED66E4DB844AFF6A1499F1D7D479BCF170A9171BA53C")._replace(
                enclosing_type=prefix + owner, member_identity="M:" + prefix + owner + "." + member + "(" + parameters + ")",
                syntax_sha256=fingerprint))

    base = {caller: """
private async Task RequestDisabledCancellationAsync(CancellationToken ct)
{
    await DrainCancellationAsync(scope.ServiceProvider, caller.Value, questId, ct).ConfigureAwait(false);
}
private async Task DrainCancellationAsync(IServiceProvider services, GuildCallerContext caller, Guid questId, CancellationToken ct)
{
    var recovery = await policy.CreateRecoveryAsync(caller, quest.Id, operationId, quest.SignetRevision, ct).ConfigureAwait(false);
}
""", catalog: "// No cancellation handoff root at the baseline.\n"}

    head = {caller: base[caller].replace("DrainCancellationAsync(scope.ServiceProvider", "DrainCancellationAsync(work, scope.ServiceProvider")
        .replace("DrainCancellationAsync(IServiceProvider", "DrainCancellationAsync(IGrimoireWorkLease work, IServiceProvider")
        .replace("    var recovery", "    work.MaintenanceRevocation.ThrowIfCancellationRequested();\n    var recovery"),
        catalog: '''Handoff(Operation("Guild", "GuildLocalQuestScheduler", "DrainCancellationAsync", HostedProducerAuthorityKind.OrdinaryHostedWork,
                GrimoireWorkKind.GuildStorage), "work")'''}

    return rows, FakeSources(base, head), caller, catalog

def reach_for(row, caller="src/Edited.cs", marker="Topology.Reconcile(", side="+"):
    """A reviewed call chain that reaches `row`, as the table spells one."""
    return CLASSIFIER.Reach(
        row.root_type,
        row.authority_operation_id,
        row.source_path,
        row.member,
        row.callee,
        side,
        caller,
        marker,
        "the edited caller now calls the member that holds the site",
    )


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

    def test_an_unedited_row_only_the_source_added_is_d_when_no_reviewed_call_chain_reaches_it(self) -> None:
        # Nothing in the diff says why the row is there: the tool does not infer a call chain, so the row is unexplained.
        added = site("src/Unedited.cs")

        result = classify(manifest(), manifest(added), edited={"src/Edited.cs"}, parity=manifest())

        self.assertEqual("source", result[("+", added)].stage)

        self.assertEqual("d", result[("+", added)].label)

        self.assertIn("no reviewed call chain", result[("+", added)].detail)

    def test_an_unedited_row_the_edited_source_no_longer_yields_is_d_when_no_reviewed_call_chain_reaches_it(self) -> None:
        # A row in the committed and the parity manifests that the regeneration silently lost, in a file nobody edited.
        lost = site("src/Unedited.cs", member="Lost")

        result = classify(manifest(lost), manifest(), edited={"src/Edited.cs"}, parity=manifest(lost))

        self.assertEqual("source", result[("-", lost)].stage)

        self.assertEqual("d", result[("-", lost)].label)

    def test_a_reviewed_call_chain_whose_call_is_new_at_the_head_explains_the_row_as_r(self) -> None:
        added = site("src/Unedited.cs", member="ProveAbsent")

        sources = FakeSources(base={"src/Edited.cs": "void Restore() { Other(); }"}, head={"src/Edited.cs": "void Restore() { Topology.Reconcile(x); }"})

        with patched("REACHED_THROUGH", (reach_for(added),)):
            result = classify(manifest(), manifest(added), edited={"src/Edited.cs"}, parity=manifest(), sources=sources)

        self.assertEqual("r", result[("+", added)].label)

        self.assertIn("edited caller", result[("+", added)].detail)

    def test_a_reviewed_call_chain_explains_a_removed_row_only_when_the_call_is_gone_at_the_head(self) -> None:
        removed = site("src/Unedited.cs", member="ProveAbsent")

        gone = FakeSources(base={"src/Edited.cs": "Topology.Reconcile(x);"}, head={"src/Edited.cs": "Other();"})

        kept = FakeSources(base={"src/Edited.cs": "Topology.Reconcile(x);"}, head={"src/Edited.cs": "Topology.Reconcile(x);"})

        with patched("REACHED_THROUGH", (reach_for(removed, side="-"),)):
            explained = classify(manifest(removed), manifest(), edited={"src/Edited.cs"}, parity=manifest(removed), sources=gone)

            unexplained = classify(manifest(removed), manifest(), edited={"src/Edited.cs"}, parity=manifest(removed), sources=kept)

        self.assertEqual("r", explained[("-", removed)].label)

        self.assertEqual("d", unexplained[("-", removed)].label)

    def test_scheduler_cancellation_handoff_explains_only_the_three_exact_removed_rows(self) -> None:
        rows, sources, caller, catalog = scheduler_cancellation_handoff_fixture()

        retained = manifest(*(row._replace(authority_operation_id="RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.DrainCancellationAsync") for row in rows))

        for row in rows:
            with self.subTest(callee=row.callee):
                reach, _ = CLASSIFIER.prove_reach(row, "-", frozenset((caller, catalog)), sources, retained)

                self.assertIsNotNone(reach)

                for field, value in (("root_type", "Other"), ("authority_operation_id", row.authority_operation_id + ".Other"),
                    ("source_path", "src/Other.cs"), ("enclosing_type", "Other"), ("member", "Other"),
                    ("member_identity", "M:Other"), ("kind", "ProviderCall"), ("callee", "System.IO.File.Delete"),
                    ("syntax_ordinal", "1"), ("syntax_sha256", "F" * 64), ("work", "A" * 64),
                    ("effect", "B" * 64), ("admitted_work_kind", "GuildStorage")):
                    with self.subTest(field=field):
                        refused, _ = CLASSIFIER.prove_reach(row._replace(**{field: value}), "-", frozenset((caller, catalog)), sources, retained)

                        self.assertIsNone(refused)

                refused, _ = CLASSIFIER.prove_reach(row, "+", frozenset((caller, catalog)), sources, retained)

                self.assertIsNone(refused)

    def test_scheduler_cancellation_handoff_requires_edited_caller_and_exact_source_transition(self) -> None:
        rows, sources, caller, catalog = scheduler_cancellation_handoff_fixture()

        retained = manifest(*(row._replace(authority_operation_id="RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.DrainCancellationAsync") for row in rows))

        for edited in ((), (catalog,)):
            with self.subTest(edited=edited):
                self.assertIsNone(CLASSIFIER.prove_reach(rows[0], "-", frozenset(edited), sources, retained)[0])

        # The CLI edit set contains authored src files, not the test-owned catalog. Its exact Git transition is checked separately.
        self.assertIsNotNone(CLASSIFIER.prove_reach(rows[0], "-", frozenset((caller,)), sources, retained)[0])

        self.assertIsNone(CLASSIFIER.prove_reach(rows[0], "-", frozenset((caller, catalog)), None, retained)[0])

        for path in (caller, catalog):
            for revision in ("base", "head"):
                for mutation in ("absent", "empty", "same-as-other"):
                    with self.subTest(path=path, revision=revision, mutation=mutation):
                        base, head = dict(sources.base_texts), dict(sources.head_texts)

                        target, other = (base, head) if revision == "base" else (head, base)

                        if mutation == "absent":
                            target.pop(path)
                        else:
                            target[path] = "" if mutation == "empty" else other[path]

                        self.assertIsNone(CLASSIFIER.prove_reach(rows[0], "-", frozenset((caller, catalog)), FakeSources(base, head), retained)[0])

        for old, new in (
            ("DrainCancellationAsync(work, scope.ServiceProvider", "DrainCancellationAsync(scope.ServiceProvider"),
            ("DrainCancellationAsync(IGrimoireWorkLease work,", "DrainCancellationAsync(object work,"),
            ("work.MaintenanceRevocation.ThrowIfCancellationRequested();", ""),
            ("policy.CreateRecoveryAsync(", "policy.OtherAsync("),
            ("RequestDisabledCancellationAsync", "OtherAsync"),
        ):
            with self.subTest(marker=old):
                head = dict(sources.head_texts)

                head[caller] = head[caller].replace(old, new)

                self.assertIsNone(CLASSIFIER.prove_reach(rows[0], "-", frozenset((caller, catalog)), FakeSources(sources.base_texts, head), retained)[0])

        # The replacement in a different caller is not evidence for the disabled-cancellation path.
        head = dict(sources.head_texts)

        head[caller] = head[caller].replace("RequestDisabledCancellationAsync", "OtherAsync")

        head[caller] += "\nprivate async Task RequestDisabledCancellationAsync(CancellationToken ct) { Other(); }"

        self.assertIsNone(CLASSIFIER.prove_reach(rows[0], "-", frozenset((caller, catalog)), FakeSources(sources.base_texts, head), retained)[0])

    def test_scheduler_cancellation_handoff_classifies_source_removals_only_with_the_retained_reads(self) -> None:
        rows, sources, caller, catalog = scheduler_cancellation_handoff_fixture()

        reviewed = CLASSIFIER.parse_manifest(MANIFEST_PATH.read_text(encoding="utf-8"), "reviewed")

        work = reviewed.resolve(rows[0].work)[0]

        committed = manifest(work, *rows)

        retained = [row._replace(authority_operation_id="RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.DrainCancellationAsync") for row in rows]

        for missing in (None, 0, 1, 2):
            with self.subTest(missing=missing):
                regenerated = manifest(work, *(row for index, row in enumerate(retained) if index != missing))

                result = classify(committed, regenerated, edited=(caller,), parity=committed, sources=sources)

                for index, row in enumerate(rows):
                    self.assertEqual("d" if index == missing else "r", result[("-", row)].label)

                    self.assertEqual("source", result[("-", row)].stage)

        without_parity = classify(committed, manifest(work, *retained), edited=(caller,), sources=sources)

        for row in rows:
            self.assertEqual("d", without_parity[("-", row)].label)

    def test_scheduler_cancellation_handoff_requires_each_exact_retained_counterpart(self) -> None:
        rows, sources, caller, catalog = scheduler_cancellation_handoff_fixture()

        for row in rows:
            retained = row._replace(authority_operation_id="RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.DrainCancellationAsync")

            for observation in (None, manifest(), manifest(retained, retained)):
                with self.subTest(callee=row.callee, observation=observation):
                    self.assertIsNone(CLASSIFIER.prove_reach(row, "-", frozenset((caller, catalog)), sources, observation)[0])

            for field in CLASSIFIER.Row._fields:
                with self.subTest(callee=row.callee, field=field):
                    changed = retained._replace(**{field: getattr(retained, field) + ".Other"})

                    self.assertIsNone(CLASSIFIER.prove_reach(row, "-", frozenset((caller, catalog)), sources, manifest(changed))[0])

    def test_a_reviewed_call_chain_does_not_explain_a_row_whose_call_was_already_there_at_the_base(self) -> None:
        added = site("src/Unedited.cs", member="ProveAbsent")

        sources = FakeSources(base={"src/Edited.cs": "Topology.Reconcile(x);"}, head={"src/Edited.cs": "Topology.Reconcile(x); More();"})

        with patched("REACHED_THROUGH", (reach_for(added),)):
            result = classify(manifest(), manifest(added), edited={"src/Edited.cs"}, parity=manifest(), sources=sources)

        self.assertEqual("d", result[("+", added)].label)

        self.assertIn("did not appear", result[("+", added)].detail)

    def test_a_reviewed_call_chain_does_not_explain_a_row_when_its_caller_is_not_an_edited_source(self) -> None:
        added = site("src/Unedited.cs", member="ProveAbsent")

        sources = FakeSources(head={"src/Edited.cs": "Topology.Reconcile(x);"})

        with patched("REACHED_THROUGH", (reach_for(added),)):
            result = classify(manifest(), manifest(added), edited={"src/Other.cs"}, parity=manifest(), sources=sources)

        self.assertEqual("d", result[("+", added)].label)

        self.assertIn("not an edited source", result[("+", added)].detail)

    def test_a_reviewed_call_chain_without_the_source_revisions_explains_nothing(self) -> None:
        added = site("src/Unedited.cs", member="ProveAbsent")

        with patched("REACHED_THROUGH", (reach_for(added),)):
            result = classify(manifest(), manifest(added), edited={"src/Edited.cs"}, parity=manifest(), sources=None)

        self.assertEqual("d", result[("+", added)].label)

        self.assertIn("--git-base", result[("+", added)].detail)

    def test_a_reviewed_call_chain_names_one_root_and_one_site_and_explains_no_other_row(self) -> None:
        reviewed = site("src/Unedited.cs", member="ProveAbsent")

        other_root = reviewed._replace(root_type="Other", authority_operation_id="Other.Run")

        other_site = reviewed._replace(callee="System.IO.File.Delete")

        sources = FakeSources(head={"src/Edited.cs": "Topology.Reconcile(x);"})

        with patched("REACHED_THROUGH", (reach_for(reviewed),)):
            result = classify(
                manifest(),
                manifest(reviewed, other_root, other_site),
                edited={"src/Edited.cs"},
                parity=manifest(),
                sources=sources,
            )

        self.assertEqual("r", result[("+", reviewed)].label)

        self.assertEqual("d", result[("+", other_root)].label)

        self.assertEqual("d", result[("+", other_site)].label)

    def test_restore_reviewed_calls_explain_only_the_exact_quota_and_secret_sites(self) -> None:
        root = "RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreService"

        operation = root + ".RestoreAsync"

        quota_caller = "src/RetroDownfall.Arcanum.Infrastructure/Repositories/ProtectedArtifactTransferStore.cs"

        secret_caller = "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupSecretRewrapper.cs"

        cases = (
            ("Data/Covenant/CovenantQuotaGuard.cs", "InsertReservationAsync", "System.Data.Common.DbCommand.ExecuteNonQueryAsync", quota_caller, ".AllocateDirectFinalizationAsync("),
            ("Data/Covenant/CovenantQuotaGuard.cs", "MoveCountersAsync", "System.Data.Common.DbCommand.ExecuteNonQueryAsync", quota_caller, ".AllocateDirectFinalizationAsync("),
            ("Data/Covenant/CovenantQuotaGuard.cs", "ReadReservationAsync", "Microsoft.Data.Sqlite.SqliteCommand.ExecuteReaderAsync", quota_caller, ".AllocateDirectFinalizationAsync("),
            ("Data/Covenant/CovenantQuotaGuard.cs", "ReadReservationAsync", "System.Data.Common.DbDataReader.ReadAsync", quota_caller, ".AllocateDirectFinalizationAsync("),
            ("Security/MirroredOsCredential.cs", "DeleteAsync", "System.IO.File.Delete", secret_caller, "await secretStore.DeleteFileEncryptionSecretAsync().ConfigureAwait(false);"),
            ("Security/MirroredOsCredential.cs", "DeleteAsync", "System.IO.File.Exists", secret_caller, "await secretStore.DeleteFileEncryptionSecretAsync().ConfigureAwait(false);"),
        )

        for path, member, callee, caller, marker in cases:
            with self.subTest(member=member, callee=callee):
                added = site("src/RetroDownfall.Arcanum.Infrastructure/" + path, member=member, callee=callee, root=root, operation=operation)

                other = added._replace(authority_operation_id=root + ".Other")

                gained = FakeSources(base={caller: "Other();"}, head={caller: marker})

                kept = FakeSources(base={caller: marker}, head={caller: marker})

                result = classify(manifest(), manifest(added, other), edited={caller}, parity=manifest(), sources=gained)

                unchanged = classify(manifest(), manifest(added), edited={caller}, parity=manifest(), sources=kept)

                self.assertEqual("r", result[("+", added)].label)

                self.assertEqual("d", result[("+", other)].label)

                self.assertEqual("d", unchanged[("+", added)].label)

    def test_guild_restore_reach_explains_only_the_exact_availability_sites_with_a_new_call(self) -> None:
        caller = "src/RetroDownfall.Arcanum.Infrastructure/Backup/BackupRestoreGuildSignetSanitation.cs"

        marker = "await GuildStorageAvailability.CheckAsync(connection, transaction, ct)"

        cases = (
            ("GrimoireDatabaseHostedService", "RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.StartAsync"),
            ("RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreService", "RetroDownfall.Arcanum.Infrastructure.Backup.BackupRestoreService.RestoreAsync"),
        )

        for root, operation in cases:
            with self.subTest(root=root):
                added = site(
                    "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildStorageAvailability.cs",
                    member="CheckAsync",
                    callee="System.Data.Common.DbCommand.ExecuteScalarAsync",
                    root=root,
                    operation=operation,
                )

                other_operation = added._replace(authority_operation_id=operation + ".Other")

                other_member = added._replace(member="CheckCovenantAsync")

                other_callee = added._replace(callee="System.Data.Common.DbCommand.ExecuteNonQueryAsync")

                gained = FakeSources(base={}, head={caller: marker})

                kept = FakeSources(base={caller: marker}, head={caller: marker})

                result = classify(
                    manifest(), manifest(added, other_operation, other_member, other_callee),
                    edited={caller}, parity=manifest(), sources=gained,
                )

                self.assertEqual("r", result[("+", added)].label)

                for other in (other_operation, other_member, other_callee):
                    self.assertEqual("d", result[("+", other)].label)

                unchanged = classify(manifest(), manifest(added), edited={caller}, parity=manifest(), sources=kept)

                no_parity = classify(manifest(), manifest(added), edited={caller}, sources=gained)

                unedited_caller = classify(manifest(), manifest(added), parity=manifest(), sources=gained)

                self.assertEqual("d", unchanged[("+", added)].label)

                self.assertEqual("d", no_parity[("+", added)].label)

                self.assertEqual("d", unedited_caller[("+", added)].label)

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

        self.assertEqual("d", result[("-", by_source)].label)

        self.assertEqual("checker", result[("-", by_checker)].stage)

        self.assertEqual("d", result[("-", by_checker)].label)

    def test_a_fabricated_row_in_an_unedited_file_fails_the_run_under_parity(self) -> None:
        fabricated = site("src/Unedited.cs", member="FabricatedMember", callee="System.Totally.Fake.Call", ordinal="999")

        with tempfile.TemporaryDirectory() as directory:
            old = Path(directory) / "old.tsv"
            new = Path(directory) / "new.tsv"
            parity = Path(directory) / "parity.tsv"
            edited = Path(directory) / "edited.txt"

            write_manifest(old, [])
            write_manifest(new, [fabricated])
            write_manifest(parity, [])
            edited.write_text("src/Edited.cs\n", encoding="utf-8")

            output = io.StringIO()

            with contextlib.redirect_stdout(output):
                status = CLASSIFIER.main(
                    [
                        "--committed", str(old),
                        "--regenerated", str(new),
                        "--edited-sources", str(edited),
                        "--parity", str(parity),
                    ]
                )

            self.assertEqual(1, status)

            self.assertIn("unexplained rows: 1", output.getvalue())

    def test_a_provider_call_row_silently_lost_in_an_unedited_file_fails_the_run_under_parity(self) -> None:
        lost = site("src/Unedited.cs", kind="ProviderCall", member="Lost", work="a" * 64, effect="b" * 64)

        group = site("src/Unedited.cs", callee=GROUP, kind="EffectFrontier", member="Begin")

        with tempfile.TemporaryDirectory() as directory:
            old = Path(directory) / "old.tsv"
            new = Path(directory) / "new.tsv"
            parity = Path(directory) / "parity.tsv"
            edited = Path(directory) / "edited.txt"

            write_manifest(old, [lost, group])
            write_manifest(new, [group])
            write_manifest(parity, [lost, group])
            edited.write_text("src/Edited.cs\n", encoding="utf-8")

            output = io.StringIO()

            with contextlib.redirect_stdout(output):
                status = CLASSIFIER.main(
                    [
                        "--committed", str(old),
                        "--regenerated", str(new),
                        "--edited-sources", str(edited),
                        "--parity", str(parity),
                    ]
                )

            self.assertEqual(1, status)

            self.assertIn("unexplained rows: 1", output.getvalue())

            self.assertIn("VIOLATED", output.getvalue())

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

        self.assertEqual(8, len(names))

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

    def test_the_base_constructor_rule_matches_the_member_that_builds_an_authored_stream_subclass_and_nothing_else(self) -> None:
        rule = rule_named("base-constructor-initializers")

        entry = CLASSIFIER.BASE_CONSTRUCTOR_SITES[0]

        sources = FakeSources(
            head={
                entry.source_path: f"""
                    internal static class Reader
                    {{
                        internal static bool {entry.member}(string path)
                        {{
                            stream = new {entry.subclass}(path);

                            return true;
                        }}

                        private sealed class {entry.subclass}(string path) : {entry.base}(path, FileMode.Open) {{ }}
                    }}
                """
            }
        )

        context = CLASSIFIER.Context(manifest(), manifest(), None, sources)

        base = site(entry.source_path, kind="FileSystemEffect", callee=f"System.IO.{entry.base}..ctor", member=entry.member)

        self.assertTrue(rule.matches(base, "+", context))

        # the rule explains an added row only, and only with the source revisions
        self.assertFalse(rule.matches(base, "-", context))

        self.assertFalse(rule.matches(base, "+", CLASSIFIER.Context(manifest(), manifest(), None)))

        # a Read row of the same member is the same site
        self.assertTrue(rule.matches(base._replace(kind="FileSystemRead"), "+", context))

        # another kind, another callee, another member and another file are not the listed site
        self.assertFalse(rule.matches(base._replace(kind="DatabaseAccess"), "+", context))

        self.assertFalse(rule.matches(base._replace(callee="System.IO.File.Delete"), "+", context))

        self.assertFalse(rule.matches(base._replace(member="Other"), "+", context))

        self.assertFalse(rule.matches(base._replace(source_path="src/Other.cs"), "+", context))

    def test_the_base_constructor_rule_does_not_excuse_a_direct_file_stream_construction(self) -> None:
        # AtomicFile builds a FileStream itself. The census always listed that call, so a row for it is not the rule's: it is d.
        rule = rule_named("base-constructor-initializers")

        sources = FakeSources(
            head={"src/RetroDownfall.Arcanum.Infrastructure/Storage/AtomicFile.cs": "static async Task ReplaceAsync() { FileStream stream = new(path, mode); }"}
        )

        direct = site(
            "src/RetroDownfall.Arcanum.Infrastructure/Storage/AtomicFile.cs",
            kind="FileSystemEffect",
            callee="System.IO.FileStream..ctor",
            member="ReplaceAsync",
            ordinal="77",
        )

        self.assertFalse(rule.matches(direct, "+", CLASSIFIER.Context(manifest(), manifest(), manifest(), sources)))

        moved = direct._replace(syntax_ordinal="78")

        result = classify(manifest(direct), manifest(moved), parity=manifest(moved), sources=sources)

        self.assertEqual({"d"}, {item.label for item in result.values()})

    def test_the_base_constructor_rule_needs_the_subclass_to_derive_from_the_stream_the_table_names(self) -> None:
        rule = rule_named("base-constructor-initializers")

        entry = CLASSIFIER.BASE_CONSTRUCTOR_SITES[0]

        base = site(entry.source_path, kind="FileSystemEffect", callee=f"System.IO.{entry.base}..ctor", member=entry.member)

        builds = f"void {entry.member}() {{ stream = new {entry.subclass}(path); }}"

        for declaration in (f"sealed class {entry.subclass} : IDisposable {{ }}", f"sealed class {entry.subclass}(string p) : Object {{ }}"):
            sources = FakeSources(head={entry.source_path: builds + " " + declaration})

            self.assertFalse(rule.matches(base, "+", CLASSIFIER.Context(manifest(), manifest(), None, sources)), declaration)

    def test_the_in_memory_stream_base_constructor_rule_matches_only_the_six_reviewed_removals(self) -> None:
        rule = rule_named("in-memory-stream-base-constructor")

        rows = CLASSIFIER.IN_MEMORY_STREAM_BASE_CONSTRUCTOR_REMOVALS

        self.assertEqual(6, len(rows))

        sources = FakeSources(base={path: (REPOSITORY / path).read_text(encoding="utf-8") for path in CLASSIFIER.IN_MEMORY_STREAM_BASE_SOURCES})

        for row in rows:
            with self.subTest(root=row.authority_operation_id, fingerprint=row.syntax_sha256):
                context = CLASSIFIER.Context(manifest(row), manifest(), manifest(), sources)

                self.assertTrue(rule.matches(row, "-", context))

                self.assertFalse(rule.matches(row, "+", context))

                self.assertEqual("c", classify(manifest(row), manifest(), parity=manifest(), sources=sources)[("-", row)].label)

                for field, value in (
                    ("root_type", "OtherRoot"),
                    ("authority_operation_id", "OtherRoot.Run"),
                    ("source_path", "src/Other.cs"),
                    ("enclosing_type", "Other.DaemonProcessRunner"),
                    ("member", "Other"),
                    ("member_identity", "M:Other.RunAsync"),
                    ("kind", "FileSystemRead"),
                    ("callee", "System.IO.FileStream..ctor"),
                    ("syntax_ordinal", "1"),
                    ("syntax_sha256", "0" * 64),
                    ("work", "a" * 64),
                    ("effect", "b" * 64),
                    ("admitted_work_kind", "BatchProcessing"),
                ):
                    self.assertFalse(rule.matches(row._replace(**{field: value}), "-", context), field)

    def test_the_in_memory_stream_base_constructor_rule_requires_old_source_parity_and_pinned_base_sources(self) -> None:
        rule = rule_named("in-memory-stream-base-constructor")

        row = next(iter(CLASSIFIER.IN_MEMORY_STREAM_BASE_CONSTRUCTOR_REMOVALS))

        texts = {path: (REPOSITORY / path).read_text(encoding="utf-8") for path in CLASSIFIER.IN_MEMORY_STREAM_BASE_SOURCES}

        sources = FakeSources(base=texts, head=texts)

        self.assertFalse(rule.matches(row, "-", CLASSIFIER.Context(manifest(row), manifest(), None, sources)))

        self.assertFalse(rule.matches(row, "-", CLASSIFIER.Context(manifest(row), manifest(), manifest(row), sources)))

        self.assertFalse(rule.matches(row, "-", CLASSIFIER.Context(manifest(row), manifest(), manifest())))

        self.assertFalse(rule.matches(row, "-", CLASSIFIER.Context(manifest(row), manifest(), manifest(), FakeSources(head=texts))))

        for path in texts:
            altered = dict(texts)

            altered[path] = altered[path].replace("Stream", "FileStream")

            self.assertFalse(rule.matches(row, "-", CLASSIFIER.Context(manifest(row), manifest(), manifest(), FakeSources(base=altered))), path)

        declaring = next(path for path in texts if path.endswith("/CappedOutputStream.cs"))

        for counterfeit in (
            "using Stream = Counterfeit.Stream;\n" + texts[declaring],
            texts[declaring].replace(": Stream", ": Stream(42)"),
            "internal class CappedOutputStream { void RunAsync() { var stream = new System.IO.Stream(); } }",
        ):
            altered = dict(texts)

            altered[declaring] = counterfeit

            self.assertFalse(rule.matches(row, "-", CLASSIFIER.Context(manifest(row), manifest(), manifest(), FakeSources(base=altered))))

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

    def test_the_stream_disposal_rule_matches_the_disposal_of_a_stream_the_member_declares_as_in_memory_or_database(self) -> None:
        rule = rule_named("stream-disposal-by-origin")

        dispose = site("src/A.cs", kind="FileSystemEffect", callee="System.IO.Stream.Dispose", member="Digest")

        for declaration in (
            "using MemoryStream buffer = new();",
            "using System.IO.MemoryStream buffer = Start(domain);",
            "using var buffer = new MemoryStream();",
            "using Stream stream = reader.GetStream(ordinal);",
        ):
            sources = FakeSources(head={"src/A.cs": "byte[] Digest(int domain)\n{\n    " + declaration + "\n    return Hash(buffer);\n}"})

            context = CLASSIFIER.Context(manifest(dispose), manifest(), manifest(), sources)

            self.assertTrue(rule.matches(dispose, "-", context), declaration)

        sources = FakeSources(head={"src/A.cs": "byte[] Digest(int d) { using MemoryStream buffer = new(); return null; }"})

        # the parity traversal still has the row: it is not the checker's to have dropped
        self.assertFalse(rule.matches(dispose, "-", CLASSIFIER.Context(manifest(dispose), manifest(), manifest(dispose), sources)))

        # an added row is not a disposal that went away
        self.assertFalse(rule.matches(dispose, "+", CLASSIFIER.Context(manifest(), manifest(dispose), manifest(), sources)))

        # no parity manifest, or no source revisions, is no proof
        self.assertFalse(rule.matches(dispose, "-", CLASSIFIER.Context(manifest(dispose), manifest(), None, sources)))

        self.assertFalse(rule.matches(dispose, "-", CLASSIFIER.Context(manifest(dispose), manifest(), manifest(), None)))

        self.assertFalse(
            rule.matches(
                dispose._replace(callee="System.IO.File.Delete"), "-", CLASSIFIER.Context(manifest(dispose), manifest(), manifest(), sources)
            )
        )

    def test_the_stream_disposal_rule_does_not_excuse_the_disposal_of_a_file_backed_stream(self) -> None:
        rule = rule_named("stream-disposal-by-origin")

        dispose = site("src/A.cs", kind="FileSystemEffect", callee="System.IO.Stream.Dispose", member="Digest")

        declarations = (
            # no in-memory or database stream at all
            "using FileStream stream = new(path, FileMode.Open);",
            "using Stream stream = new FileStream(path, FileMode.Open);",
            "using Stream stream = File.OpenRead(path);",
            # the file-backed stream shares the member with an in-memory one
            "using MemoryStream buffer = new(); using FileStream stream = new(path, FileMode.Open);",
            "using MemoryStream buffer = new(); using Stream stream = File.OpenRead(path);",
        )

        for declaration in declarations:
            sources = FakeSources(head={"src/A.cs": "byte[] Digest(string path) { " + declaration + " return null; }"})

            self.assertFalse(rule.matches(dispose, "-", CLASSIFIER.Context(manifest(dispose), manifest(), manifest(), sources)), declaration)

        # a member the source does not declare at all is not excused either
        sources = FakeSources(head={"src/A.cs": "byte[] Other() { using MemoryStream buffer = new(); return null; }"})

        self.assertFalse(rule.matches(dispose, "-", CLASSIFIER.Context(manifest(dispose), manifest(), manifest(), sources)))

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

    def test_every_reviewed_call_chain_is_still_the_call_the_source_makes_and_a_row_of_the_committed_manifest(self) -> None:
        reviewed = CLASSIFIER.parse_manifest(MANIFEST_PATH.read_text(encoding="utf-8"), "reviewed")

        self.assertGreater(len(CLASSIFIER.REACHED_THROUGH), 0)

        for entry in CLASSIFIER.REACHED_THROUGH:
            caller = (REPOSITORY / entry.caller).read_text(encoding="utf-8")

            # The call that reaches an added row is in the caller now; the call that reached a removed row is gone from it.
            if entry.side == "+":
                self.assertIn(entry.marker, caller, entry.caller)
            else:
                self.assertNotIn(entry.marker, caller, entry.caller)

            holder = (REPOSITORY / entry.source_path).read_text(encoding="utf-8")

            self.assertTrue(CLASSIFIER.member_bodies(holder, entry.member), entry.member)

            if entry.side != "+":
                # The row a removed chain explains is not in the manifest any more, and a site with the same member and callee can
                # have other rows that stay, so the committed manifest says nothing about it.
                continue

            self.assertTrue(
                any(
                    (row.root_type, row.authority_operation_id, row.source_path, row.member, row.callee)
                    == (entry.root_type, entry.operation, entry.source_path, entry.member, entry.callee)
                    for row in reviewed.rows
                ),
                f"{entry.root_type}|{entry.operation}|{entry.member}|{entry.callee} is no row of the committed manifest",
            )

    def test_the_guild_memory_read_reaches_require_the_introduced_call_and_exact_root_site(self) -> None:
        entries = CLASSIFIER.GUILD_MEMORY_READ_REACHES

        self.assertEqual(29, len(entries))

        self.assertEqual(14, sum(entry.root_type == "Loremaster" for entry in entries))

        self.assertEqual(15, sum(entry.root_type == "SessionAttachmentIndexingService" for entry in entries))

        for entry in entries:
            with self.subTest(root=entry.root_type, member=entry.member, callee=entry.callee):
                row = site(entry.source_path, callee=entry.callee, member=entry.member,
                    root=entry.root_type, operation=entry.operation)

                def label(candidate=row, base="", head=entry.marker):
                    result = classify(manifest(), manifest(candidate), edited=[entry.caller], parity=manifest(),
                        sources=FakeSources(base={entry.caller: base}, head={entry.caller: head}))

                    return result[("+", candidate)].label

                self.assertEqual("r", label())

                self.assertEqual("d", label(head=""))

                self.assertEqual("d", label(base=entry.marker))

                for field, value in (("root_type", "Other"), ("authority_operation_id", "Other.Run"),
                    ("source_path", "src/Other.cs"), ("member", "Other"), ("callee", "Other.Call")):
                    self.assertEqual("d", label(row._replace(**{field: value})), field)

    def test_historical_ledger_addition_requires_exact_baseline_and_parity_rows(self) -> None:
        retained = site("src/Retained.cs")
        added = site("src/Preexisting.cs")

        result = CLASSIFIER.classify(manifest(retained), manifest(retained, added), frozenset(),
            manifest(retained, added), None, manifest(retained, added))

        item = next(item for item in result if item.row == added)

        self.assertEqual(("l", "baseline-ledger"), (item.label, item.stage))

        for parity, baseline in ((None, manifest(added)), (manifest(added), None),
            (manifest(), manifest(added)), (manifest(added), manifest()),
            (manifest(added._replace(syntax_sha256="A" * 64)), manifest(added)),
            (manifest(added), manifest(added._replace(effect="B" * 64)))):
            result = CLASSIFIER.classify(manifest(retained), manifest(retained, added), frozenset(), parity, None, baseline)

            self.assertNotEqual("l", next(item for item in result if item.row == added).label)

        removed = CLASSIFIER.classify(manifest(retained, added), manifest(retained), frozenset(),
            manifest(retained, added), None, manifest(retained, added))

        self.assertNotEqual("l", removed[0].label)

        self.assertEqual("d", classify(manifest(retained), manifest(retained, added), parity=manifest(added))[("+", added)].label)

    def test_cli_baseline_observation_reports_historical_rows_without_qualifying_baseline(self) -> None:
        retained = site("src/Retained.cs")
        added = site("src/Preexisting.cs")

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)

            for name, rows in (("committed", [retained]), ("regenerated", [retained, added]),
                ("parity", [retained, added]), ("baseline", [retained, added])):
                write_manifest(root / (name + ".tsv"), rows)

            (root / "edited.txt").write_text("")

            with contextlib.redirect_stdout(io.StringIO()):
                status = CLASSIFIER.main(["--committed", str(root / "committed.tsv"),
                    "--regenerated", str(root / "regenerated.tsv"), "--parity", str(root / "parity.tsv"),
                    "--baseline-observation", str(root / "baseline.tsv"), "--edited-sources", str(root / "edited.txt"),
                    "--report", str(root / "report.tsv")])

            self.assertEqual(0, status)

            self.assertIn("l\t\tbaseline-ledger\t+", (root / "report.tsv").read_text())

    def test_reviewed_workspace_lease_closures_need_exact_observations_and_source(self) -> None:
        rows = sorted(CLASSIFIER.WORKSPACE_LEASE_REVIEWED_ROWS)

        self.assertEqual(92, len(rows))

        texts = {path: (REPOSITORY / path).read_text() for path in CLASSIFIER.WORKSPACE_LEASE_BASE_SOURCE_SHA256}
        source_hashes = {path: hashlib.sha256(text.encode()).hexdigest() for path, text in texts.items()}
        sources = FakeSources(base=texts, head=texts)
        terminal_hashes = {path: source_hashes[path] for path in CLASSIFIER.WORKSPACE_LEASE_TERMINAL_SOURCE_SHA256}
        # This synthetic fixture defines both sides as the same current authored corpus. Pin both guard layers to those
        # fixture bytes only within this test; production keeps the contract's historical baseline and terminal digests.
        # The final observation wrapper separately validates those real pins against the verified exact baseline snapshot.
        reviewed = CLASSIFIER.parse_manifest(MANIFEST_PATH.read_text(), "reviewed")

        with patched("WORKSPACE_LEASE_BASE_SOURCE_SHA256", source_hashes), \
            patched("WORKSPACE_LEASE_TERMINAL_SOURCE_SHA256", terminal_hashes):
            for row in rows:
                retained = site("src/Retained.cs", root=row.root_type, operation=row.authority_operation_id)
                references = [candidate for candidate in rows if candidate.capsule_id in row.references]
                # Some contexts reference acquisitions outside the reviewed terminal footprint. Use the actual final ledger's
                # exact acquisitions so the test does not pass through the unresolved-reference refusal first.
                references += [candidate for candidate in reviewed.rows if candidate.capsule_id in row.references]
                references = [candidate for candidate in dict.fromkeys(references) if candidate != row]
                old = manifest(retained, *references)
                new = manifest(retained, *references, row)
                parity = new
                result = CLASSIFIER.classify(old, new, frozenset(), parity, sources, old)
                item = next(item for item in result if item.row == row)

                self.assertEqual("c", item.label, (row.root, row.member, row.callee))

                expected_rule = "retained-workspace-pin-cleanup-origin" if row.source_path.endswith("/RetainedExclusiveFileLock.cs") else "workspace-lease-closed-dispatch"

                self.assertEqual(expected_rule, item.rule)

                if row == rows[0]:
                    # Every baseline chain source remains required, even when it is not this row's terminal file.
                    for path in source_hashes:
                        for altered in (None, "changed call-chain source"):
                            drift = FakeSources(base={**texts, path: altered}, head=texts)
                            result = CLASSIFIER.classify(old, new, frozenset(), parity, drift, old)

                            self.assertNotEqual("c", next(item for item in result if item.row == row).label, path)

                    # Terminal sources must remain exact on the head side too. A real candidate edit cannot borrow
                    # the synthetic fixture's equal-source classification merely by retaining its historical base.
                    for path in terminal_hashes:
                        for altered in (None, "changed terminal source"):
                            drift = FakeSources(base=texts, head={**texts, path: altered})
                            result = CLASSIFIER.classify(old, new, frozenset(), parity, drift, old)

                            self.assertNotEqual("c", next(item for item in result if item.row == row).label, path)

                for changed in (row._replace(member="Other"), row._replace(syntax_ordinal="999"),
                    row._replace(syntax_sha256="A" * 64), row._replace(effect="B" * 64),
                    row._replace(root_type="Other")):
                    result = CLASSIFIER.classify(old, manifest(retained, *references, changed), frozenset(),
                        manifest(retained, *references, changed), sources, old)

                    self.assertNotEqual("c", next(item for item in result if item.row == changed).label)

                for p, b in ((None, old), (parity, None), (old, old), (parity, new)):
                    result = CLASSIFIER.classify(old, new, frozenset(), p, sources, b)

                    self.assertNotEqual("c", next(item for item in result if item.row == row).label)

                drift = FakeSources(base={**texts, row.source_path: "changed source"}, head=texts)
                result = CLASSIFIER.classify(old, new, frozenset(), parity, drift, old)

                self.assertNotEqual("c", next(item for item in result if item.row == row).label)

                drift = FakeSources(base=texts, head={**texts, row.source_path: "changed source"})
                result = CLASSIFIER.classify(old, new, frozenset(), parity, drift, old)

                self.assertNotEqual("c", next(item for item in result if item.row == row).label)

                removed = CLASSIFIER.classify(new, old, frozenset(), parity, sources, old)

                self.assertNotEqual("c", next(item for item in removed if item.row == row).label)

    def test_legacy_guild_ownership_reads_require_the_exact_introduced_repository_call(self) -> None:
        caller = "src/RetroDownfall.Arcanum.Infrastructure/Repositories/ApprenticeRepository.Guild.cs"
        inspector = "await inspector.InspectAsync(command.Connection!, command.Transaction, core, cancellationToken)"
        availability = "await GuildStorageAvailability.CheckAsync(command.Connection!, command.Transaction, cancellationToken)"

        for operation in ("ContinueAsync", "InterveneAsync", "InterveneOperatorAsync", "ResumeAsync", "ReweaveAsync", "StartAsync"):
            for member in ("ReadCatalogAsync", "ReadIndexColumnsAsync", "ReadIndexListAsync"):
                for callee in ("Microsoft.Data.Sqlite.SqliteCommand.ExecuteReaderAsync", "System.Data.Common.DbDataReader.ReadAsync"):
                    row = site("src/RetroDownfall.Arcanum.Infrastructure/Data/Schema/GrimoireSchemaManifestInspector.cs",
                        callee=callee, member=member, root="ApprenticeService",
                        operation="RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService." + operation)

                    self._assert_exact_introduced_source_reach(row, caller, inspector)

            row = site("src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildStorageAvailability.cs",
                callee="System.Data.Common.DbCommand.ExecuteScalarAsync", member="CheckAsync", root="ApprenticeService",
                operation="RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService." + operation)

            self._assert_exact_introduced_source_reach(row, caller, availability)

    def test_guild_host_identity_reads_require_the_exact_introduced_physical_identity_call(self) -> None:
        caller = "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildLocalHostPolicy.Canonical.cs"
        marker = "opener.IdentifyExact(root)"

        for root, operation in (
            ("Loremaster", "RetroDownfall.Arcanum.Infrastructure.Hosting.Loremaster.ExecuteAsync"),
            ("SagaExtractionService", "RetroDownfall.Arcanum.Infrastructure.Hosting.SagaExtractionService.ExecuteAsync"),
            ("SessionAttachmentIndexingService", "RetroDownfall.Arcanum.Infrastructure.Weave.SessionAttachmentIndexingService.ExecuteAsync"),
        ):
            for member, callee in (
                ("TryGetUnixPathMetadataNoFollow", "RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.lstat"),
                ("TryGetWindowsFileIdInfo", "RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.GetFileInformationByHandleEx"),
                ("TryGetWindowsHandleMetadata", "RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.GetFileInformationByHandle"),
                ("TryGetWindowsPathMetadataNoFollow", "RetroDownfall.Arcanum.Infrastructure.Security.FileHandleIdentityInterop.CreateFile"),
            ):
                row = site("src/RetroDownfall.Arcanum.Infrastructure/Security/FileHandleIdentity.cs", kind="FileSystemRead",
                    callee=callee, member=member, root=root, operation=operation)

                self._assert_exact_introduced_source_reach(row, caller, marker)

    def _assert_exact_introduced_source_reach(self, row, caller, marker):
        def label(candidate=row, base="", head=marker, edited=(caller,)):
            retained = site("src/Retained.cs", root=candidate.root_type, operation=candidate.authority_operation_id)

            result = classify(manifest(retained), manifest(retained, candidate), edited=edited, parity=manifest(retained),
                sources=FakeSources(base={caller: base}, head={caller: head}))

            return result[("+", candidate)].label

        self.assertEqual("r", label(), (row.root, row.member, row.callee))

        self.assertEqual("d", label(head=""))

        self.assertEqual("d", label(base=marker))

        self.assertEqual("d", label(edited=()))

        for field, value in (("root_type", "Other"), ("authority_operation_id", "Other.Run"),
            ("source_path", "src/Other.cs"), ("member", "Other"), ("callee", "Other.Call")):
            self.assertEqual("d", label(row._replace(**{field: value})), field)

    def test_every_reviewed_call_chain_names_one_side_a_marker_and_a_reason(self) -> None:
        keys = [(e.root_type, e.operation, e.source_path, e.member, e.callee, e.side) for e in CLASSIFIER.REACHED_THROUGH]

        self.assertEqual(len(keys), len(set(keys)))

        for entry in CLASSIFIER.REACHED_THROUGH:
            self.assertIn(entry.side, ("+", "-"))

            self.assertTrue(entry.marker.strip())

            self.assertTrue(entry.reason.strip())

    def test_every_base_constructor_site_is_a_subclass_the_source_declares_and_builds_and_a_row_of_the_committed_manifest(self) -> None:
        reviewed = CLASSIFIER.parse_manifest(MANIFEST_PATH.read_text(encoding="utf-8"), "reviewed")

        for entry in CLASSIFIER.BASE_CONSTRUCTOR_SITES:
            member_text = (REPOSITORY / entry.source_path).read_text(encoding="utf-8")

            declaring_text = (REPOSITORY / entry.declared_in).read_text(encoding="utf-8")

            self.assertRegex(
                declaring_text,
                r"class\s+" + entry.subclass + r"\s*(?:\([^)]*\))?\s*:\s*(?:System\.IO\.)?" + entry.base + r"\b",
            )

            self.assertRegex("\n".join(CLASSIFIER.member_bodies(member_text, entry.member)), r"\b" + entry.subclass + r"\b")

            self.assertTrue(
                any(
                    row.source_path == entry.source_path and row.member == entry.member and row.callee == f"System.IO.{entry.base}..ctor"
                    for row in reviewed.rows
                ),
                f"{entry.source_path} {entry.member} has no {entry.base} constructor row in the committed manifest",
            )

    def test_the_reviewed_roots_without_a_work_are_exactly_the_roots_that_hold_unframed_provider_calls_in_the_committed_manifest(self) -> None:
        reviewed = CLASSIFIER.parse_manifest(MANIFEST_PATH.read_text(encoding="utf-8"), "reviewed")

        holders = {
            CLASSIFIER.selection_root(row)
            for row in reviewed.rows
            if row.kind == "ProviderCall" and "-" in (row.work, row.effect)
        }

        named = {(entry.root_type, entry.operation) for entry in CLASSIFIER.UNFRAMED_PROVIDER_CALL_ROOTS}

        self.assertEqual(len(CLASSIFIER.UNFRAMED_PROVIDER_CALL_ROOTS), len(named))

        self.assertEqual(sorted(holders), sorted(named))

        for entry in CLASSIFIER.UNFRAMED_PROVIDER_CALL_ROOTS:
            self.assertTrue(entry.reason.strip(), entry.operation)

            self.assertIn(
                entry.authority,
                ("PreReadinessStartup", "StoppedHost", "OwnerBoundMaintenance", "FiniteRequest", "OwnerBoundRecovery", "EffectFree"),
            )

    def test_the_member_bodies_of_a_source_are_the_blocks_and_expression_bodies_that_declare_the_member(self) -> None:
        text = """
            internal sealed class Store
            {
                [Obsolete("x")]
                internal static async Task<Result<byte[]>> Read(string path, int n)
                {
                    using MemoryStream buffer = new();

                    return Call(buffer);
                }

                private static string Name() => "name";

                internal int Read(int overload) { return overload; }

                abstract void Missing(int a);

                void Caller()
                {
                    Read(path, 1);
                    Name();
                    Missing(3);
                }
            }
        """

        read = CLASSIFIER.member_bodies(text, "Read")

        self.assertEqual(2, len(read))

        self.assertIn("MemoryStream", read[0])

        self.assertIn("return overload;", read[1])

        self.assertEqual(['private static string Name() => "name";'], [body.strip() for body in CLASSIFIER.member_bodies(text, "Name")])

        # a declaration without a body and a call are no body
        self.assertEqual([], CLASSIFIER.member_bodies(text, "Missing"))

        self.assertEqual([], CLASSIFIER.member_bodies(text, "Absent"))

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
        framed = site("src/A.cs", kind="ProviderCall", work="a" * 64, effect="b" * 64, root=PID_ROOT, operation=PID_OPERATION)

        unframed = site("src/B.cs", kind="ProviderCall", member="Other", root=PID_ROOT, operation=PID_OPERATION)

        existing = site("src/C.cs", kind="ProviderCall", member="Lifecycle", root=PID_ROOT, operation=PID_OPERATION)

        violations = {
            invariant.name: invariant.violations
            for invariant in CLASSIFIER.check_invariants(manifest(existing), manifest(existing, framed, unframed))
        }

        # the reviewed root holds the row it had, and the row the regeneration adds under it is still a finding
        self.assertEqual(1, len(violations["provider-call-frames"]))

        self.assertIn("Other", violations["provider-call-frames"][0])

        self.assertNotIn("under no reviewed root", violations["provider-call-frames"][0])

        self.assertEqual([], violations["new-roots"])

    def test_a_provider_call_without_frames_under_a_root_nobody_reviewed_violates_the_invariants(self) -> None:
        # Present before and after, so nothing was added; but a root whose authority was never reviewed as Work-less holds it.
        stray = site("src/C.cs", kind="ProviderCall", member="Stray", root="Worker", operation="Worker.ExecuteAsync")

        reviewed = site("src/D.cs", kind="ProviderCall", member="Lifecycle", root=PID_ROOT, operation=PID_OPERATION)

        violations = {i.name: i.violations for i in CLASSIFIER.check_invariants(manifest(stray, reviewed), manifest(stray, reviewed))}

        self.assertEqual(1, len(violations["provider-call-frames"]))

        self.assertIn("Stray", violations["provider-call-frames"][0])

        self.assertIn("under no reviewed root", violations["provider-call-frames"][0])

    def test_a_selection_root_is_reviewed_by_its_call_and_not_by_its_digest(self) -> None:
        selected = CLASSIFIER.UNFRAMED_PROVIDER_CALL_ROOTS[1]

        row = site("src/A.cs", kind="ProviderCall", member="Recover", root=selected.root_type, operation=selected.operation + "~" + "F" * 64)

        violations = {i.name: i.violations for i in CLASSIFIER.check_invariants(manifest(row), manifest(row))}

        self.assertEqual([], violations["provider-call-frames"])

    def test_a_provider_call_row_that_goes_away_unexplained_violates_the_invariants(self) -> None:
        acquisition = site("src/Keep.cs", callee=ACQUISITION, kind="EffectFrontier", member="Acquire")

        group = site("src/Keep.cs", callee=GROUP, kind="EffectFrontier", member="Begin")

        frames = {"work": acquisition.capsule_id, "effect": group.capsule_id}

        gone = site("src/Unedited.cs", kind="ProviderCall", member="Gone", **frames)

        edited = site("src/Edited.cs", kind="ProviderCall", member="Edited", **frames)

        framed = site("src/Unedited.cs", kind="ProviderCall", member="Moved", **frames)

        # the same site with another framed row in its place: its frame changed, and it is still a provider call with both frames
        replaced = framed._replace(work=group.capsule_id)

        committed = manifest(acquisition, group, gone, edited, framed)

        regenerated = manifest(acquisition, group, replaced)

        labelled = CLASSIFIER.classify(committed, regenerated, frozenset({"src/Edited.cs"}), committed)

        violations = {i.name: i.violations for i in CLASSIFIER.check_invariants(committed, regenerated, labelled)}

        # Gone is the finding: an unedited provider call that nothing explains and nothing replaced. Edited is explained by its
        # file. Moved is a changed row, replaced at its site by a framed one; its removal is labelled d (nobody explained why its
        # frame moved), which is the classifier's finding and not this invariant's.
        self.assertEqual(1, len(violations["provider-call-removals"]))

        self.assertIn("Gone", violations["provider-call-removals"][0])

    def test_a_provider_call_row_a_named_rule_explains_may_go_away(self) -> None:
        acquisition = site("src/Keep.cs", callee=ACQUISITION, kind="EffectFrontier", member="Acquire")

        group = site("src/Keep.cs", callee=GROUP, kind="EffectFrontier", member="Begin")

        gone = site("src/Unedited.cs", kind="ProviderCall", member="Gone", work=acquisition.capsule_id, effect=group.capsule_id)

        rule = CLASSIFIER.Rule("test-rule", "Test lane", "-", "matches the member Gone", lambda row, side, context: row.member == "Gone")

        committed, regenerated = manifest(acquisition, group, gone), manifest(acquisition, group)

        with patched("RULES", (rule,)):
            labelled = CLASSIFIER.classify(committed, regenerated, frozenset(), manifest())

        violations = {i.name: i.violations for i in CLASSIFIER.check_invariants(committed, regenerated, labelled)}

        self.assertEqual("c", labelled[0].label)

        self.assertEqual([], violations["provider-call-removals"])

    def test_every_row_of_a_reviewed_new_root_is_n_whatever_source_it_comes_from(self) -> None:
        entry = CLASSIFIER.NEW_ROOTS[0]

        old = site("src/A.cs")

        fresh = site("src/Unedited.cs", root=entry.root_type, operation=entry.operation)

        edited = site("src/Edited.cs", root=entry.root_type, operation=entry.operation)

        result = classify(manifest(old), manifest(old, fresh, edited), edited={"src/Edited.cs"})

        self.assertEqual("n", result[("+", fresh)].label)

        self.assertEqual("n", result[("+", edited)].label)

    def test_split_apprentice_guidance_roots_are_reviewed_without_widening_the_family(self) -> None:
        old = site("src/A.cs")

        for member, expected in (("ContinueAsync", "n"), ("InterveneOperatorAsync", "n"), ("UnreviewedGuidanceAsync", "d")):
            with self.subTest(member=member):
                added = site(
                    "src/UneditedRepository.cs",
                    root="ApprenticeService",
                    operation="RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService." + member,
                )

                result = classify(manifest(old), manifest(old, added))

                self.assertEqual(expected, result[("+", added)].label)

    def test_a_row_of_a_root_nobody_reviewed_is_not_n(self) -> None:
        old = site("src/A.cs")

        foreign = site("src/Unedited.cs", root="Other", operation="Other.ExecuteAsync")

        result = classify(manifest(old), manifest(old, foreign))

        self.assertEqual("d", result[("+", foreign)].label)

    def test_a_row_of_a_reviewed_root_the_committed_manifest_already_had_is_not_n(self) -> None:
        entry = CLASSIFIER.NEW_ROOTS[0]

        held = site("src/A.cs", root=entry.root_type, operation=entry.operation)

        added = site("src/Unedited.cs", root=entry.root_type, operation=entry.operation)

        result = classify(manifest(held), manifest(held, added))

        self.assertEqual("d", result[("+", added)].label)

    def test_a_reviewed_new_root_passes_the_new_roots_invariant_and_an_unreviewed_one_fails_it(self) -> None:
        entry = CLASSIFIER.NEW_ROOTS[0]

        old = site("src/A.cs")

        reviewed = site("src/B.cs", root=entry.root_type, operation=entry.operation)

        violations = {
            invariant.name: invariant.violations
            for invariant in CLASSIFIER.check_invariants(manifest(old), manifest(old, reviewed))
        }

        self.assertEqual([], violations["new-roots"])

    def test_the_reviewed_new_roots_are_unique_and_each_states_its_reason(self) -> None:
        named = {(entry.root_type, entry.operation) for entry in CLASSIFIER.NEW_ROOTS}

        self.assertEqual(len(CLASSIFIER.NEW_ROOTS), len(named))

        for entry in CLASSIFIER.NEW_ROOTS:
            self.assertTrue(entry.reason.strip(), entry.operation)

    def test_guild_execution_new_roots_are_exact_catalog_operations_without_mixed_task_owners(self) -> None:
        expected = {
            ("ApprenticeService", "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.ExecuteGuildAsync"),
            *(("GuildLocalHostReadiness", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalHostReadiness." + member)
                for member in ("RequireExecutionReady", "StartAsync", "StopAsync")),
            *(("GuildLocalQuestScheduler", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler." + member)
                for member in ("ExecuteAsync", "RunAdmittedAsync", "RunRemoteAdmittedAsync", "DrainCancellationAsync", "RunCommittedAsync", "Schedule", "StartAsync")),
            *(("GuildInboundPeerReloadService", "RetroDownfall.Arcanum.Infrastructure.Guild.GuildInboundPeerReloadService." + member)
                for member in ("StartAsync", "ExecuteAsync", "OnChanged")),
        }

        actual = {(entry.root_type, entry.operation) for entry in CLASSIFIER.NEW_ROOTS
            if entry.root_type in ("GuildLocalHostReadiness", "GuildLocalQuestScheduler", "GuildInboundPeerReloadService")
                or entry.operation.endswith(".ExecuteGuildAsync")}

        self.assertEqual(expected, actual)

        # These declared traversal roots currently have no physical sites. They must not grant a new-root
        # explanation to a future physical effect merely because their lifecycle methods already exist.
        for member in ("StopAsync", "Dispose"):
            operation = "RetroDownfall.Arcanum.Infrastructure.Guild.GuildInboundPeerReloadService." + member
            unexpected = site("src/Unedited.cs", root="GuildInboundPeerReloadService", operation=operation)
            result = classify(manifest(), manifest(unexpected))

            self.assertEqual("d", result[("+", unexpected)].label, member)

        self.assertNotIn(("GuildLocalQuestScheduler",
            "RetroDownfall.Arcanum.Infrastructure.Hosting.ApprenticeService.RunApprenticeAsync"), actual)

    def test_every_reviewed_new_root_is_a_root_of_the_committed_manifest(self) -> None:
        reviewed = CLASSIFIER.parse_manifest(MANIFEST_PATH.read_text(encoding="utf-8"), "reviewed")

        roots = {CLASSIFIER.selection_root(row) for row in reviewed.rows}

        for entry in CLASSIFIER.NEW_ROOTS:
            self.assertIn((entry.root_type, entry.operation), roots)

    def test_an_unframed_provider_call_under_a_reviewed_new_root_is_held_to_the_reviewed_list_and_not_to_the_committed_manifest(self) -> None:
        entry = CLASSIFIER.NEW_ROOTS[0]

        old = site("src/A.cs")

        unframed = site(
            "src/Daemon.cs",
            callee="System.Diagnostics.Process.Start",
            kind="ProviderCall",
            root=entry.root_type,
            operation=entry.operation,
        )

        reviewed = {(item.root_type, item.operation) for item in CLASSIFIER.UNFRAMED_PROVIDER_CALL_ROOTS}

        violations = {
            invariant.name: invariant.violations
            for invariant in CLASSIFIER.check_invariants(manifest(old), manifest(old, unframed))
        }

        if (entry.root_type, entry.operation) in reviewed:
            self.assertEqual([], violations["provider-call-frames"])
        else:
            self.assertEqual(1, len(violations["provider-call-frames"]))

    def test_an_unframed_provider_call_added_under_a_reviewed_new_root_the_committed_manifest_already_holds_is_reported(self) -> None:
        for entry in CLASSIFIER.NEW_ROOTS:
            with self.subTest(root=entry.operation):
                held = site(
                    "src/Daemon.cs",
                    callee="System.Diagnostics.Process.Start",
                    kind="ProviderCall",
                    root=entry.root_type,
                    operation=entry.operation,
                )

                added = site(
                    "src/Edited.cs",
                    callee="System.Diagnostics.Process.Start",
                    kind="ProviderCall",
                    member="Spawn",
                    root=entry.root_type,
                    operation=entry.operation,
                )

                violations = {
                    invariant.name: invariant.violations
                    for invariant in CLASSIFIER.check_invariants(manifest(held), manifest(held, added))
                }

                spelled = f"{entry.root_type}|{entry.operation}|System.Diagnostics.Process.Start|"

                # The root is reviewed, but the committed manifest holds it: the row it gained is judged like any other root's, so it is
                # reported by its own member, and the row the committed manifest already held is not an addition.
                self.assertIn(spelled + "Spawn", violations["provider-call-frames"])

                self.assertNotIn(spelled + "Run", violations["provider-call-frames"])

                reviewed = {(item.root_type, item.operation) for item in CLASSIFIER.UNFRAMED_PROVIDER_CALL_ROOTS}

                if (entry.root_type, entry.operation) in reviewed:
                    self.assertEqual([spelled + "Spawn"], violations["provider-call-frames"])

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
