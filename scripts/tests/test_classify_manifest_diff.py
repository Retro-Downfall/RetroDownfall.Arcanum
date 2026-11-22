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
    REPOSITORY / "tests" / "RetroDownfall.Arcanum.Tests" / "Support" / "hosted-grimoire-producer-capsules-v2.tsv"
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
    return CLASSIFIER.Manifest(list(rows), name, version=1)


def manifest_v2(*rows: "CLASSIFIER.Row", name: str = "m") -> "CLASSIFIER.Manifest":
    return CLASSIFIER.Manifest(list(rows), name, version=2)


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


def joined_guild_authorizer_fixture():
    guild = "RetroDownfall.Arcanum.Infrastructure.Guild."
    authorizer = guild + "GuildQuestStore"
    authorization = authorizer + ".AuthorizeLocalToolAsync"
    caller = site("src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildQuestStore.WorkspaceMutationTools.cs",
                  root="GrimoireDatabaseHostedService",
                  operation="RetroDownfall.Arcanum.Infrastructure.Hosting.GrimoireDatabaseHostedService.StartAsync",
                  member="RevalidateWorkspaceMutationToolAsync", callee=authorization,
                  kind="JoinedSelfAdmission")._replace(enclosing_type=authorizer,
                      member_identity="M:" + authorizer + ".RevalidateWorkspaceMutationToolAsync("
                      + guild + "GuildChildToolAdmission,System.Threading.CancellationToken)",
                      syntax_sha256="E219862A30D013CCD1844F1085A719C8B2AF0DC565849ECA438BA9B1B65E36C5")
    work = site("src/SignetAuthority.cs", member="WithInstallationAsync", root=authorizer, operation=authorization,
                kind="EffectFrontier", callee=ACQUISITION)
    work = work._replace(work=work.capsule_id, registered_root_work=work.capsule_id, admitted_work_kind="GuildStorage")
    child = site("src/GuildQuestStore.cs", member="ReadWithinAsync", root=authorizer, operation=authorization,
                 callee="System.Data.Common.DbCommand.ExecuteScalarAsync", kind="DatabaseAccess",
                 work=work.capsule_id)._replace(registered_root_work=work.capsule_id)
    return caller, work, child


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

    def test_joined_guild_authorizer_is_a_named_new_root_with_exact_link_and_own_work(self) -> None:
        caller, work, child = joined_guild_authorizer_fixture()
        committed = manifest_v2(caller._replace(kind="FileSystemRead", callee="System.IO.File.Exists"))
        candidate = manifest_v2(*committed.rows, caller, work, child)

        changes = classify(committed, candidate, parity=committed)

        self.assertEqual("n", changes[("+", work)].label)
        self.assertEqual("n", changes[("+", child)].label)

        invariant = next(check for check in CLASSIFIER.check_invariants(committed, candidate)
                         if check.name == "joined-guild-authorization")
        self.assertEqual([], invariant.violations)

    def test_joined_guild_authorizer_refuses_orphan_links_foreign_sites_and_unowned_children(self) -> None:
        caller, work, child = joined_guild_authorizer_fixture()
        caller_read = caller._replace(kind="FileSystemRead", callee="System.IO.File.Exists")
        committed = manifest_v2(caller_read)
        caller_work = work._replace(root_type=caller.root_type, authority_operation_id=caller.authority_operation_id)

        cases = (
            (work, child),
            (caller,),
            (caller, caller, work, child),
            (caller._replace(root_type="Other", authority_operation_id="Other.Start"), work, child),
            (caller._replace(work=caller_work.capsule_id, registered_root_work=caller_work.capsule_id), caller_work, work, child),
            (caller._replace(callee="Other.Auth"), work, child),
            (caller._replace(source_path="src/Other.cs"), work, child),
            (caller._replace(syntax_sha256="F" * 64), work, child),
            (caller, work._replace(admitted_work_kind="LongRestSweep"), child),
            (caller, work, child._replace(work="-")),
            (caller, work, child._replace(registered_root_work="unresolved")),
            (caller, work, child._replace(effect="outside")),
            (caller, work, child._replace(kind="ProviderCall")),
            (caller, work, child._replace(kind="FileSystemEffect")),
            (caller, work, child._replace(callee="System.Data.Common.DbCommand.ExecuteNonQueryAsync")),
        )

        for rows in cases:
            with self.subTest(rows=rows):
                candidate = manifest_v2(*committed.rows, *rows)
                invariant = next(check for check in CLASSIFIER.check_invariants(committed, candidate)
                                 if check.name == "joined-guild-authorization")
                self.assertTrue(invariant.violations)
                self.assertNotIn(work.root, CLASSIFIER.reviewed_new_root_keys(candidate))

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

    def test_exact_source_reach_requires_the_reviewed_row_and_call_in_the_named_member(self) -> None:
        caller = "src/Scheduler.cs"
        marker = "await portal.DrainOutboxAsync(token);"
        acquired = site("src/Lease.cs", callee=ACQUISITION, kind="EffectFrontier", member="Acquire")
        acquired = acquired._replace(work=acquired.capsule_id, admitted_work_kind="GuildStorage")
        changed = site("src/Portal.cs", member="Publish", work=acquired.capsule_id)
        reach = CLASSIFIER.ExactSourceReach(frozenset([changed]), caller, "RunAdmittedAsync", marker, "the scheduler awaits the durable publish path")
        base = "private async Task RunAdmittedAsync() { await ExistingAsync(); }"
        head = base.replace("await ExistingAsync();", marker)
        old = manifest(acquired)

        def label(row=changed, old_source=base, new_source=head, parity=old, edited=(caller,)):
            sources = FakeSources(base={caller: old_source}, head={caller: new_source})
            with patched("EXACT_SOURCE_REACHES", (reach,)):
                result = classify(old, manifest(acquired, row), edited=edited, parity=parity, sources=sources)
            return result[("+", row)].label

        self.assertEqual("r", label())
        self.assertEqual("d", label(old_source=head))
        self.assertEqual("d", label(new_source=base + " private async Task OtherAsync() { " + marker + " }"))
        self.assertEqual("d", label(edited=()))
        self.assertEqual("d", label(parity=manifest(acquired, changed)))

        for field, value in (("root_type", "Other"), ("authority_operation_id", "Other.Run"),
            ("syntax_ordinal", "3"), ("syntax_sha256", "F" * 64), ("work", "-"), ("effect", "missing")):
            self.assertEqual("d", label(row=changed._replace(**{field: value})), field)

    def test_scheduler_reach_table_binds_exact_rows_to_the_current_authored_calls(self) -> None:
        reaches = CLASSIFIER.EXACT_SOURCE_REACHES

        self.assertEqual({"RunAdmittedAsync": 45, "RunRemoteAdmittedAsync": 2, "PulseAsync": 13},
            {entry.caller_member: len(entry.rows) for entry in reaches})

        for entry in reaches:
            source = (REPOSITORY / entry.caller).read_text(encoding="utf-8")
            self.assertEqual(1, sum(body.count(entry.marker) for body in CLASSIFIER.member_bodies(source, entry.caller_member)))
            added = FakeSources(base={entry.caller: source.replace(entry.marker, "")}, head={entry.caller: source})
            unchanged = FakeSources(base={entry.caller: source}, head={entry.caller: source})
            edited = frozenset({entry.caller})

            for reviewed in entry.rows:
                self.assertIs(entry, CLASSIFIER.prove_reach(reviewed, "+", edited, added)[0])
                self.assertIsNone(CLASSIFIER.prove_reach(reviewed, "+", edited, unchanged)[0])
                self.assertIsNone(CLASSIFIER.prove_reach(reviewed, "-", edited, added)[0])

    def test_scheduler_reach_table_rejects_a_duplicate_or_unknown_cause(self) -> None:
        lines = CLASSIFIER.GUILD_SCHEDULER_REACH_PATH.read_text(encoding="utf-8").splitlines()
        header, columns, first = lines[:3]

        with tempfile.TemporaryDirectory() as folder:
            contract = Path(folder) / "unreviewed.tsv"
            for extra in (first, "unknown" + first[first.index("\t"):]):
                contract.write_text("\n".join((header, columns, first, extra)) + "\n", encoding="utf-8")
                with self.assertRaises(CLASSIFIER.ManifestError):
                    CLASSIFIER.read_guild_scheduler_reaches(contract)

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

        self.assertEqual(9, len(names))

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

    def test_legacy_wait_diagnostic_reaches_require_the_new_call_and_exact_root_site(self) -> None:
        caller = "src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildLocalWaitCheckpoint.cs"

        marker = "await ReadDiagnosticWaitSnapshotWithinAsync(connection, transaction, questId, continuationId.Value, ct)"

        entries = [entry for entry in CLASSIFIER.REACHED_THROUGH if entry.caller == caller and entry.marker == marker]

        self.assertEqual(56, len(entries))

        self.assertEqual(8, len({entry.operation for entry in entries}))

        for entry in entries:
            with self.subTest(root=entry.root_type, operation=entry.operation, member=entry.member, callee=entry.callee):
                row = site(entry.source_path, callee=entry.callee, member=entry.member,
                    root=entry.root_type, operation=entry.operation)

                def label(candidate=row, base="", head=marker, edited=(caller,), parity=manifest()):
                    retained = site("src/Retained.cs", root=candidate.root_type, operation=candidate.authority_operation_id)

                    result = classify(manifest(retained), manifest(retained, candidate), edited=edited, parity=parity,
                        sources=FakeSources(base={caller: base}, head={caller: head}))

                    return result[("+", candidate)].label

                self.assertEqual("r", label())

                self.assertEqual("d", label(head=""))

                self.assertEqual("d", label(base=marker))

                self.assertEqual("d", label(edited=()))

                self.assertEqual("d", label(parity=None))

                self.assertEqual("d", label(parity=manifest(row)))

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
                for member in ("ExecuteAsync", "RunAdmittedAsync", "RunLocalCompletionAdmittedAsync", "RunRemoteAdmittedAsync", "DrainCancellationAsync", "RunCommittedAsync", "Schedule", "StartAsync")),
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

    def test_guild_completion_handoff_is_named_exactly_without_authorizing_a_neighbor(self) -> None:
        operation = "RetroDownfall.Arcanum.Infrastructure.Guild.GuildLocalQuestScheduler.RunLocalCompletionAdmittedAsync"

        reviewed = site("src/Unedited.cs", root="GuildLocalQuestScheduler", operation=operation)

        neighbor = site("src/Unedited.cs", root="GuildLocalQuestScheduler", operation=operation + "Other")

        result = classify(manifest(), manifest(reviewed, neighbor))

        self.assertEqual("n", result[("+", reviewed)].label)

        self.assertEqual("d", result[("+", neighbor)].label)

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


