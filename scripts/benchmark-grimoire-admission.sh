#!/bin/sh

set -u

invalid()
{
    printf '%s\n' "$1" >&2
    exit 2
}

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd -P) || exit 2
project="$repo_root/tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj"
temp_root=''

cleanup()
{
    [ -n "$temp_root" ] || return 0

    canonical_temp=$(CDPATH= cd -- "$temp_root" 2>/dev/null && pwd -P) || return 0
    system_temp=${TMPDIR:-/tmp}
    canonical_system_temp=$(CDPATH= cd -- "$system_temp" 2>/dev/null && pwd -P) || return 0

    case "$canonical_temp" in
        "$canonical_system_temp"/arcanum-grimoire-admission-script.*)
            [ ! -L "$temp_root" ] || return 0
            rm -rf -- "$canonical_temp"
            ;;
    esac
}

cancel()
{
    trap - INT TERM
    cleanup
    exit 130
}

trap cleanup EXIT
trap cancel INT TERM

create_workspace()
{
    temp_root=$(mktemp -d "${TMPDIR:-/tmp}/arcanum-grimoire-admission-script.XXXXXX") || exit 2
}

rid()
{
    case "$(uname -m)" in
        arm64) printf '%s\n' 'osx-arm64' ;;
        x86_64) printf '%s\n' 'osx-x64' ;;
        *) return 2 ;;
    esac
}

publish_host()
{
    publish_source_root=$1
    publish_output=$2
    publish_project="$publish_source_root/tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj"
    publish_rid=$(rid) || return 2

    dotnet publish "$publish_project" \
        -c Release \
        -r "$publish_rid" \
        --self-contained true \
        -p:RestoreLockedMode=true \
        -o "$publish_output"
}

run_host()
{
    executable=$1
    shift
    env -u ARCANUM_TEST_HOME "$executable" "$@"
}

machine_inputs()
{
    benchmark_sdk=$(dotnet --version) || return 2
    benchmark_cpu=$(sysctl -n machdep.cpu.brand_string 2>/dev/null) || benchmark_cpu=$(uname -m)
    benchmark_toolchain=$(dotnet --info | shasum -a 256 | awk '{ print $1 }') || return 2
}

measure()
{
    executable=$1
    source_root=$2
    revision=$3
    session=$4
    pair=$5
    order=$6
    role=$7
    output=$8

    run_host "$executable" \
        --measure \
        --profile qualification \
        --revision "$revision" \
        --session "$session" \
        --pair "$pair" \
        --order "$order" \
        --role "$role" \
        --source-root "$source_root" \
        --sdk "$benchmark_sdk" \
        --cpu "$benchmark_cpu" \
        --toolchain-digest "$benchmark_toolchain" \
        --out "$output"
}

