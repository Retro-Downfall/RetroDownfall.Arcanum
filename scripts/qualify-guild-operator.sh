#!/usr/bin/env bash
# Opt-in operator evidence; the broader guild-execution profile is unchanged.
set -euo pipefail

repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

case "$(uname -s)" in
  Darwin) operator_platform=macos ;;
  MINGW*|MSYS*|CYGWIN*) operator_platform=windows ;;
  *) printf '%s\n' 'Guild operator qualification requires macOS or Windows.' >&2; exit 1 ;;
esac

results=.tmp/guild-operator-qualification
mkdir -p "$results"
rm -f -- "$results/core.trx" "$results/compendium.trx" \
  "$results/core-summary.json" "$results/compendium-summary.json"
dotnet --info > "$results/sdk-info.txt"
git rev-parse HEAD > "$results/commit.txt"
git status --porcelain --untracked-files=all > "$results/worktree-status.txt"
cp tests/RetroDownfall.Arcanum.Tests/TestData/Contracts/Arcanum.GuildOperatorQualification.json "$results/witness-manifest.json"

# Runsettings carry the complete filter without Windows command-line length/truncation limits.
python3 scripts/guild_operator_qualification.py filter --project compendium --platform "$operator_platform" \
  --manifest "$results/witness-manifest.json" \
  --settings-output "$results/compendium.runsettings" > "$results/compendium-filter.txt"
python3 scripts/guild_operator_qualification.py filter --project core --platform "$operator_platform" \
  --manifest "$results/witness-manifest.json" \
  --settings-output "$results/core.runsettings" > "$results/core-filter.txt"

dotnet test tests/RetroDownfall.Compendium.Tests/RetroDownfall.Compendium.Tests.csproj \
  -c Release --settings "$results/compendium.runsettings" \
  --blame-hang-timeout 15m --blame-hang-dump-type none \
  --logger 'trx;LogFileName=compendium.trx' --logger 'console;verbosity=normal' --results-directory "$results"
python3 scripts/guild_operator_qualification.py validate --project compendium --platform "$operator_platform" \
  --manifest "$results/witness-manifest.json" \
  --report "$results/compendium.trx" --summary-json "$results/compendium-summary.json"

dotnet test tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj \
  -c Release --settings "$results/core.runsettings" \
  --blame-hang-timeout 15m --blame-hang-dump-type none \
  --logger 'trx;LogFileName=core.trx' --logger 'console;verbosity=normal' --results-directory "$results"
python3 scripts/guild_operator_qualification.py validate --project core --platform "$operator_platform" \
  --manifest "$results/witness-manifest.json" \
  --report "$results/core.trx" --summary-json "$results/core-summary.json"