class RegisteredRootManifestTests(unittest.TestCase):
    def test_v2_preserves_the_explicit_registered_root_column(self):
        row = site("src/Read.cs")
        columns = CLASSIFIER.LEGACY_COLUMNS + "\tregistered_root_work_frontier_capsule_sha256"
        fields = list(row[:13]) + ["A" * 64]
        parsed = CLASSIFIER.parse_manifest(
            "# arcanum-hosted-producer-capsules v2\n" + columns + "\n" + "\t".join(fields) + "\n", "v2"
        )
        self.assertEqual(14, len(parsed.rows[0]))
        self.assertEqual("A" * 64, parsed.rows[0][-1])

    def test_v1_does_not_infer_a_registered_root_from_physical_work(self):
        row = site("src/Read.cs", work="B" * 64)
        parsed = CLASSIFIER.parse_manifest(
            CLASSIFIER.LEGACY_HEADER + "\n" + CLASSIFIER.LEGACY_COLUMNS + "\n"
            + "\t".join(row[:13]) + "\n", "historical"
        )
        self.assertEqual("B" * 64, parsed.rows[0].work)
        self.assertEqual("-", parsed.rows[0].registered_root_work)
        self.assertEqual(1, parsed.version)

    def test_registered_root_reference_requires_its_own_operation_and_work_frontier(self):
        frontier = site("src/Worker.cs", callee=ACQUISITION, kind="EffectFrontier", admitted="WorkspaceIndexing")
        frontier = frontier._replace(work=frontier.capsule_id, registered_root_work=frontier.capsule_id)
        consumer = site("src/Read.cs")._replace(registered_root_work=frontier.capsule_id)
        self.assertEqual([], CLASSIFIER.unresolved_references(manifest_v2(frontier, consumer), consumer))
        self.assertEqual([frontier.capsule_id], CLASSIFIER.unresolved_references(manifest_v2(consumer), consumer))
        foreign = frontier._replace(authority_operation_id="Other.Run")
        self.assertEqual([frontier.capsule_id], CLASSIFIER.unresolved_references(manifest_v2(foreign, consumer), consumer))

    def test_changed_registered_root_is_not_only_an_effect_withdrawal(self):
        before = site("src/Read.cs", work="A" * 64, effect="B" * 64)._replace(registered_root_work="C" * 64)
        after = before._replace(effect="-", registered_root_work="D" * 64)
        self.assertFalse(CLASSIFIER.withdraws_effect(
            before, "-", CLASSIFIER.Context(manifest_v2(before), manifest_v2(), manifest_v2(after))))
        self.assertFalse(CLASSIFIER.withdraws_effect(
            after, "+", CLASSIFIER.Context(manifest_v2(before), manifest_v2(after), None)))
        same_root = before._replace(effect="-")
        self.assertTrue(CLASSIFIER.withdraws_effect(
            before, "-", CLASSIFIER.Context(manifest_v2(before), manifest_v2(), manifest_v2(same_root))))

    def test_registered_root_cannot_mix_foreign_contexts_with_the_same_capsule(self):
        frontier = site("src/Worker.cs", callee=ACQUISITION, kind="EffectFrontier", admitted="WorkspaceIndexing")
        frontier = frontier._replace(work=frontier.capsule_id, registered_root_work=frontier.capsule_id)
        consumer = site("src/Read.cs")._replace(registered_root_work=frontier.capsule_id)
        wrong_operation = frontier._replace(authority_operation_id="Other.Run")
        wrong_root = frontier._replace(root_type="Other")
        self.assertEqual([frontier.capsule_id], CLASSIFIER.unresolved_references(manifest_v2(wrong_root, consumer), consumer))
        self.assertEqual([frontier.capsule_id], CLASSIFIER.unresolved_references(manifest_v2(wrong_operation, wrong_root, consumer), consumer))
        for candidates in ((frontier, wrong_root), (wrong_root, frontier)):
            self.assertEqual([], CLASSIFIER.unresolved_references(manifest_v2(*candidates, consumer), consumer))


