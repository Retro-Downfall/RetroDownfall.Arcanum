#!/usr/bin/env bash
# Opt-in A2A transport evidence on one macOS host; broader profiles remain unchanged.
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

if [[ "$(uname -s)" != Darwin ]]; then
  printf '%s\n' 'Guild A2A qualification requires macOS.' >&2
  exit 1
fi

results=.tmp/guild-a2a-qualification
mkdir -p "$results"
rm -f -- "$results/core.trx" "$results/core-summary.json"
dotnet --info > "$results/sdk-info.txt"
git rev-parse HEAD > "$results/commit.txt"
git status --porcelain --untracked-files=all > "$results/worktree-status.txt"
cp tests/RetroDownfall.Arcanum.Tests/TestData/Contracts/Arcanum.GuildA2AQualification.json "$results/witness-manifest.json"

python3 scripts/guild_operator_qualification.py filter --project core --platform macos \
  --manifest "$results/witness-manifest.json" \
  --settings-output "$results/core.runsettings" > "$results/core-filter.txt"

dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  -c Release --settings "$results/core.runsettings" \
  --blame-hang-timeout 15m --blame-hang-dump-type none \
  --logger 'trx;LogFileName=core.trx' --logger 'console;verbosity=normal' --results-directory "$results"
python3 scripts/guild_operator_qualification.py validate --project core --platform macos \
  --manifest "$results/witness-manifest.json" \
  --report "$results/core.trx" --summary-json "$results/core-summary.json"
