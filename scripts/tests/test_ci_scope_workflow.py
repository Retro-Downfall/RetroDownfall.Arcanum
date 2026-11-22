"""Exercise the CI authority and pin conservative documentation-only job wiring."""

import os
import json
import re
import shutil
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / ".github/workflows/ci.yml"
WORKERS = (
    "hosted-producer-primary-analysis", "hosted-producer-additional-analysis",
    "hosted-producer-fixture-analysis", "hosted-producer-fixture-build-analysis",
    "hosted-producer-serve-host-analysis",
)


def job(name):
    source = WORKFLOW.read_text()
    match = re.search(r"^  " + re.escape(name) + r":\n(.*?)(?=^  \S|\Z)", source, re.M | re.S)
    if match is None:
        raise AssertionError("Missing CI job: " + name)
    return match.group(1)


def step(body, name):
    match = re.search(r"^      - name: " + re.escape(name) + r"\n(.*?)(?=^      - name:|\Z)", body, re.M | re.S)
    if match is None:
        raise AssertionError("Missing CI step: " + name)
    return match.group(1)


def matrix_hosts(body, platform):
    """Evaluate the workflow's closed JSON matrix branches, not a copied platform map."""
    value = re.search(r"^        os: (.+)$", body, re.M)
    if value is None:
        return [None]
    value = value.group(1)
    if value.startswith("["):
        return [host.strip() for host in value.strip("[]").split(",")]
    expression = re.fullmatch(r"\$\{\{ fromJSON\((.*)\) \}\}", value)
    if expression is None:
        raise AssertionError("Unsupported matrix expression: " + value)
    branches = expression.group(1).split(" || ")
    for branch in branches[:-1]:
        match = re.fullmatch(r"inputs\.execution-platform == '([^']+)' && '([^']+)'", branch)
        if match is None:
            raise AssertionError("Unsupported matrix branch: " + branch)
        if platform == match.group(1):
            return json.loads(match.group(2))
    fallback = re.fullmatch(r"'([^']+)'", branches[-1])
    if fallback is None:
        raise AssertionError("Unsupported matrix fallback: " + branches[-1])
    return json.loads(fallback.group(1))


def selected_inventory(event, qualification, platform, full="true", win_x64="verified", win_arm64="verified"):
    """Execute the simple job guards and matrix values declared in this workflow."""
    context = {"github.event_name": event, "inputs.qualification": qualification,
               "inputs.execution-platform": platform, "needs.scope.outputs.full": full,
               "needs.native-sqlcipher-assets.outputs.win-x64": win_x64,
               "needs.native-sqlcipher-assets.outputs.win-arm64": win_arm64}

    def atom(value):
        if value == "always()":
            return True
        match = re.fullmatch(r"([\w.-]+) (==|!=) '([^']*)'", value)
        if match is None or match.group(1) not in context:
            raise AssertionError("Unsupported job guard: " + value)
        equal = context[match.group(1)] == match.group(3)
        return equal if match.group(2) == "==" else not equal

    selected, skipped = [], []
    for name in re.findall(r"^  ([\w-]+):", WORKFLOW.read_text().split("\njobs:\n", 1)[1], re.M):
        body = job(name)
        guard = re.search(r"^    if: (.+)$", body, re.M)
        enabled = guard is None or any(all(atom(value) for value in branch.split(" && "))
                                       for branch in guard.group(1).split(" || "))
        if enabled:
            selected.extend((name, host) for host in matrix_hosts(body, platform))
        else:
            skipped.append(name)
    return selected, skipped


