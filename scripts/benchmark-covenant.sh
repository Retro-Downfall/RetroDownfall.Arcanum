#!/usr/bin/env bash
#
# The Covenant release benchmark gate.
#
#   ./scripts/benchmark-covenant.sh                     measure and report
#   ./scripts/benchmark-covenant.sh --gate              measure and fail on an absolute ceiling
#   ./scripts/benchmark-covenant.sh --record <path>     measure and write a baseline
#   ./scripts/benchmark-covenant.sh --compare <path>    measure and compare against a baseline
#
# The host is published Native AOT and measured as published. The shipped CLI is Native AOT, and a
# number produced by a JIT-warmed host is a number the product never produces: on this workload the
# JIT run reported turn planning at roughly twice the published binary's cost, so a ceiling set from
# one would be meaningless against the other.
#
# A recorded baseline is only comparable on the host that recorded it, and the comparison is weaker
# than a co-run one would be. There is no co-run mode: this host measures one revision, and --compare
# deserializes a baseline some other process recorded at some other time. The bootstrap therefore
# pairs by batch ordinal across two separately recorded runs — batch i of each side shares an ordinal
# and nothing else — so it estimates within-run variance and aligns drift by position within a run.
# It does not cancel the drift between two recording sessions, and it certainly cannot cancel
# the difference between two machines. Recording a baseline and then comparing against it at the same
# commit on the same host, with no edits in between, has reported two regressions, one at a ratio of
# 1.353 on an operation whose p50 is a microsecond and a half. So read a comparative verdict as a
# reason to investigate rather than as a measurement, and never compare a developer machine's
# baseline against a shared runner: that reports the runner as a code regression. Record and compare
# on the same host, or record one revision and compare the other inside one CI job. The absolute
# ceilings are the cross-host gate, and they are the authoritative half for exactly these reasons.
#
# Exit codes come from the host and are the contract: 0 within every stated bound, 1 a breach, 2 the
# run could not be made. A breach and an unmeasurable run are different answers.
#
# Ceiling headroom, and what is not yet known about it. The latency ceilings are set at roughly four
# times what an Apple-silicon developer machine measures, on the reasoning that a shared CI runner is
# slower and a gate that failed on a busy one would be turned off. That factor is an estimate. No
# observed distribution from a shared runner has been recorded anywhere in this repository, so the
# margin between what the CI lane actually measures and what these ceilings allow is unverified.
#
# The CI lane now records its run and uploads it as covenant-benchmark-run.json with ninety days of
# retention, which is where that evidence comes from. Before any ceiling is tightened, read several of
# those artifacts, and state the observed p95 and p99 per operation and the date of the runs here, so
# the next person tightening a ceiling can see how much room they are taking away rather than guessing.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

PROJECT="$ROOT/tests/RetroDownfall.Arcanum.Covenant.Benchmarks/RetroDownfall.Arcanum.Covenant.Benchmarks.csproj"

RID="${ARCANUM_BENCHMARK_RID:-$(dotnet --info | awk -F': *' '/^ RID:/ {print $2}')}"

if [[ -z "$RID" ]]; then

    echo "Could not determine the runtime identifier; set ARCANUM_BENCHMARK_RID." >&2

    exit 2

fi

host_args=()

while [[ $# -gt 0 ]]; do

    case "$1" in

        --gate)

            host_args+=(--gate)

            shift
            ;;

        --record)

            [[ $# -ge 2 ]] || { echo "--record needs a path." >&2; exit 2; }

            host_args+=(--out "$2")

            shift 2
            ;;

        --compare)

            [[ $# -ge 2 ]] || { echo "--compare needs a path." >&2; exit 2; }

            host_args+=(--compare "$2")

            shift 2
            ;;

        *)

            echo "Unrecognized argument: $1" >&2

            exit 2
            ;;

    esac

done

# A fresh output directory every run. Native AOT publish reuses whatever it finds, so a stale tree
# lets a run measure a binary built from code that is no longer checked out — which presents as an
# unexplained regression or, worse, as an unexplained improvement.
OUTPUT="$(mktemp -d "${TMPDIR:-/tmp}/covenant-benchmark-XXXXXX")"

trap 'rm -rf "$OUTPUT"' EXIT

echo "publishing $RID" >&2

dotnet publish "$PROJECT" -c Release -r "$RID" -o "$OUTPUT" --nologo >"$OUTPUT/publish.log" 2>&1 || {

    echo "The benchmark host failed to publish:" >&2

    cat "$OUTPUT/publish.log" >&2

    exit 2

}

EXECUTABLE="$OUTPUT/RetroDownfall.Arcanum.Covenant.Benchmarks"

if [[ ! -x "$EXECUTABLE" ]]; then

    EXECUTABLE="$EXECUTABLE.exe"

fi

if [[ ! -x "$EXECUTABLE" ]]; then

    echo "The publish produced no benchmark executable in $OUTPUT." >&2

    exit 2

fi

"$EXECUTABLE" "${host_args[@]}"
