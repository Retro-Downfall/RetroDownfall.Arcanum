#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

results=.tmp/guild-workspace-qualification
mkdir -p "$results"
rm -f -- "$results/guild-workspace.trx" "$results/evidence-summary.json"
dotnet --info > "$results/sdk-info.txt"
git rev-parse HEAD > "$results/commit.txt"
git status --porcelain --untracked-files=all > "$results/worktree-status.txt"

filter='Category!=Perf&Category!=HostedProducerAnalysis&(FullyQualifiedName~Guild|FullyQualifiedName~WorkspaceMutation|FullyQualifiedName~WorkspaceLease|FullyQualifiedName~PhysicalFileSystemWriter|FullyQualifiedName~SandboxedFileIo|FullyQualifiedName~MultiFileCommit|FullyQualifiedName~AtomicFileTests|FullyQualifiedName~FileHandleIdentity|FullyQualifiedName~SpellRepository|FullyQualifiedName~SpellDirectory|FullyQualifiedName~CodexEndpoint|FullyQualifiedName~WorkspaceEndpoint|FullyQualifiedName~DataRetention|FullyQualifiedName~GrimoireSchema|FullyQualifiedName~Documentation|FullyQualifiedName~ConstraintInventory|FullyQualifiedName~UnrestrictedHarness|FullyQualifiedName~ContinuousIntegrationWorkflow|FullyQualifiedName~EfNativeAot|FullyQualifiedName~SqliteSqlCompositionBoundaryTests|FullyQualifiedName~GrimoireRepositorySqlBoundaryTests|FullyQualifiedName~AiReflection|FullyQualifiedName~ArcanumConventionTests|FullyQualifiedName~ArcanumErrorMapper|FullyQualifiedName~SdkMcpClientWrapperTests|FullyQualifiedName~FamiliarProcessRunner|FullyQualifiedName~CappedChildProcess|FullyQualifiedName~WorkspaceCheck|FullyQualifiedName~CampaignBackedWorkspaceRegistry|FullyQualifiedName~GrimoireTurnWriterTests|FullyQualifiedName~BackupRestore|FullyQualifiedName~WorkspaceResetScopeIndexSchemaTests|FullyQualifiedName~OwnershipProvenanceIndexSchemaTests)|FullyQualifiedName~Guild_workspace_native|FullyQualifiedName~Guild_workspace_boundary_names'
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  -c Release --filter "$filter" --blame-hang-timeout 15m --blame-hang-dump-type none \
  --logger 'trx;LogFileName=guild-workspace.trx' --logger 'console;verbosity=normal' --results-directory "$results"

host_lock_case=RetroDownfall.Arcanum.Tests.Guild.GuildWorkspaceLeaseHostLockTests.
case "$(uname -s)" in
  Darwin) host_lock_case+=Replaced_lock_file_or_reparse_path_revokes_admission ;;
  MINGW*|MSYS*|CYGWIN*) host_lock_case+=Windows_retained_no_delete_sharing_prevents_lock_leaf_replacement ;;
  *) printf '%s\n' 'Focused workspace qualification requires macOS or Windows.' >&2; exit 1 ;;
esac

python3 scripts/validate_guild_workspace_evidence.py "$results/guild-workspace.trx" \
  --summary-json "$results/evidence-summary.json" \
  RetroDownfall.Arcanum.Tests.Guild.GuildWorkspaceLeaseStoreTests \
  RetroDownfall.Arcanum.Tests.Guild.GuildWorkspaceLeaseBrokerTests \
  RetroDownfall.Arcanum.Tests.Guild.WorkspaceLeaseIdentityTests \
  RetroDownfall.Arcanum.Tests.Guild.GuildWorkspaceLeaseSignetPolicyTests \
  RetroDownfall.Arcanum.Tests.Guild.WorkspaceLeaseDirectoryCreationTests \
  RetroDownfall.Arcanum.Tests.Guild.WorkspaceLeaseDirectoryRemovalTests \
  RetroDownfall.Arcanum.Tests.Workspaces.WorkspaceMutationFenceWriterTests \
  --required-case "$host_lock_case" \
  --required-case RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Guild_workspace_native_imports_have_exact_physical_effect_kinds \
  --required-case RetroDownfall.Arcanum.Tests.Operations.HostedGrimoireProducerInventoryTests.Guild_workspace_boundary_names_on_unreviewed_declaring_types_stay_unclassified \
  --required-case RetroDownfall.Arcanum.Tests.Workspaces.WorkspaceMutationFenceProcessTests.Sdk_transport_refuses_before_connect_even_without_Guild_ambient \
  --required-case RetroDownfall.Arcanum.Tests.Workspaces.WorkspaceMutationFenceProcessTests.Installed_fence_refuses_workspace_check_before_jail_probe
