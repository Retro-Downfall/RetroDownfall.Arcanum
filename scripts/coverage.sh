#!/usr/bin/env bash
# Run runtime coverage and mandatory uninstrumented producer analysis, and (optionally)
# enforce the tiered coverage gates documented in docs/Arcanum.DESIGN.md section 13.
#
# Usage:
#   scripts/coverage.sh              # collect coverage + write HTML report
#   scripts/coverage.sh --threshold  # also enforce line/branch/security gates
#   scripts/coverage.sh --feature    # fast feedback only; not delivery qualification
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

FEATURE=0

for arg in "$@"; do
  case "$arg" in
    --threshold) THRESHOLD=1 ;;
    --feature) FEATURE=1 ;;
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
# Category=Perf is the manual wall-clock baseline harness (Tests/Performance): its
# assertions are machine-load sensitive and would fail the gate for reasons unrelated
# to any code change, especially under coverlet instrumentation on a loaded runner.
# Runtime tests must see redirected EOF even when this runner inherits a terminal.
dotnet test "$TEST_PROJECT" \
  --collect:"XPlat Code Coverage" \
  --settings "$RUNSETTINGS" \
  --filter "Category!=Perf&Category!=HostedProducerAnalysis" \
  --results-directory "$OUT_DIR" </dev/null

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

# This source analyzer lives in the excluded test assembly and analyzes Roslyn syntax;
# it does not execute the production graph it proves. Keep its full correctness gate,
# without coverlet. One serial lane owns the shared production graph while independent
# fixture methods run in bounded worker processes with live test/root progress logs.
if [ "$FEATURE" -eq 1 ]; then
  echo "Feature feedback only: not delivery qualification; producer analysis remains required."
else
  if command -v python3 >/dev/null 2>&1 && python3 -c 'import sys' >/dev/null 2>&1; then
    ANALYSIS_PYTHON=python3
  elif command -v python >/dev/null 2>&1 && python -c 'import sys' >/dev/null 2>&1; then
    ANALYSIS_PYTHON=python
  else
    echo "coverage.sh: Python is required for bounded producer analysis" >&2
    exit 1
  fi

  "$ANALYSIS_PYTHON" "$ROOT/scripts/hosted_producer_analysis_runner.py" \
    --dotnet dotnet \
    --project "$TEST_PROJECT" \
    --results-directory "$OUT_DIR/analysis"
fi
