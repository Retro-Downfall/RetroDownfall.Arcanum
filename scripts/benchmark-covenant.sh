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
# Exit codes come from the host and are the contract: 0 within every stated bound, 1 a breach, 2 the
# run could not be made. A breach and an unmeasurable run are different answers.

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