require_exact_commit()
{
    revision=$1
    case "$revision" in
        *[!0-9a-f]*|'') return 2 ;;
    esac
    [ ${#revision} -eq 40 ] || return 2
    [ "$(git -C "$repo_root" rev-parse "$revision^{commit}" 2>/dev/null)" = "$revision" ]
}

require_instrument_bytes()
{
    left=$1
    right=$2
    for path in \
        scripts/benchmark-grimoire-admission.sh \
        tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkComparison.cs \
        tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkEvidence.cs \
        tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/AdmissionBenchmarkManifest.cs \
        tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json
    do
        left_file="$temp_root/left-$(basename "$path")"
        right_file="$temp_root/right-$(basename "$path")"
        git -C "$repo_root" show "$left:$path" > "$left_file" || return 2
        git -C "$repo_root" show "$right:$path" > "$right_file" || return 2
        cmp -s "$left_file" "$right_file" || return 2
        cmp -s "$left_file" "$repo_root/$path" || return 2
    done
}

[ $# -ge 1 ] || invalid 'Expected --smoke, --calibrate, or --qualify.'

case "$1" in
    --smoke)
        [ $# -eq 1 ] || invalid '--smoke accepts no additional arguments.'
        create_workspace
        publish_dir="$temp_root/publish"
        publish_host "$repo_root" "$publish_dir" || exit 2
        run_host "$publish_dir/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks" --smoke
        exit $?
        ;;
    --calibrate)
        [ $# -eq 5 ] || invalid 'Usage: --calibrate --revision H --out FILE.'
        [ "$2" = '--revision' ] || invalid 'Calibration requires --revision.'
        [ "$4" = '--out' ] || invalid 'Calibration requires --out.'
        revision=$3
        output=$5
        case "$revision" in
            *[!0-9a-f]*|'') invalid 'Calibration revision must be lowercase hex.' ;;
        esac
        [ ${#revision} -eq 40 ] || invalid 'Calibration revision must be an exact commit.'
        [ "$(git -C "$repo_root" rev-parse HEAD 2>/dev/null)" = "$revision" ] || invalid 'Calibration revision must equal HEAD.'
        [ -z "$(git -C "$repo_root" status --porcelain --untracked-files=all)" ] || invalid 'Calibration requires a clean tree.'
        create_workspace
        machine_inputs || exit 2
        publish_dir="$temp_root/publish"
        publish_host "$repo_root" "$publish_dir" || exit 2
        measure \
            "$publish_dir/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks" \
            "$repo_root" "$revision" "calibration-$revision" 0 0 H "$output"
        exit $?
        ;;
    --qualify)
        [ $# -eq 7 ] || invalid 'Usage: --qualify --base B --candidate C --out DIRECTORY.'
        [ "$2" = '--base' ] || invalid 'Qualification requires --base.'
        [ "$4" = '--candidate' ] || invalid 'Qualification requires --candidate.'
        [ "$6" = '--out' ] || invalid 'Qualification requires --out.'
        base=$3
        candidate=$5
        output_dir=$7
        [ -z "$(git -C "$repo_root" status --porcelain --untracked-files=all)" ] || invalid 'Qualification requires a clean caller tree.'
        require_exact_commit "$base" || invalid 'Baseline is not an exact commit.'
        require_exact_commit "$candidate" || invalid 'Candidate is not an exact commit.'
        git -C "$repo_root" merge-base --is-ancestor "$base" "$candidate" || invalid 'Baseline must be an ancestor of candidate.'
        create_workspace
        require_instrument_bytes "$base" "$candidate" || invalid 'The immutable benchmark instrument differs across caller, B, or C.'
        machine_inputs || exit 2
        base_tree="$temp_root/base"
        candidate_tree="$temp_root/candidate"
        [ ! -e "$output_dir" ] || invalid 'Qualification output directory already exists.'
        mkdir -p "$base_tree" "$candidate_tree" "$output_dir/runs" || exit 2
        git -C "$repo_root" archive "$base" > "$temp_root/base.tar" || exit 2
        git -C "$repo_root" archive "$candidate" > "$temp_root/candidate.tar" || exit 2
        tar -xf "$temp_root/base.tar" -C "$base_tree" || exit 2
        tar -xf "$temp_root/candidate.tar" -C "$candidate_tree" || exit 2
        publish_host "$base_tree" "$temp_root/publish-base" || exit 2
        publish_host "$candidate_tree" "$temp_root/publish-candidate" || exit 2
        base_host="$temp_root/publish-base/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks"
        candidate_host="$temp_root/publish-candidate/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks"
        session=$(uuidgen | tr '[:upper:]' '[:lower:]') || exit 2
        pair=0
        while [ "$pair" -lt 6 ]
        do
            if [ $((pair % 2)) -eq 0 ]; then
                measure "$base_host" "$base_tree" "$base" "$session" "$pair" 0 B "$output_dir/runs/pair-$pair-B.json" || exit 2
                measure "$candidate_host" "$candidate_tree" "$candidate" "$session" "$pair" 1 C "$output_dir/runs/pair-$pair-C.json" || exit 2
            else
                measure "$candidate_host" "$candidate_tree" "$candidate" "$session" "$pair" 0 C "$output_dir/runs/pair-$pair-C.json" || exit 2
                measure "$base_host" "$base_tree" "$base" "$session" "$pair" 1 B "$output_dir/runs/pair-$pair-B.json" || exit 2
            fi
            pair=$((pair + 1))
        done
        run_host "$base_host" \
            --compare \
            --session "$session" \
            --runs-dir "$output_dir/runs" \
            --bundle-out "$output_dir/evidence.json" \
            --out "$output_dir/comparison.json"
        exit $?
        ;;
    *)
        invalid 'Unknown Grimoire-admission benchmark mode.'
        ;;
esac
