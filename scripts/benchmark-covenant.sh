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
# Ceiling headroom, and where the latency ceilings come from. Each is ten times what a quiet shared CI
# runner measured for that percentile, rounded up to two significant figures, taken from the second
# table below. Ten is the multiple the gate's own intent asks for -- it catches an order-of-magnitude
# regression and nothing smaller -- and it is also what keeps a contended runner from tripping it: the
# four operations that drift with the host clear the worst figure yet recorded by a further factor of
# two or more. turn.admission is deliberately outside the rule and keeps the tighter ceilings it has,
# because it does not drift and its resolution is load-bearing; the workload manifest states why, and
# states what holding it costs.
#
# The CI lane records its run and uploads it as covenant-benchmark-run.json with ninety days of
# retention, which is where that evidence comes from. Before any ceiling is moved in either direction,
# read several of those artifacts, and state the observed p95 and p99 per operation and the date of
# the runs here, so the next person moving one can see how much room they are taking away or granting
# rather than guessing.
#
# First observations from a shared runner, both macos-26, 2026-08-26, microseconds:
#
#                     run A p95 / p99      run B p95 / p99      dev machine p95 / p99
#   turn.plan          550.2 / 1099.5       242.7 /  441.4        175.5 /  376.6
#   turn.admission       4.1 /  119.7         2.8 /    3.2          2.0 /    2.2
#   mutation.prepare   371.1 /  530.3       111.3 /  127.4         71.7 /  107.0
#   mutation.commit   2141.1 / 4729.1      1427.6 / 3248.1        863.0 / 1908.5
#   status.census       50.6 /   76.7        48.3 /   58.0         38.2 /   46.8
#
# Run A breached turn.admission p99 and mutation.prepare p95; run B passed everything with room, at
# the same commit-adjacent tree on the same runner label. The ceilings were left where they were at
# that point. What the two runs establish is the size of the noise: turn.admission p99 moved by a
# factor of thirty-seven between them, on an operation whose p50 is two and a half microseconds. At
# that scale one scheduler preemption is the whole measurement, so the p99 of the microsecond
# operations reports the runner rather than the code, and a red one is a reason to re-run before it is
# a reason to investigate. Allocations are the half that holds still -- identical to within sixteen
# bytes across all three columns -- and are the number to reach for when a regression needs catching.
#
# The pair the current latency ceilings are derived from, both macos-26 and both measuring identical
# code, 2026-08-29, microseconds. The third column is this machine, quiet, measured the same day:
#
#                     quiet 33262061371    busy 32943342814     this machine
#                       p95 /   p99          p95 /   p99         p95 /  p99
#   turn.plan          327.2 /  652.6       881.0 / 1631.7      152.9 /  318.8
#   turn.admission       3.5 /   10.0         3.7 /    5.1        1.7 /    2.1
#   mutation.prepare   142.0 /  241.2       478.2 /  761.7       65.7 /   91.5
#   mutation.commit   1627.8 / 3363.3      5280.2 / 7476.9      782.9 / 1835.4
#   status.census       61.2 /   77.7       214.7 /  381.0       32.4 /   35.9
#
# The busy run breached three of the five operations against the ceilings in force that day and the
# quiet one breached none, on the same code, which is the whole case for re-deriving them. The size of
# the host term is plain in the two CI columns: status.census p95 moved by three and a half and
# mutation.prepare p95 by three and a third, while allocations moved by at most two per cent. The
# third column is what the retired four-times factor was measured against, and shows the other half of
# why it was not enough -- a shared runner is about twice a developer machine before any contention
# begins, so headroom derived from a developer machine is already halved by the time the gate runs.

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
