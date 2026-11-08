#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

results=.tmp/guild-budget-qualification
mkdir -p "$results"
dotnet --info > "$results/sdk-info.txt"
git rev-parse HEAD > "$results/commit.txt"
git status --porcelain --untracked-files=all > "$results/worktree-status.txt"

filter='Category!=Perf&Category!=HostedProducerAnalysis&(FullyQualifiedName~Guild|FullyQualifiedName~BudgetReservation|FullyQualifiedName~TurnAccounting|FullyQualifiedName~DelegatedManaTracker|FullyQualifiedName~InferenceAccounting|FullyQualifiedName~DailySpend|FullyQualifiedName~ExternalSpend|FullyQualifiedName~DataRetention|FullyQualifiedName~ExactUsd|FullyQualifiedName~GrimoireSchema|FullyQualifiedName~Documentation|FullyQualifiedName~ConstraintInventory|FullyQualifiedName~UnrestrictedHarness|FullyQualifiedName~ContinuousIntegrationWorkflow|FullyQualifiedName~EfNativeAot|FullyQualifiedName~SqliteSqlCompositionBoundaryTests|FullyQualifiedName~GrimoireRepositorySqlBoundaryTests|FullyQualifiedName~AiReflection|FullyQualifiedName~ArcanumConventionTests)'
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  -c Release --filter "$filter" --blame-hang-timeout 15m --blame-hang-dump-type none \
  --logger 'trx;LogFileName=guild-budget.trx' --logger 'console;verbosity=normal' --results-directory "$results"

python3 - "$results/guild-budget.trx" <<'PY'
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
results = root.findall(".//t:UnitTestResult", ns)
if not results or any(r.get("outcome") not in ("Passed", "NotExecuted") for r in results):
    raise SystemExit("Focused qualification requires executed tests with no failures.")
required = {
    "RetroDownfall.Arcanum.Tests.Guild.GuildBudgetLedgerTests",
    "RetroDownfall.Arcanum.Tests.Data.Schema.GuildBudgetSchemaTests",
    "RetroDownfall.Arcanum.Tests.Guild.GuildQuestStoreTests",
    "RetroDownfall.Arcanum.Tests.Data.BudgetReservationServiceTests",
    "RetroDownfall.Arcanum.Tests.Data.InferenceAccountingStoreTests",
    "RetroDownfall.Arcanum.Tests.Intelligence.DelegatedManaTrackerTests",
}
definitions = root.findall(".//t:UnitTest", ns)
for name in sorted(required):
    ids = {d.get("id") for d in definitions
           if d.find("t:TestMethod", ns) is not None
           and d.find("t:TestMethod", ns).get("className") == name}
    executed = [r for r in results if r.get("testId") in ids]
    if not executed or any(r.get("outcome") != "Passed" for r in executed):
        raise SystemExit(f"Missing, skipped or failed required Guild budget contract: {name}")
print(f"Focused Guild budget evidence: {len(results)} results; every required budget contract passed.")
PY