class RegisteredRootPhysicalContextTests(unittest.TestCase):
    @staticmethod
    def rows():
        root = site("src/Root.cs", callee=ACQUISITION, kind="EffectFrontier", admitted="LongRunningOperationRecovery")
        root = root._replace(work=root.capsule_id, registered_root_work=root.capsule_id)
        inner = site("src/Inner.cs", callee=ACQUISITION, kind="EffectFrontier", admitted="GuildStorage")
        inner = inner._replace(work=inner.capsule_id, registered_root_work=root.capsule_id)
        group = site("src/Group.cs", callee=GROUP, kind="EffectFrontier")
        group = group._replace(work=inner.capsule_id, effect=group.capsule_id, registered_root_work=root.capsule_id)
        consumer = site("src/Send.cs", callee="System.IO.File.Delete", kind="FileSystemEffect")
        consumer = consumer._replace(work=inner.capsule_id, effect=group.capsule_id, registered_root_work=root.capsule_id)
        return root, inner, group, consumer

    def test_sole_physical_targets_cannot_lend_another_registered_context(self):
        root, inner, group, consumer = self.rows()
        other = site("src/OtherRoot.cs", callee=ACQUISITION, kind="EffectFrontier", admitted="LongRunningOperationRecovery")
        other = other._replace(work=other.capsule_id, registered_root_work=other.capsule_id)
        forged = consumer._replace(registered_root_work=other.capsule_id)
        self.assertCountEqual([inner.capsule_id, group.capsule_id],
            CLASSIFIER.unresolved_references(manifest_v2(root, other, inner, group, forged), forged))

    def test_effect_target_must_match_current_physical_work(self):
        root, inner, group, consumer = self.rows()
        forged = consumer._replace(work=root.capsule_id)
        self.assertEqual([group.capsule_id], CLASSIFIER.unresolved_references(manifest_v2(root, inner, group, forged), forged))

    def test_two_root_contexts_select_the_explicit_one_without_filling_a_missing_root(self):
        root, inner, group, consumer = self.rows()
        other = site("src/OtherRoot.cs", callee=ACQUISITION, kind="EffectFrontier", admitted="LongRunningOperationRecovery")
        other = other._replace(work=other.capsule_id, registered_root_work=other.capsule_id)
        second_inner = inner._replace(registered_root_work=other.capsule_id)
        second_group = group._replace(registered_root_work=other.capsule_id)
        rows = manifest_v2(root, other, inner, second_inner, group, second_group, consumer)
        self.assertEqual([], CLASSIFIER.unresolved_references(rows, consumer))
        departed = consumer._replace(registered_root_work="-")
        self.assertCountEqual([inner.capsule_id, group.capsule_id], CLASSIFIER.unresolved_references(rows, departed))
        self.assertEqual("-", departed.registered_root_work)
        self.assertEqual([], CLASSIFIER.unresolved_references(manifest_v2(root, inner, group, departed), departed))



    def test_historical_acquisition_effect_does_not_require_the_new_root_pair(self):
        root, inner, group, _ = self.rows()
        other = site("src/OtherRoot.cs", callee=ACQUISITION, kind="EffectFrontier", admitted="LongRunningOperationRecovery")
        other = other._replace(work=other.capsule_id, registered_root_work=other.capsule_id)
        acquired = inner._replace(registered_root_work=other.capsule_id, effect=group.capsule_id)
        self.assertEqual([], CLASSIFIER.unresolved_references(manifest_v2(root, other, acquired, group), acquired))
        ambiguous = group._replace(registered_root_work=other.capsule_id)
        self.assertEqual([group.capsule_id], CLASSIFIER.unresolved_references(
            manifest_v2(root, other, acquired, group, ambiguous), acquired))

    def test_nonordinary_null_work_and_root_keep_only_a_unique_structural_effect(self):
        group = site("src/Group.cs", callee=GROUP, kind="EffectFrontier")
        group = group._replace(effect=group.capsule_id)
        consumer = site("src/Startup.cs", kind="FileSystemEffect")._replace(effect=group.capsule_id)
        self.assertEqual([], CLASSIFIER.unresolved_references(manifest_v2(group, consumer), consumer))
        self.assertEqual("-", consumer.work)
        self.assertEqual("-", consumer.registered_root_work)
        sibling = group._replace(registered_root_work="A" * 64)
        self.assertEqual([group.capsule_id], CLASSIFIER.unresolved_references(manifest_v2(group, sibling, consumer), consumer))

    def test_conflicting_intrinsic_aliases_are_rejected_before_root_selection(self):
        root, inner, group, consumer = self.rows()
        for conflicting in (inner._replace(admitted_work_kind="WorkspaceIndexing"), inner._replace(work=root.capsule_id)):
            conflicting = conflicting._replace(registered_root_work="A" * 64)
            self.assertEqual([inner.capsule_id], CLASSIFIER.unresolved_references(
                manifest_v2(root, inner, conflicting, group, consumer), consumer))

    def test_work_only_read_requires_proven_unrelated_incoming_groups(self):
        root = site('src/Root.cs', callee=ACQUISITION, kind='EffectFrontier', admitted='GuildMaintenance')
        root = root._replace(work=root.capsule_id, registered_root_work=root.capsule_id)
        parent = site('src/Parent.cs', callee=ACQUISITION, kind='EffectFrontier', admitted='GuildStorage')
        parent = parent._replace(work=parent.capsule_id, registered_root_work=root.capsule_id)
        first = site('src/FirstGroup.cs', callee=GROUP, kind='EffectFrontier')
        first = first._replace(work=root.capsule_id, registered_root_work=root.capsule_id, effect=first.capsule_id)
        second = site('src/SecondGroup.cs', callee=GROUP, kind='EffectFrontier')
        second = second._replace(work=parent.capsule_id, registered_root_work=root.capsule_id, effect=second.capsule_id)
        child = site('src/Child.cs', callee=ACQUISITION, kind='EffectFrontier', admitted='GuildStorage')
        child = child._replace(work=child.capsule_id, registered_root_work=root.capsule_id)
        a, b = child._replace(effect=first.capsule_id), child._replace(effect=second.capsule_id)
        read = site('src/Read.cs')._replace(work=child.capsule_id, registered_root_work=root.capsule_id)
        rows = [root, parent, first, second, a, b, read]
        self.assertEqual([], CLASSIFIER.unresolved_references(manifest_v2(*rows), read))
        for bad in ([root, parent, first, a, b, read], [root, parent, first, first, second, a, b, read],
                    [root, first, second, a, b, read], [root, parent, first, second, a, a, read],
                    [root, parent, parent._replace(admitted_work_kind="Other", registered_root_work="F" * 64), first, second, a, b, read],
                    [root, parent, first, second, a, b, read._replace(effect=first.capsule_id)]):
            with self.subTest(bad=bad):
                self.assertIn(child.capsule_id, CLASSIFIER.unresolved_references(manifest_v2(*bad), bad[-1]))

    def test_same_root_self_and_no_effect_exceptions_do_not_accept_duplicate_rows(self):
        root, inner, _, consumer = self.rows()
        incoming = inner._replace(effect="D" * 64)
        read = consumer._replace(kind="DatabaseAccess", effect="-")
        rows = manifest_v2(root, inner, incoming, read)
        self.assertEqual([], CLASSIFIER.unresolved_references(rows, inner))
        self.assertEqual([], CLASSIFIER.unresolved_references(rows, read))
        duplicate = manifest_v2(root, inner, inner, incoming, read)
        self.assertEqual([inner.capsule_id], CLASSIFIER.unresolved_references(duplicate, inner))
        self.assertEqual([inner.capsule_id], CLASSIFIER.unresolved_references(duplicate, read))

    def test_acquisition_callee_predicate_matches_the_csharp_manifest_contract(self):
        root = site("src/Root.cs", callee="Fixture.Gate.TryAcquireWorkLease", kind="EffectFrontier", admitted="WorkspaceIndexing")
        root = root._replace(work=root.capsule_id, registered_root_work=root.capsule_id)
        consumer = site("src/Read.cs")._replace(work=root.capsule_id, registered_root_work=root.capsule_id)
        self.assertEqual([], CLASSIFIER.unresolved_references(manifest_v2(root, consumer), consumer))


