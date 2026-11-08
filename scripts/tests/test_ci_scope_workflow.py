"""Exercise the CI authority and pin conservative documentation-only job wiring."""

import re
from pathlib import Path
import subprocess
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


class CiScopeWorkflowTests(unittest.TestCase):
    def test_focused_execution_profile_requires_both_hosts_and_execution_evidence(self):
        focused = job("guild-execution")
        self.assertIn("if: github.event_name == 'workflow_dispatch' && inputs.qualification == 'guild-execution'", focused)
        self.assertIn("os: [macos-26, windows-latest]", focused)
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
                       "Secure_reread_returns_the_exact_published_identity_and_bytes"):
            with self.subTest(required_journal_case=method):
                self.assertIn("--required-case " + journal_file_class + "." + method, harness)
        for suffix, methods in (
            ("Intelligence.WizardIntelligenceProviderTests", (
                "Guild_wizard_entry_refuses_unknown_binding_without_ambient",
                "Guild_ordinary_context_path_is_unavailable_before_provider_or_embedding_effects")),
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
        self.assertIn("FullyQualifiedName~WizardIntelligenceProviderTests", harness)
        self.assertIn("FullyQualifiedName~WorkspaceLease", harness)
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
