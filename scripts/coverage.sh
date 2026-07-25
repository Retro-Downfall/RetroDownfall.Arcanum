#!/usr/bin/env bash
# Run the test suite with coverage, render an HTML report, and (optionally)
# enforce the tiered coverage gates documented in docs/tests.README.md.
#
# Usage:
#   scripts/coverage.sh              # collect coverage + write HTML report
#   scripts/coverage.sh --threshold  # also enforce line/branch/security gates
#
# Exit codes:
#   0  coverage collected (and thresholds met, when --threshold is passed)
#   1  test failure, no coverage produced, or threshold not met
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"

TEST_PROJECT="$ROOT/tests/RetroDownfall.Arcanum.Tests/RetroDownfall.Arcanum.Tests.csproj"

RUNSETTINGS="$ROOT/tests/RetroDownfall.Arcanum.Tests/coverage.runsettings"

OUT_DIR="$ROOT/.tmp/coverage"

REPORT_DIR="$OUT_DIR/report"

THRESHOLD=0

for arg in "$@"; do
  case "$arg" in
    --threshold) THRESHOLD=1 ;;
    -h | --help)
      sed -n '2,12p' "$0"
      exit 0
      ;;
    *)
      echo "coverage.sh: unknown argument '$arg'" >&2
      exit 1
      ;;
  esac
done

rm -rf "$OUT_DIR"

mkdir -p "$OUT_DIR"

# Local tools (reportgenerator) come from .config/dotnet-tools.json.
dotnet tool restore >/dev/null

# The XPlat Code Coverage collector + its include/exclude filters are declared
# in coverage.runsettings; --settings both enables and configures it.
# --collect is required so VSTest actually attaches the XPlat collector.
dotnet test "$TEST_PROJECT" \
  --collect:"XPlat Code Coverage" \
  --settings "$RUNSETTINGS" \
  --results-directory "$OUT_DIR"

COBERTURA="$(find "$OUT_DIR" -name 'coverage.cobertura.xml' | head -n 1)"

if [ -z "$COBERTURA" ]; then
  echo "coverage.sh: no cobertura report was produced under $OUT_DIR" >&2
  exit 1
fi

# HTML report is best-effort — never let a reporting hiccup mask the gate.
if dotnet reportgenerator \
  "-reports:$COBERTURA" \
  "-targetdir:$REPORT_DIR" \
  "-reporttypes:Html;TextSummary" >/dev/null 2>&1; then
  echo "Coverage report: $REPORT_DIR/index.html"
else
  echo "coverage.sh: warning — reportgenerator failed; skipping HTML report" >&2
fi

if [ "$THRESHOLD" -eq 1 ]; then
  if command -v python3 >/dev/null 2>&1 && python3 -c 'import sys' >/dev/null 2>&1; then
    python3 "$ROOT/scripts/coverage_threshold.py" "$COBERTURA"
  elif command -v python >/dev/null 2>&1 && python -c 'import sys' >/dev/null 2>&1; then
    python "$ROOT/scripts/coverage_threshold.py" "$COBERTURA"
  elif command -v pwsh >/dev/null 2>&1; then
    pwsh -NoProfile -File "$ROOT/scripts/coverage_threshold.ps1" "$COBERTURA"
  elif command -v powershell.exe >/dev/null 2>&1; then
    powershell.exe -NoProfile -ExecutionPolicy Bypass \
      -File "$(cygpath -w "$ROOT/scripts/coverage_threshold.ps1")" \
      "$(cygpath -w "$COBERTURA")"
  else
    echo "coverage.sh: Python or PowerShell is required to enforce thresholds" >&2
    exit 1
  fi
fi