class VersionMigrationAndStartupWitnessTests(unittest.TestCase):
    def test_v1_unknown_provenance_pairs_losslessly_with_v2_and_preserves_counts(self):
        work = site('src/Work.cs', callee=ACQUISITION, kind='EffectFrontier', admitted='GuildStorage')
        work = work._replace(work=work.capsule_id, registered_root_work=work.capsule_id)
        old = site('src/Reader.cs', work=work.capsule_id)
        new = old._replace(registered_root_work=work.capsule_id)
        result = CLASSIFIER.classify(manifest(old, old), manifest_v2(work, new), frozenset(), manifest_v2(work, new))
        paired = [item for item in result if item.label == 'v1-provenance']
        self.assertEqual(2, len(paired))
        self.assertEqual(1, sum(item.side == '-' and item.row == old for item in paired))
        self.assertEqual(1, sum(item.side == '+' and item.row == new for item in paired))
        self.assertEqual(1, sum(item.side == '-' and item.row == old and item.label != 'v1-provenance' for item in result))
        self.assertTrue(all('unknown' in item.detail for item in paired))

    def test_v1_unknown_and_v2_none_require_exact_parity_witness(self):
        row = site('src/Reader.cs')
        with_witness = CLASSIFIER.classify(manifest(row), manifest_v2(row), frozenset(), manifest_v2(row))
        without_witness = CLASSIFIER.classify(manifest(row), manifest_v2(row), frozenset(), manifest_v2())
        self.assertEqual(['v1-provenance', 'v1-provenance'], [x.label for x in with_witness])
        self.assertEqual(2, len(without_witness))
        self.assertTrue(all(x.label == 'd' for x in without_witness))
        later = row._replace(registered_root_work='A' * 64)
        delta = CLASSIFIER.classify(manifest_v2(row), manifest_v2(later), frozenset(), manifest_v2(row))
        self.assertTrue(all(x.label != 'v1-provenance' for x in delta))

    def test_v1_removal_stages_against_its_unique_parity_refinement(self):
        work = site('src/Work.cs', callee=ACQUISITION, kind='EffectFrontier', admitted='GuildStorage')
        work = work._replace(work=work.capsule_id, registered_root_work=work.capsule_id)
        old = site('src/Reader.cs', work=work.capsule_id)
        p = manifest_v2(work, old._replace(registered_root_work=work.capsule_id))
        result = CLASSIFIER.classify(manifest(work._replace(registered_root_work='-'), old), manifest_v2(), frozenset(), p)
        removal = next(x for x in result if x.side == '-' and x.row == old)
        self.assertEqual('source', removal.stage)

    def test_paired_legacy_provider_is_not_a_removed_call(self):
        row = site('src/Provider.cs', kind='ProviderCall', root=PID_ROOT, operation=PID_OPERATION)
        c, n, p = manifest(row), manifest_v2(row), manifest_v2(row)
        labelled = CLASSIFIER.classify(c, n, frozenset(), p)
        removal = next(x for x in CLASSIFIER.check_invariants(c, n, labelled, p) if x.name == 'provider-call-removals')
        self.assertEqual([], removal.violations)

    def test_unframed_legacy_none_requires_parity_and_preserves_occurrence_counts(self):
        row = site('src/Provider.cs', kind='ProviderCall', root=PID_ROOT, operation=PID_OPERATION)
        def violations(c, n, p):
            return next(x.violations for x in CLASSIFIER.check_invariants(c, n, parity=p) if x.name == 'provider-call-frames')
        self.assertTrue(violations(manifest(row), manifest_v2(row), manifest_v2()))
        self.assertEqual([], violations(manifest(row), manifest_v2(row), manifest_v2(row)))
        self.assertEqual(1, len(violations(manifest(row), manifest_v2(row, row), manifest_v2(row))))

    def test_unframed_v1_provenance_requires_exact_parity_before_treating_as_existing(self):
        work = site('src/Work.cs', callee=ACQUISITION, kind='EffectFrontier', admitted='GuildStorage', root=PID_ROOT, operation=PID_OPERATION)
        work = work._replace(work=work.capsule_id, registered_root_work=work.capsule_id)
        old = site('src/Provider.cs', kind='ProviderCall', root=PID_ROOT, operation=PID_OPERATION)
        new = old._replace(registered_root_work=work.capsule_id)
        def violations(parity):
            return next(x.violations for x in CLASSIFIER.check_invariants(manifest(old), manifest_v2(work, new), parity=parity)
                        if x.name == 'provider-call-frames')
        self.assertEqual([], violations(manifest_v2(work, new)))
        self.assertTrue(violations(manifest_v2(work)))

    def test_unresolved_v2_provenance_is_not_paired(self):
        old = site('src/Reader.cs')
        new = old._replace(registered_root_work='A' * 64)
        result = CLASSIFIER.classify(manifest(old), manifest_v2(new), frozenset())
        self.assertTrue(all(item.label != 'v1-provenance' for item in result))

    def test_physical_guild_work_separation_requires_exact_parity_and_kinds(self):
        root = site('src/Root.cs', callee=ACQUISITION, kind='EffectFrontier', admitted='GuildMaintenance')
        root = root._replace(work=root.capsule_id, registered_root_work=root.capsule_id)
        inner = site('src/Inner.cs', callee=ACQUISITION, kind='EffectFrontier', admitted='GuildStorage')
        inner = inner._replace(work=inner.capsule_id, registered_root_work=root.capsule_id)
        old = site('src/Reader.cs', work=root.capsule_id)
        new = old._replace(work=inner.capsule_id, registered_root_work=root.capsule_id)
        p = manifest_v2(root, inner, new)
        old_root = root._replace(registered_root_work='-')
        result = CLASSIFIER.classify(manifest(old_root, old), manifest_v2(root, inner, new), frozenset(), p)
        matches = [x for x in result if x.row in (old, new)]
        self.assertEqual(['c', 'c'], sorted(x.label for x in matches))
        self.assertTrue(all(x.rule == 'physical-work-root-separation' for x in matches))
        for bad in (new._replace(work=root.capsule_id), new._replace(effect='F' * 64),
                    new._replace(syntax_sha256='F' * 64), new._replace(registered_root_work='F' * 64)):
            result = CLASSIFIER.classify(manifest(old_root, old), manifest_v2(root, inner, bad), frozenset(), p)
            self.assertFalse(any(x.row == bad and x.rule == 'physical-work-root-separation' for x in result))
        result = CLASSIFIER.classify(manifest(old_root, old), manifest_v2(root, inner, new), frozenset(), manifest_v2())
        self.assertFalse(any(x.rule == 'physical-work-root-separation' for x in result))

    def test_physical_work_rule_rejects_mutated_parity_and_candidate_contexts(self):
        root = site('src/Root.cs', callee=ACQUISITION, kind='EffectFrontier', admitted='GuildMaintenance')
        root = root._replace(work=root.capsule_id, registered_root_work=root.capsule_id)
        inner = site('src/Inner.cs', callee=ACQUISITION, kind='EffectFrontier', admitted='GuildStorage')
        inner = inner._replace(work=inner.capsule_id, registered_root_work=root.capsule_id)
        old = site('src/Reader.cs', work=root.capsule_id)
        new = old._replace(work=inner.capsule_id, registered_root_work=root.capsule_id)
        effect = site('src/Group.cs', callee=GROUP, kind='EffectFrontier')
        effect = effect._replace(work=inner.capsule_id, registered_root_work=root.capsule_id, effect=effect.capsule_id)
        cases = [
            (root, inner._replace(admitted_work_kind='Other'), new, []),
            (root._replace(admitted_work_kind='Other'), inner, new, []),
            (root, inner, new._replace(effect=effect.capsule_id), [effect]),
            (root, inner, new._replace(registered_root_work=inner.capsule_id), []),
            (root, inner, new._replace(admitted_work_kind='GuildStorage'), []),
        ]
        for r, w, candidate, extra in cases:
            with self.subTest(root=r, work=w, candidate=candidate):
                parity = manifest_v2(r, w, candidate, *extra)
                context = CLASSIFIER.Context(manifest(root._replace(registered_root_work='-'), old), parity, parity)
                self.assertFalse(CLASSIFIER.physical_work_root_separation(candidate, '+', context))

    def test_spend_reach_callers_follow_the_actual_identity_and_quest_paths(self):
        identities = {'ExecuteAsync', 'ReadGenerationAsync', 'GetNativeOperatorPrincipalWithinAsync',
                      'ReadCatalogAsync', 'ReadIndexColumnsAsync', 'ReadIndexListAsync'}
        quest = {'AuthorizeLocateAsync', 'CurrentIdentityAsync', 'ScopeAsync', 'RegistrationAsync',
                 'ValidateOwnersAsync', 'RequireInboundRootWithinAsync', 'RequireInboundRootForPeerWithinAsync'}
        for _, path, member, _, group in CLASSIFIER._GUILD_SPEND_REACH_SITES:
            if path.startswith(('Guild/GuildLocalHostPolicy', 'Guild/GuildWorkspaceLeaseStore', 'Data/Schema/')):
                with self.subTest(path=path, member=member):
                    self.assertEqual('identity' if member in identities else 'quest', group)
                    self.assertIn(member, identities | quest)

    def test_spend_refresh_reach_is_exact_and_requires_new_caller(self):
        entry = CLASSIFIER.GUILD_SPEND_REFRESH_REACHES[0]
        row = site(entry.source_path, callee=entry.callee, member=entry.member, root=entry.root_type, operation=entry.operation)
        edited = frozenset({entry.caller})
        sources = FakeSources(base={entry.caller: ''}, head={entry.caller: entry.marker})
        self.assertIsNotNone(CLASSIFIER.prove_reach(row, '+', edited, sources)[0])
        self.assertIsNone(CLASSIFIER.prove_reach(row._replace(authority_operation_id='Other'), '+', edited, sources)[0])
        self.assertIsNone(CLASSIFIER.prove_reach(row, '+', edited, FakeSources(base={entry.caller: ''}, head={entry.caller: ''}))[0])
        self.assertIsNone(CLASSIFIER.prove_reach(row, '+', frozenset(), sources)[0])

    def test_exact_selected_startup_group_witness_only(self):
        root = 'LongRunningOperationStartupHostedService'
        operation = ('RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.StartAsync::call:'
                     'RetroDownfall.Arcanum.Infrastructure.Operations.LongRunningOperationStartupHostedService.RunStartupPassAsync#0~ABC')
        group = site('src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildSpendRefreshService.cs', callee=GROUP,
                     kind='EffectFrontier', member='RunClaimedAsync', root=root, operation=operation)
        group = group._replace(enclosing_type='RetroDownfall.Arcanum.Infrastructure.Guild.GuildSpendRefreshService')
        provider = site('src/RetroDownfall.Arcanum.Infrastructure/Guild/GuildOpenRouterSpendReader.cs', callee='System.IO.Stream.ReadAsync', kind='ProviderCall',
                        member='ReadAsync', root=root, operation=operation, effect=group.capsule_id)
        provider = provider._replace(enclosing_type='RetroDownfall.Arcanum.Infrastructure.Guild.GuildSpendObservation')
        def violations(row, witness):
            return next(x.violations for x in CLASSIFIER.check_invariants(manifest(), manifest_v2(witness, row))
                        if x.name == 'provider-call-frames')
        self.assertEqual([], violations(provider, group))
        conflicting = group._replace(work='B' * 64)
        ambiguous = next(x.violations for x in CLASSIFIER.check_invariants(manifest(), manifest_v2(group, conflicting, provider))
                         if x.name == 'provider-call-frames')
        self.assertTrue(ambiguous)
        self.assertTrue(violations(provider._replace(registered_root_work='C' * 64), group))
        for changed, witness in ((provider._replace(effect='-'), group),
                                 (provider._replace(effect='F' * 64), group),
                                 (provider._replace(callee='Unknown.Send'), group),
                                 (provider._replace(authority_operation_id=operation.replace('RunStartupPassAsync', 'Other')), group),
                                 (provider._replace(authority_operation_id='ContinueInBackgroundAsync'), group),
                                 (provider, group._replace(member='Other'))):
            self.assertTrue(violations(changed, witness))


if __name__ == "__main__":
    unittest.main()