class CiScopeWorkflowTests(unittest.TestCase):
    def test_static_job_provisions_ripgrep_before_running_the_shipping_gate_fixtures(self):
        body = job("static-analysis")
        provision = step(body, "Install ripgrep")
        self.assertIn("apt-get install -y ripgrep", provision)
        self.assertLess(body.index(provision), body.index("python3 -m unittest discover"))

    def test_final_native_qualification_reuses_full_windows_authority(self):
        self.assertNotIn("  guild-final-native:", WORKFLOW.read_text())
        for name, rid in (("windows-suite", "win-x64"), ("windows-arm64-suite", "win-arm64")):
            body = job(name)
            self.assertIn("ARCANUM_EXPECTED_WINDOWS_RID: " + rid, body)
            self.assertIn("ARCANUM_PUBLISHED_EXECUTABLE", body)
            self.assertIn("WindowsAppContainerBrokerTests", step(body, "Test Arcanum"))
            for command in ("./scripts/qualify-guild-execution.sh",
                            "./scripts/verify-native-sqlcipher.sh --rid " + rid + " --strict"):
                self.assertIn(command, body)
            self.assertIn("./scripts/verify-aot-il-warnings.sh " + rid, body)
            self.assertIn("./scripts/verify-guild-aot.sh --rid " + rid, body)
            self.assertIn("./scripts/verify-shipping-publish.sh --rid " + rid, body)
            self.assertIn("--retain-publish-dir", body)
            self.assertIn("if: inputs.qualification == 'guild-final'", step(body, "Qualify Guild execution and lifecycle"))
            self.assertIn("if: always()", step(body, "Preserve final Guild evidence"))

    def test_final_windows_shipping_receipts_bind_retained_smoked_apphosts(self):
        for name, rid in (("windows-suite", "win-x64"), ("windows-arm64-suite", "win-arm64")):
            with self.subTest(job=name):
                body = job(name)
                evidence = step(body, "Record final Windows shipping identity")
                self.assertIn("if: inputs.qualification == 'guild-final'", evidence)
                for witness in ("$env:ARCANUM_PUBLISHED_EXECUTABLE", "$env:GITHUB_SHA", "git rev-parse HEAD",
                                "git status --porcelain --untracked-files=all", "--version", "Get-FileHash",
                                "Get-ChildItem", "-Recurse", "dotnet --info", "dotnet --version",
                                "expectedRid = '" + rid + "'", "executableSha256", "publishFiles",
                                ".tmp/guild-final-native/"):
                    self.assertIn(witness, evidence)
                self.assertLess(body.index("--retain-publish-dir"), body.index("name: Record final Windows shipping identity"))
                self.assertIn(".tmp/guild-final-native/", step(body, "Preserve final Guild evidence"))

    def test_final_windows_forge_acl_witnesses_are_executed_and_counted(self):
        for name in ("windows-suite", "windows-arm64-suite"):
            witness = step(job(name), "Test final Windows Forge permissions")
            self.assertIn("if: inputs.qualification == 'guild-final'", witness)
            self.assertIn("RetroDownfall.TheForge.Tests.csproj", witness)
            self.assertIn("FullyQualifiedName~RetroDownfall.TheForge.Tests.TheForgeOwnerOnlyPermissionsTests", witness)
            self.assertIn("$results.Count -ne 2", witness)
            self.assertIn("$_.outcome -ne 'Passed'", witness)
            self.assertIn("forge-windows.trx", witness)
            self.assertIn("RetroDownfall.TheForge.Tests.TheForgeOwnerOnlyPermissionsTests.$name", witness)
            self.assertIn(".tmp/windows-forge/", step(job(name), "Preserve final Guild evidence"))

    def test_final_guild_qualification_requires_both_native_jobs_and_authority(self):
        selected, _ = selected_inventory("workflow_dispatch", "guild-final", "both")
        self.assertEqual({("scope", None), ("sdk", None),
                          ("native-sqlcipher-assets", None), ("static-analysis", None),
                          ("windows-suite", None), ("windows-arm64-suite", None),
                          ("guild-final-authority", None)}, set(selected))
        for x64, arm in (("pending", "verified"), ("verified", "pending"), ("", "")):
            selected, _ = selected_inventory("workflow_dispatch", "guild-final", "both", full="false",
                                             win_x64=x64, win_arm64=arm)
            self.assertTrue({("windows-suite", None), ("windows-arm64-suite", None),
                             ("guild-final-authority", None)} <= set(selected))

        authority = job("guild-final-authority")
        self.assertIn("always()", authority)
        self.assertIn("windows-suite", authority)
        self.assertIn("windows-arm64-suite", authority)
        for required in ("needs.windows-suite.result", "needs.windows-arm64-suite.result",
                         "needs.native-sqlcipher-assets.outputs.win-x64", "needs.native-sqlcipher-assets.outputs.win-arm64"):
            self.assertIn(required, authority)
        for profile in ("full", "guild-memory", "guild-budget", "guild-workspace",
                        "guild-execution", "guild-operator", "guild-a2a"):
            selected, _ = selected_inventory("workflow_dispatch", profile, "both")
            self.assertFalse(any(name == "guild-final-authority" for name, _ in selected))

    def test_final_authority_fails_when_any_native_job_or_asset_is_missing(self):
        body = step(job("guild-final-authority"), "Require both Windows qualification results")
        script = body.split("run: |\n", 1)[1]
        script = "\n".join(line[10:] for line in script.splitlines() if line.startswith("          "))
        keys = ("windows-suite.result", "windows-arm64-suite.result",
                "native-sqlcipher-assets.outputs.win-x64", "native-sqlcipher-assets.outputs.win-arm64")
        def evaluate(values):
            code = re.sub(r"\$\{\{ needs\.([\w.-]+) \}\}", lambda match: values[match.group(1)], script)
            self.assertNotIn("${{", code)
            return subprocess.run(["bash", "-c", code], capture_output=True, text=True).returncode
        good = dict(zip(keys, ("success", "success", "verified", "verified")))
        self.assertEqual(0, evaluate(good))
        for key in keys:
            for bad in ("", "skipped", "failure", "cancelled", "pending"):
                with self.subTest(key=key, value=bad):
                    self.assertNotEqual(0, evaluate({**good, key: bad}))

    def test_execution_platform_choice_defaults_to_both_without_affecting_other_jobs(self):
        source = WORKFLOW.read_text()
        match = re.search(r"^      execution-platform:\n(.*?)(?=^      [^ ]|^permissions:)", source, re.M | re.S)
        self.assertIsNotNone(match, "The focused execution dispatch needs an explicit platform choice")
        declaration = match.group(1)
        self.assertRegex(declaration, r"(?m)^        type: choice$")
        self.assertRegex(declaration, r"(?m)^        default: both$")
        self.assertEqual(["both", "macos", "windows"], re.findall(r"^          - ([\w-]+)$", declaration, re.M))
        names = re.findall(r"^  ([\w-]+):", source.split("\njobs:\n", 1)[1], re.M)
        for name in names:
            if name not in ("guild-execution", "guild-operator"):
                self.assertNotIn("inputs.execution-platform", job(name), name)

    def test_execution_matrix_selects_only_the_requested_platform_and_preserves_default_inventory(self):
        support = {("scope", None), ("sdk", None), ("native-sqlcipher-assets", None), ("static-analysis", None)}
        for platform, hosts in (("both", ["macos-26", "windows-latest"]),
                                ("", ["macos-26", "windows-latest"]),
                                ("macos", ["macos-26"]), ("windows", ["windows-latest"])):
            with self.subTest(platform=platform):
                selected, skipped = selected_inventory("workflow_dispatch", "guild-execution", platform)
                self.assertEqual(support | {("guild-execution", host) for host in hosts}, set(selected))
                self.assertEqual(4 + len(hosts), len(selected))
                self.assertEqual(17, len(skipped))

    def test_execution_platform_choice_does_not_change_full_pr_or_other_focused_inventories(self):
        for event, profile, full in (("pull_request", "", "true"), ("pull_request", "", "false"),
                                     ("workflow_dispatch", "full", "true"),
                                     ("workflow_dispatch", "guild-memory", "true"),
                                     ("workflow_dispatch", "guild-budget", "true"),
                                     ("workflow_dispatch", "guild-workspace", "true")):
            baseline = selected_inventory(event, profile, "both", full)
            for platform in ("macos", "windows", ""):
                with self.subTest(event=event, profile=profile, full=full, platform=platform):
                    self.assertEqual(baseline, selected_inventory(event, profile, platform, full))

    def test_execution_job_has_budget_for_measured_continuing_windows_work(self):
        focused = job("guild-execution")
        self.assertRegex(focused, r"(?m)^    timeout-minutes: 90$")
        self.assertIn("fail-fast: false", focused)
        self.assertNotIn("continue-on-error", focused)
        self.assertIn("if: always()", step(focused, "Upload focused evidence"))
        self.assertIn("name: guild-execution-qualification-${{ matrix.os }}", focused)
        self.assertRegex(job("guild-workspace"), r"(?m)^    timeout-minutes: 60$")

    def test_execution_qualifier_requires_windows_permission_witnesses_only_on_windows(self):
        windows_cases = (
            "RetroDownfall.Arcanum.Tests.GrimoireTransitions.GrimoireOfflineTransitionJournalFileStoreTests."
            "Windows_owner_only_application_accepts_an_enumerated_modify_only_read_handle",
            "RetroDownfall.Arcanum.Tests.GrimoireTransitions.GrimoireOfflineTransitionJournalFileStoreTests."
            "Windows_owner_only_application_refuses_a_weakened_same_file_without_changing_its_acl",
        )
        for host, required in (("Darwin", False), ("MINGW64_NT-10.0", True)):
            with self.subTest(host=host), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                scripts = root / "scripts"
                scripts.mkdir()
                harness = scripts / "qualify-guild-execution.sh"
                shutil.copyfile(ROOT / "scripts/qualify-guild-execution.sh", harness)
                commands = root / "commands"
                commands.mkdir()
                captured = root / "validator-arguments.txt"
                collector = root / "validator-probe.py"
                collector.write_text("""
import os
from pathlib import Path
import subprocess
import sys

arguments = sys.argv[1:]
windows_command = subprocess.list2cmdline(["python3.exe", *arguments])
if os.environ["QUALIFIER_TEST_HOST"].startswith("MINGW") and len(windows_command.encode("utf-16-le")) // 2 + 1 > 32767:
    sys.exit("The qualifier exceeded the Windows process argument limit.")

if "--requirements-stdin" in arguments:
    payload = sys.stdin.buffer.read()
    if not payload.endswith(b"\\0"):
        sys.exit("The selector list was not a complete NUL-delimited stream.")
    arguments.extend(payload[:-1].decode("utf-8").split("\\0"))

with Path(os.environ["QUALIFIER_TEST_CAPTURE"]).open("a", encoding="utf-8") as stream:
    stream.writelines(argument + "\\n" for argument in arguments)
""")
                stubs = {
                    "dotnet": "#!/bin/sh\nexit 0\n",
                    "git": "#!/bin/sh\nexit 0\n",
                    "uname": '#!/bin/sh\nprintf "%s\\n" "$QUALIFIER_TEST_HOST"\n',
                    "python3": '#!/bin/sh\nexec "$QUALIFIER_TEST_PYTHON" "$QUALIFIER_TEST_COLLECTOR" "$@"\n',
                }
                for name, content in stubs.items():
                    command = commands / name
                    command.write_text(content)
                    command.chmod(0o755)
                result = subprocess.run(
                    ["bash", str(harness)], cwd=root, capture_output=True, text=True,
                    env={"PATH": str(commands) + os.pathsep + os.defpath,
                         "QUALIFIER_TEST_HOST": host, "QUALIFIER_TEST_CAPTURE": str(captured),
                         "QUALIFIER_TEST_PYTHON": sys.executable, "QUALIFIER_TEST_COLLECTOR": str(collector)},
                    timeout=10,
                )
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                arguments = captured.read_text().splitlines()
                required_cases = [arguments[index + 1] for index, argument in enumerate(arguments)
                                  if argument == "--required-case"]
                for case in windows_cases:
                    with self.subTest(case=case):
                        self.assertEqual(required_cases.count(case), 1 if required else 0, case)
                self.assertIn("RetroDownfall.Arcanum.Tests.Guild.GuildWorkspaceLeaseHostLockTests."
                              + ("Windows_retained_no_delete_sharing_prevents_lock_leaf_replacement" if required
                                 else "Replaced_lock_file_or_reparse_path_revokes_admission"), required_cases)

    def test_focused_execution_profile_requires_both_hosts_and_execution_evidence(self):
        focused = job("guild-execution")
        self.assertIn("if: github.event_name == 'workflow_dispatch' && inputs.qualification == 'guild-execution'", focused)
        self.assertEqual(["macos-26", "windows-latest"], matrix_hosts(focused, "both"))
        self.assertIn("run: ./scripts/qualify-guild-execution.sh", focused)
        self.assertNotIn("continue-on-error", focused)
        for name in ("build-test", *WORKERS, "hosted-producer-analysis", "windows-suite",
                     "windows-arm64-suite", "aot-il", "macos-workspace-check"):
            with self.subTest(job=name):
                self.assertRegex(job(name), r"(?m)^    if: .*inputs\.qualification != 'guild-execution'")
        harness = (ROOT / "scripts/qualify-guild-execution.sh").read_text()
        for name in ("GuildLocalHostPolicyTests", "GuildQuestStoreTests", "GuildLocalExecutionSchemaTests",
                     "LongRunningOperationRecoveryOwnershipTests", "ApprenticeServiceReliabilityTests",
                     "WizardIntelligenceProviderTests", "BudgetReservation", "DataRetention", "AiReflection",
                     "GrimoireOfflineTransitionJournalFileStoreTests", "GrimoireOfflineTransitionJournalStoreTests"):
            self.assertIn(name, harness)
        self.assertIn("validate_guild_workspace_evidence.py", harness)
        self.assertIn("--required-case", harness)
        journal_file_class = "RetroDownfall.Arcanum.Tests.GrimoireTransitions.GrimoireOfflineTransitionJournalFileStoreTests"
        journal_store_class = "RetroDownfall.Arcanum.Tests.GrimoireTransitions.GrimoireOfflineTransitionJournalStoreTests"
        for name in ("GrimoireOfflineTransitionJournalFileStoreTests", "GrimoireOfflineTransitionJournalStoreTests"):
            self.assertIn("FullyQualifiedName~" + name, harness)
        # File-store platform cases legitimately skip on the other host; require
        # executed cross-platform witnesses without requiring those skips to pass.
        self.assertNotRegex(harness, r"(?m)^\s*" + re.escape(journal_file_class) + r"\s*\\?\s*$")
        self.assertRegex(harness, r"(?m)^\s*" + re.escape(journal_store_class) + r"\s*\\\s*$")
        for method in ("Publication_bounds_the_number_of_directory_enumerations_per_revision",
                       "Deletion_does_not_repeat_the_absence_proof_without_an_intervening_state_change",
                       "Resume_reports_recovery_required_when_enumeration_fails_with_an_io_exception",
                       "Secure_reread_returns_the_exact_published_identity_and_bytes",
                       "Owner_only_application_accepts_an_enumerated_read_handle",
                       "Owner_only_application_refuses_a_replaced_named_file_without_changing_its_permissions"):
            with self.subTest(required_journal_case=method):
                self.assertIn("--required-case " + journal_file_class + "." + method, harness)
        for suffix, methods in (
            ("Intelligence.WizardIntelligenceProviderTests", (
                "Guild_wizard_entry_refuses_unknown_binding_without_ambient",
                "Guild_ordinary_context_path_is_unavailable_before_provider_or_embedding_effects",
                "Spell_parent_pivot_during_hydration_never_discloses_external_content_to_provider")),
            ("Guild.GuildWorkspaceLeaseBrokerTests", (
                "Expiry_does_not_steal_admitted_writer_and_copied_permit_cannot_release_it",
                "Terminal_acquire_replay_survives_file_replaced_by_directory_on_every_shipping_platform")),
            ("Guild.WorkspaceLeaseIdentityTests", (
                "Parent_and_root_replacement_invalidate_evidence",
                "Durable_evidence_round_trips_and_malformed_ancestry_is_refused")),
        ):
            required_class = "RetroDownfall.Arcanum.Tests." + suffix
            with self.subTest(platform_skippable_class=required_class):
                self.assertNotRegex(harness, r"(?m)^\s*" + re.escape(required_class) + r"\s*\\?\s*$")
            for method in methods:
                with self.subTest(required_cross_platform_case=method):
                    self.assertIn("--required-case " + required_class + "." + method, harness)
        self.assertIn("FullyQualifiedName~Guild", harness)
        self.assertIn("FullyQualifiedName~ExactUsd", harness)
        for name in ("ExactUsdTextTests", "ExactUsdTextAccumulationTests"):
            self.assertRegex(harness, r"(?m)^\s*RetroDownfall\.Arcanum\.Tests\.Data\."
                             + name + r"\s*\\\s*$")
        self.assertIn("--required-case RetroDownfall.Arcanum.Tests.Data.ExactUsdTextTests."
                      "Authoritative_money_boundaries_use_the_exact_codec_and_new_owners_require_review", harness)
        self.assertRegex(harness, r"(?m)^\s*RetroDownfall\.Arcanum\.Tests\.A2A\."
                         r"A2ASendingAccountingTests\s*\\\s*$")
        self.assertIn("FullyQualifiedName~WizardIntelligenceProviderTests", harness)
        self.assertIn("FullyQualifiedName~WorkspaceLease", harness)
        for method in ("Authored_overrides_of_framework_virtual_members_that_reach_a_site_are_exactly_the_reviewed_types",
                       "Authored_disposals_that_reach_a_site_and_belong_to_the_container_are_exactly_the_reviewed_types"):
            with self.subTest(required_limit_pin=method):
                self.assertIn("|FullyQualifiedName~" + method, harness)
                self.assertIn("--required-case RetroDownfall.Arcanum.Tests.Build.CheckerLimitPinTests." + method, harness)
        for method in ("Completed_task_pruning_requires_exact_observation_and_final_drain",
                       "In_memory_json_writer_requires_closed_backend_encoder_and_lifetime",
                       "Workspace_lease_dispatch_requires_complete_census_and_preserves_effect_frontiers",
                       "Reference_comparer_singleton_requires_exact_framework_getter_identity",
                       "Nested_read_work_does_not_borrow_an_unrelated_outer_effect",
                       "Nested_owned_admission_pairs_the_inner_work_and_effect",
                       "Conditional_effect_guard_remains_unproved_despite_a_constant_true_argument",
                       "Engine_wait_dispatch_preserves_held_port_body_and_refuses_implementation_drift",
                       "Actual_workspace_recovery_adapter_joins_its_broker_before_returning",
                       "Workspace_host_evidence_dispatch_traverses_both_bodies_and_refuses_census_drift",
                       "Empty_unused_enclosing_admission_entries_do_not_split_parent_callable_identity",
                       "Owner_bound_ambient_recovery_identity_preserves_semantic_distinctions",
                       "Self_admitted_task_handoff_uses_its_unique_declared_catalog_owner",
                       "Host_owned_task_handoff_uses_its_unique_declared_catalog_owner",
                       "ManifestNullEffectWorkOnlyConsumersResolveOneExactAcquisitionContext",
                       "ManifestNullEffectWorkOnlyConsumersPreserveStrictFrontierBoundaries"):
            with self.subTest(required_source_proof=method):
                self.assertIn("|FullyQualifiedName~" + method, harness)
                self.assertIn("--required-case RetroDownfall.Arcanum.Tests.Operations."
                              "HostedGrimoireProducerInventoryTests." + method, harness)
        self.assertIn("tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj", harness)
        self.assertIn("compendium-execution.trx", harness)
        self.assertIn("compendium-evidence-summary.json", harness)
        self.assertIn("--required-case RetroDownfall.Compendium.Ux.Tests.Compendium.GenericSettingsPreservationTests."
                      "Editing_guild_gate_publishes_the_opt_in_without_changing_other_settings", harness)
        for name in (
            "Api.GuildEndpointTests.Native_operator_executes_through_real_scheduler_engine_HTTP_adapter_and_durable_result",
            "Api.GuildEndpointTests.Native_parked_question_remains_discoverable_only_at_the_proven_input_frontier",
            "Api.GuildCapacityTests.Native_blocking_delegation_releases_shared_capacity_and_resumes_parent_at_capacity_one",
            "Api.GuildCapacityTests.Native_parallel_units_wait_for_owned_physical_call_settlement_without_canceling_a_live_sibling",
            "Guild.GuildLocalHostPolicyTests.Run_bound_Mcp_cast_uses_one_child_coordinator_and_replays_exact_identity",
            "Guild.GuildLocalHostPolicyTests.Queued_Mcp_cast_rechecks_exact_live_host_frame_before_creating_work",
            "Guild.GuildLocalHostPolicyTests.Local_scheduler_protects_every_physical_readiness_probe_with_work_admission",
            "Guild.GuildLocalHostPolicyTests.Local_scheduler_hot_enable_requires_effect_admission_before_installing_physical_protection",
            "Guild.GuildLocalHostPolicyTests.Guild_host_readiness_physical_stage_refuses_closed_effect_admission_after_identity_commit",
            "Guild.GuildLocalHostPolicyTests.Local_scheduler_discovery_fault_cancels_and_joins_its_waiting_pump",
            "Guild.GuildLocalHostPolicyTests.Local_port_paid_completion_closes_account_and_preserves_unknown_price",
            "Guild.GuildQuestStoreTests.Provider_effect_timeout_keeps_real_daily_hold_past_expiry_and_refuses_another_identity",
            "Guild.GuildQuestStoreTests.Guild_shared_engine_admits_each_unit_with_its_own_Guild_work_and_effect_lease",
            "Guild.GuildQuestStoreTests.Local_child_frontier_separates_explicit_interruption_from_resumable_or_live_descendants",
            "Guild.GuildQuestStoreTests.Local_child_frontier_does_not_count_its_own_interruption_as_a_descendant",
            "Operations.LongRunningOperationRecoveryOwnershipTests.Generic_recovery_claims_process_ownership_before_lease_acquisition_and_releases_after_settlement",
            "Hosting.ApprenticeServiceReliabilityTests.Guild_cutover_never_resumes_legacy_crash_recovery_into_execution_capacity",
            "Backup.BackupRestoreServiceTests.A_full_archive_restores_onto_a_clean_machine_without_the_source_credential_store",
            "Backup.BackupRestoreStagedDrainTests.An_archive_already_at_head_needs_no_drain",
            "Data.DataRetentionServiceTests.Factory_reset_refuses_local_execution_evidence_whose_quest_link_is_missing",
            "Guild.GuildLocalExecutionSchemaTests.V8_upgrade_preserves_content_and_erasure_evidence_and_converges_with_fresh_schema",
            "Guild.GuildQuestStoreTests.Local_binding_restore_preserves_identity_but_refuses_execution_input",
        ):
            with self.subTest(required_execution_case=name):
                self.assertIn("--required-case RetroDownfall.Arcanum.Tests." + name, harness)

        self.assertNotIn("FullyQualifiedName~BackupRestore", harness)

    def test_focused_execution_requires_native_operator_surface_evidence(self):
        harness = (ROOT / "scripts/qualify-guild-execution.sh").read_text()
        for suite in ("Cli.GuildCommandTests", "Cli.ApprenticeCommandTests", "Cli.CliSurfaceTests",
                      "Cli.CliCompletionTests", "Cli.CliCompletionResolverTests", "Cli.CliSuggestionTests",
                      "Cli.ResourceSelectorTests", "Cli.ResourceSelectorCrossGenerationTests",
                      "Cli.CliApiClientDeadlineInventoryTests", "Build.CliInfrastructureBoundaryTests",
                      "Api.GrimoireStreamingRouteInventoryTests"):
            self.assertRegex(harness, r"(?m)^\s*" + re.escape("RetroDownfall.Arcanum.Tests." + suite) + r"\s*\\\s*$")
            if suite not in ("Cli.GuildCommandTests", "Cli.ApprenticeCommandTests"):
                self.assertIn("FullyQualifiedName~" + suite.split(".")[-1], harness)
        for case in (
            "Cli.GuildCommandTests.Guild_owner_selectors_resolve_a_unique_uuid_prefix",
            "Cli.GuildCommandTests.Guild_content_uuid_prefix_uses_authorized_metadata_resolution_for_message_content",
            "Cli.GuildCommandTests.Guild_watch_cancellation_exits_130_and_disposes_the_active_subscription",
            "Api.GuildEndpointTests.Operator_roster_refresh_pages_workspace_sources_and_refuses_changed_source_sets",
            "Guild.GuildLocalHostPolicyTests.Native_operator_workspace_authority_uses_current_registration_without_minting_canonical_Run_pins",
            "Guild.GuildLocalHostPolicyTests.Native_operator_leases_conflict_across_registered_aliases_without_canonical_Run_records",
            "Guild.GuildLocalHostPolicyTests.Native_operator_lease_renewal_refuses_a_replacement_physical_root",
        ):
            self.assertIn("--required-case RetroDownfall.Arcanum.Tests." + case, harness)

    def test_guild_final_checks_both_coverage_threshold_engines_on_windows(self):
        for lane in ("windows-suite", "windows-arm64-suite"):
            source = job(lane)
            self.assertIn("name: Test coverage threshold engine parity", source)
            self.assertIn("python -m unittest scripts/coverage_threshold_test.py", source)
            self.assertIn("assert shutil.which('pwsh')", source)

    def test_focused_guild_profile_is_explicit_and_does_not_claim_full_authority(self):
        source = WORKFLOW.read_text()
        self.assertIn("default: full", source)
        focused = job("guild-memory")
        self.assertIn("if: github.event_name == 'workflow_dispatch' && inputs.qualification == 'guild-memory'", focused)
        self.assertIn("run: ./scripts/qualify-guild-memory.sh", focused)
        self.assertNotIn("continue-on-error", focused)
        for name in ("build-test", *WORKERS, "hosted-producer-analysis", "windows-suite",
                     "windows-arm64-suite", "aot-il", "macos-workspace-check"):
            with self.subTest(job=name):
                self.assertRegex(job(name), r"(?m)^    if: .*inputs\.qualification != 'guild-memory'")
        self.assertNotRegex(job("static-analysis"), r"(?m)^    if:")

    def test_focused_guild_budget_profile_executes_billing_and_schema_contracts(self):
        source = WORKFLOW.read_text()
        self.assertIn("          - guild-budget", source)
        focused = job("guild-budget")
        self.assertIn("if: github.event_name == 'workflow_dispatch' && inputs.qualification == 'guild-budget'", focused)
        self.assertIn("run: ./scripts/qualify-guild-budget.sh", focused)
        self.assertNotIn("continue-on-error", focused)
        for name in ("build-test", *WORKERS, "hosted-producer-analysis", "windows-suite",
                     "windows-arm64-suite", "aot-il", "macos-workspace-check"):
            with self.subTest(job=name):
                self.assertRegex(job(name), r"(?m)^    if: .*inputs\.qualification != 'guild-budget'")
        harness = (ROOT / "scripts/qualify-guild-budget.sh").read_text()
        for name in ("GuildBudget", "BudgetReservation", "TurnAccounting", "DelegatedManaTracker",
                     "InferenceAccounting", "DataRetention", "GuildQuest", "ExactUsd"):
            self.assertIn(name, harness)
        self.assertIn("Missing, skipped or failed required Guild budget contract", harness)
        self.assertNotIn("continue-on-error", harness)

    def test_focused_workspace_profile_requires_both_native_platforms_and_executed_cases(self):
        focused = job("guild-workspace")
        self.assertIn("if: github.event_name == 'workflow_dispatch' && inputs.qualification == 'guild-workspace'", focused)
        self.assertIn("os: [macos-26, windows-latest]", focused)
        self.assertNotIn("continue-on-error", focused)
        for name in ("build-test", *WORKERS, "hosted-producer-analysis", "windows-suite",
                     "windows-arm64-suite", "aot-il", "macos-workspace-check"):
            with self.subTest(job=name):
                self.assertRegex(job(name), r"(?m)^    if: .*inputs\.qualification != 'guild-workspace'")
        harness = (ROOT / "scripts/qualify-guild-workspace.sh").read_text()
        for name in ("GuildWorkspaceLeaseSignetPolicyTests", "WorkspaceMutationFenceWriterTests",
                     "WorkspaceMutationFenceProcessTests", "WorkspaceLeaseDirectoryCreationTests",
                     "WorkspaceLeaseDirectoryRemovalTests", "SdkMcpClientWrapperTests", "FamiliarProcessRunner",
                     "CappedChildProcess", "CampaignBackedWorkspaceRegistry", "ArcanumErrorMapper"):
            self.assertIn(name, harness)
        for name in ("Windows_retained_no_delete_sharing_prevents_lock_leaf_replacement",
                     "Replaced_lock_file_or_reparse_path_revokes_admission"):
            self.assertIn("--required-case", harness)
            self.assertIn(name, harness)
        self.assertNotIn("continue-on-error", harness)

    def test_guild_qualification_requires_model_workspace_effects_and_retained_authority_cases(self):
        for profile in ("workspace", "execution"):
            with self.subTest(profile=profile):
                harness = (ROOT / f"scripts/qualify-guild-{profile}.sh").read_text()
                for method in (
                    "Provider_reads_lists_and_searches_only_its_physical_workspace_with_financial_controls_off",
                    "Retained_workspace_function_refuses_disclosure_after_loss_of_authority",
                    "Provider_mutates_only_its_Quest_workspace_and_commits_the_patch_receipt",
                    "Quest_lease_tools_manage_the_same_Quest_owned_epoch_and_refuse_a_sibling_path",
                ):
                    selector = f"--required-case RetroDownfall.Arcanum.Tests.Guild.GuildLocalHostPolicyTests.{method}"
                    self.assertIn(selector, harness)
                self.assertIn("RetroDownfall.Arcanum.Tests.Guild.GuildWorkspaceReadToolsTests", harness)

    def test_guild_final_requires_the_independent_host_journey_to_pass(self):
        method = ("RetroDownfall.Arcanum.Tests.Guild.GuildLocalHostPolicyTests."
                  "Financial_off_mixed_cohort_uses_two_running_Arcanum_hosts_and_replays_after_restart")
        harness = (ROOT / "scripts/qualify-guild-execution.sh").read_text()
        self.assertIn(f"--required-case {method} ", harness)

    def test_guild_execution_requires_authenticated_portal_delivery_and_recovery(self):
        harness = (ROOT / "scripts/qualify-guild-execution.sh").read_text()
        for method in (
            "Provider_informational_tool_reaches_the_authenticated_waiting_peer_once",
            "Lost_remote_portal_acknowledgement_survives_driver_replacement_without_resending_the_body",
            "Mixed_cohort_information_preserves_local_application_and_remote_withholding",
        ):
            self.assertIn(f"--required-case RetroDownfall.Arcanum.Tests.Guild.GuildLocalHostPolicyTests.{method}", harness)

    def test_scope_uses_event_shas_and_full_history_without_shell_interpolation(self):
        body = job("scope")
        self.assertIn("fetch-depth: 0", body)
        self.assertIn("full: ${{ steps.classify.outputs.full }}", body)
        for binding in (
            "CI_EVENT: ${{ github.event_name }}",
            "CI_BASE: ${{ github.event.pull_request.base.sha }}",
            "CI_PR_HEAD: ${{ github.event.pull_request.head.sha }}",
        ):
            self.assertIn(binding, body)
        command = step(body, "Classify tested change")
        self.assertIn('python3 scripts/ci_scope.py --event "$CI_EVENT"', command)
        self.assertIn('--base "$CI_BASE" --head "$GITHUB_SHA" --pr-head "$CI_PR_HEAD"', command)
        self.assertIn('>> "$GITHUB_OUTPUT"', command)
        self.assertNotIn("continue-on-error", body)

    def test_native_and_producer_jobs_only_skip_on_verified_documentation_scope(self):
        for name in (*WORKERS, "windows-suite", "windows-arm64-suite", "aot-il", "macos-workspace-check"):
            with self.subTest(job=name):
                body = job(name)
                self.assertRegex(body, r"(?m)^    needs: \[[^\n]*\bscope\b")
                self.assertRegex(body, r"(?m)^    if: needs\.scope\.outputs\.full == 'true'")
                self.assertNotIn("continue-on-error", body)
        for name, rid in (("windows-suite", "win-x64"), ("windows-arm64-suite", "win-arm64")):
            self.assertIn("needs.native-sqlcipher-assets.outputs." + rid + " == 'verified'", job(name))

    def test_existing_required_build_job_owns_documentation_success(self):
        body = job("build-test")
        self.assertIn("name: Build, test, coverage", body)
        self.assertIn("needs: [sdk, scope]", body)
        self.assertIn("if: inputs.qualification != 'guild-memory'", body)
        docs = step(body, "Test documentation contracts")
        self.assertIn("if: needs.scope.outputs.full == 'false'", docs)
        self.assertIn("set -euo pipefail", docs)
        self.assertIn("dotnet test tests/RetroDownfall.Arcanum.Tests", docs)
        self.assertIn("FullyQualifiedName~Documentation", docs)
        self.assertIn("Markdown_inventory_table_has_exact_json_row_parity", docs)
        self.assertIn("Every_stated_limit_has_a_pin_and_the_design_still_states_it", docs)
        self.assertIn("if: needs.scope.outputs.full == 'true'", step(body, "Arcanum tests + coverage thresholds"))
        self.assertIn("if: needs.scope.outputs.full == 'true'", step(body, "Test the macOS signing helpers"))
        desktops = step(body, "Test Compendium and The Forge")
        self.assertNotIn("if:", desktops)

    def run_authority(self, full="true", scope="success", build="success", workers=None):
        body = job("hosted-producer-analysis")
        code = step(body, "Require successful selected qualification").split("run: |\n", 1)[1]
        code = "\n".join(line[10:] for line in code.splitlines() if line.startswith("          "))
        results = {"scope.outputs.full": full, "scope.result": scope, "build-test.result": build}
        results.update({name + ".result": value for name, value in zip(WORKERS, workers or ["success"] * 5)})
        code = re.sub(r"\$\{\{ needs\.([\w.-]+) \}\}", lambda match: results[match.group(1)], code)
        self.assertNotIn("${{", code)
        return subprocess.run(["bash", "-c", code], capture_output=True, text=True)

    def test_authority_accepts_full_evidence_or_successful_docs_with_all_workers_skipped(self):
        self.assertEqual(0, self.run_authority().returncode)
        self.assertEqual(0, self.run_authority(full="false", workers=["skipped"] * 5).returncode)

    def test_authority_refuses_missing_classification_failed_docs_and_missing_full_workers(self):
        for full in ("true", "false", ""):
            workers = ["skipped" if full == "false" else "success"] * 5
            for result in ("failure", "cancelled", "skipped", ""):
                with self.subTest(full=full, result=result):
                    self.assertNotEqual(0, self.run_authority(full=full, scope=result, workers=workers).returncode)
                    self.assertNotEqual(0, self.run_authority(full=full, build=result, workers=workers).returncode)
            for index in range(5):
                damaged = workers.copy()
                damaged[index] = "success" if full == "false" else "skipped"
                self.assertNotEqual(0, self.run_authority(full=full, workers=damaged).returncode)
        self.assertNotEqual(0, self.run_authority(full="").returncode)

    def test_source_receipt_consumers_remain_full_only(self):
        body = job("hosted-producer-analysis")
        self.assertIn("    if: always()", body)
        self.assertIn("      - scope", body)
        self.assertIn("      - build-test", body)
        for name in (
            "Download primary source analysis evidence", "Download additional source analysis evidence",
            "Download fixture source analysis evidence", "Download fixture build source analysis evidence",
            "Download serve host source analysis evidence", "Reconcile complete source analysis evidence",
        ):
            self.assertIn("if: needs.scope.outputs.full == 'true'", step(body, name))
        self.assertIn("if: always() && needs.scope.outputs.full == 'true'", step(body, "Upload source analysis authority"))


if __name__ == "__main__":
    unittest.main()
