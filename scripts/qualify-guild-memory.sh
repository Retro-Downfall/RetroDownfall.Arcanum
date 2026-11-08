#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

results=.tmp/guild-memory-qualification
mkdir -p "$results"
dotnet --info > "$results/sdk-info.txt"
git rev-parse HEAD > "$results/commit.txt"
git status --porcelain --untracked-files=all > "$results/worktree-status.txt"

filter='Category!=Perf&Category!=HostedProducerAnalysis&(FullyQualifiedName~Guild|FullyQualifiedName~Lexicon|FullyQualifiedName~Saga|FullyQualifiedName~MemoryRecall|FullyQualifiedName~MemoryDelegation|FullyQualifiedName~SessionAttachment|FullyQualifiedName~Tapestry|FullyQualifiedName~Annals|FullyQualifiedName~MemoryErasure|FullyQualifiedName~MemoryReset|FullyQualifiedName~DataRetention|FullyQualifiedName~CovenantArtifact|FullyQualifiedName~Documentation|FullyQualifiedName~CovenantDerivedOutputInventoryTests|FullyQualifiedName~EfNativeAotBoundaryTests|FullyQualifiedName~SqliteSqlCompositionBoundaryTests|FullyQualifiedName~GrimoireRepositorySqlBoundaryTests|FullyQualifiedName~AiReflection|FullyQualifiedName~ArcanumConventionTests)'
dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  -c Release --filter "$filter" --blame-hang-timeout 15m --blame-hang-dump-type none \
  --logger 'trx;LogFileName=guild-memory.trx' --logger 'console;verbosity=normal' --results-directory "$results"

python3 - "$results/guild-memory.trx" <<'PY'
import sys
import xml.etree.ElementTree as ET

root = ET.parse(sys.argv[1]).getroot()
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
results = root.findall(".//t:UnitTestResult", ns)
if not results or any(r.get("outcome") not in ("Passed", "NotExecuted") for r in results):
    raise SystemExit("Focused qualification requires executed tests with no failures.")
required = {
    "RetroDownfall.Arcanum.Tests.Guild.GuildMemoryAuthorityTests",
    "RetroDownfall.Arcanum.Tests.Guild.GuildMemorySchemaTests",
    "RetroDownfall.Arcanum.Tests.Guild.GuildMemoryAttributionLifecycleTests",
    "RetroDownfall.Arcanum.Tests.Guild.GuildSagaTranscriptAccessTests",
    "RetroDownfall.Arcanum.Tests.Guild.GuildContextBoundaryTests",
    "RetroDownfall.Arcanum.Tests.Lexicon.GuildLexiconAccessTests",
    "RetroDownfall.Arcanum.Tests.Intelligence.WizardIntelligenceProviderTests",
}
definitions = root.findall(".//t:UnitTest", ns)
for name in sorted(required):
    ids = {d.get("id") for d in definitions
           if d.find("t:TestMethod", ns) is not None
           and d.find("t:TestMethod", ns).get("className") == name}
    executed = [r for r in results if r.get("testId") in ids]
    if not executed or any(r.get("outcome") != "Passed" for r in executed):
        raise SystemExit(f"Missing, skipped or failed required Guild memory contract: {name}")
factory = [r for r in results if "Factory_reset_explicitly_deletes_and_reconciles_Guild_attribution_without_foreign_keys" in r.get("testName", "")]
if len(factory) != 2 or any(r.get("outcome") != "Passed" for r in factory):
    raise SystemExit("Both Guild attribution factory-reset cases must execute and pass.")
print(f"Focused Guild memory evidence: {len(results)} results; every required memory contract passed.")
PY
