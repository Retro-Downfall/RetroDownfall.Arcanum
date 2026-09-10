#!/bin/sh

set -u

invalid()
{
    printf '%s\n' "$1" >&2
    exit 2
}

repo_root=$(CDPATH='' cd -- "$(dirname -- "$0")/.." && pwd -P) || exit 2
temp_root=''
temp_parent=''
temp_root_identity=''
child_pid=''
watchdog_pid=''

# Invoked indirectly by the EXIT trap.
# shellcheck disable=SC2317
cleanup()
{
    [ -n "$temp_root" ] && [ -n "$temp_parent" ] && [ -n "$temp_root_identity" ] || return 0
    [ ! -L "$temp_root" ] || return 0

    # Cleanup authority is the original physical directory, never a mutable TMPDIR alias.
    cleanup__canonical_temp=$(CDPATH='' cd -- "$temp_root" 2>/dev/null && pwd -P) || return 0
    [ "$cleanup__canonical_temp" = "$temp_root" ] || return 0
    [ "${temp_root%/*}" = "$temp_parent" ] || return 0

    case "${temp_root##*/}" in
        arcanum-grimoire-admission-script.??????)
            cleanup__identity=$(/usr/bin/stat -f '%d:%i' "$temp_root" 2>/dev/null) || return 0
            [ "$cleanup__identity" = "$temp_root_identity" ] || return 0
            rm -rf -- "$temp_root"
            ;;
    esac
}

# Invoked indirectly by the INT and TERM traps.
# shellcheck disable=SC2317
cancel()
{
    trap - INT TERM
    if [ -n "$child_pid" ]; then
        kill -TERM "$child_pid" 2>/dev/null || true
        sleep 1
        kill -KILL "$child_pid" 2>/dev/null || true
        wait "$child_pid" 2>/dev/null || true
    fi
    [ -z "$watchdog_pid" ] || kill "$watchdog_pid" 2>/dev/null || true
    cleanup
    exit 130
}

trap cleanup EXIT
trap cancel INT TERM

create_workspace()
{
    # NuGet must see the same physical root for the top-level project and its references.
    temp_parent=$(CDPATH='' cd -- "${TMPDIR:-/tmp}" && pwd -P) || exit 2
    create_workspace__created=$(mktemp -d "$temp_parent/arcanum-grimoire-admission-script.XXXXXX") || exit 2
    create_workspace__identity=$(/usr/bin/stat -f '%d:%i' "$create_workspace__created" 2>/dev/null) || exit 2
    [ ! -L "$create_workspace__created" ] || exit 2
    create_workspace__canonical=$(CDPATH='' cd -- "$create_workspace__created" && pwd -P) || exit 2
    [ "$create_workspace__canonical" = "$create_workspace__created" ] || exit 2
    [ "${create_workspace__canonical%/*}" = "$temp_parent" ] || exit 2
    case "${create_workspace__canonical##*/}" in
        arcanum-grimoire-admission-script.??????) ;;
        *) exit 2 ;;
    esac
    temp_root=$create_workspace__canonical
    temp_root_identity=$create_workspace__identity
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
    publish_host__source_root=$1
    publish_host__output=$2
    publish_host__project="$publish_host__source_root/tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj"
    publish_host__rid=$(rid) || return 2

    dotnet publish "$publish_host__project" \
        -c Release \
        -r "$publish_host__rid" \
        --self-contained true \
        -p:RestoreLockedMode=true \
        -o "$publish_host__output"
}

run_host()
{
    run_host__deadline_seconds=$1
    shift
    run_host__executable=$1
    shift
    run_host__deadline_flag="$temp_root/parent-deadline.$$.flag"
    env -u ARCANUM_TEST_HOME "$run_host__executable" "$@" &
    child_pid=$!
    (
        sleep "$run_host__deadline_seconds"
        if kill -0 "$child_pid" 2>/dev/null; then
            : > "$run_host__deadline_flag"
            kill -TERM "$child_pid" 2>/dev/null || true
            sleep 5
            kill -KILL "$child_pid" 2>/dev/null || true
        fi
    ) &
    watchdog_pid=$!
    wait "$child_pid"
    run_host__child_status=$?
    child_pid=''
    kill "$watchdog_pid" 2>/dev/null || true
    wait "$watchdog_pid" 2>/dev/null || true
    watchdog_pid=''
    [ ! -f "$run_host__deadline_flag" ] || return 2
    return "$run_host__child_status"
}

machine_inputs()
{
    machine_inputs__sdk=$(dotnet --version) || return 2
    machine_inputs__cpu=$(sysctl -n machdep.cpu.brand_string 2>/dev/null) || machine_inputs__cpu=$(uname -m)
    [ -n "$machine_inputs__sdk" ] || return 2
    [ -n "$machine_inputs__cpu" ] || return 2
    dotnet --info > "$temp_root/dotnet-info.txt" || return 2
    shasum -a 256 "$temp_root/dotnet-info.txt" > "$temp_root/dotnet-info.sha256" || return 2
    read -r machine_inputs__toolchain _ < "$temp_root/dotnet-info.sha256" || return 2
    require_digest "$machine_inputs__toolchain" || return 2
}

require_digest()
{
    require_digest__digest=$1
    case "$require_digest__digest" in
        *[!0-9a-f]*|'') return 2 ;;
    esac
    [ ${#require_digest__digest} -eq 64 ]
}

measure()
{
    measure__executable=$1
    measure__source_root=$2
    measure__revision=$3
    measure__session=$4
    measure__pair=$5
    measure__order=$6
    measure__role=$7
    measure__output=$8

    run_host 930 "$measure__executable" \
        --measure \
        --profile qualification \
        --revision "$measure__revision" \
        --session "$measure__session" \
        --pair "$measure__pair" \
        --order "$measure__order" \
        --role "$measure__role" \
        --source-root "$measure__source_root" \
        --sdk "$machine_inputs__sdk" \
        --cpu "$machine_inputs__cpu" \
        --toolchain-digest "$machine_inputs__toolchain" \
        --out "$measure__output"
}

require_exact_commit()
{
    require_exact_commit__revision=$1
    case "$require_exact_commit__revision" in
        *[!0-9a-f]*|'') return 2 ;;
    esac
    [ ${#require_exact_commit__revision} -eq 40 ] || return 2
    [ "$(git -C "$repo_root" rev-parse "$require_exact_commit__revision^{commit}" 2>/dev/null)" = "$require_exact_commit__revision" ]
}

derive_revision_catalog()
{
    derive_revision_catalog__revision=$1
    derive_revision_catalog__output=$2
    derive_revision_catalog__tree="$temp_root/tree-$derive_revision_catalog__revision.txt"
    derive_revision_catalog__selected="$temp_root/selected-$derive_revision_catalog__revision.txt"
    git -C "$repo_root" ls-tree -r --name-only "$derive_revision_catalog__revision" > "$derive_revision_catalog__tree" || return 2
    awk '
        /\.cs$/ && ($0 ~ /^tests\/RetroDownfall\.Arcanum\.GrimoireAdmission\.Benchmarks\// || $0 ~ /^src\/RetroDownfall\.Arcanum\.(Core|Infrastructure|Secrets)\//) { print; next }
        /\.sql$/ && $0 ~ /^src\/RetroDownfall\.Arcanum\.Infrastructure\/Data\/Schema\// { print }
    ' "$derive_revision_catalog__tree" > "$derive_revision_catalog__selected" || return 2
    for derive_revision_catalog__path in \
        Directory.Build.props \
        Directory.Build.targets \
        scripts/benchmark-grimoire-admission.sh \
        tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt \
        tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-workload-v1.json \
        tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/packages.lock.json \
        tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks.csproj \
        src/RetroDownfall.Arcanum.Core/RetroDownfall.Arcanum.Core.csproj \
        src/RetroDownfall.Arcanum.Infrastructure/RetroDownfall.Arcanum.Infrastructure.csproj \
        src/RetroDownfall.Arcanum.Secrets/RetroDownfall.Arcanum.Secrets.csproj \
        src/RetroDownfall.Arcanum.NativeSqlCipher/RetroDownfall.Arcanum.NativeSqlCipher.csproj \
        src/RetroDownfall.Arcanum.NativeSqlCipher/build/RetroDownfall.Arcanum.NativeSqlCipher.targets \
        src/RetroDownfall.Arcanum.NativeSqlCipher/buildTransitive/RetroDownfall.Arcanum.NativeSqlCipher.targets \
        src/RetroDownfall.Arcanum.NativeSqlCipher/native-source-manifest.json \
        src/RetroDownfall.Arcanum.NativeSqlCipher/runtimes/osx-arm64/native/libe_sqlcipher.dylib \
        src/RetroDownfall.Arcanum.NativeSqlCipher/runtimes/win-arm64/native/e_sqlcipher.dll \
        src/RetroDownfall.Arcanum.NativeSqlCipher/runtimes/win-x64/native/e_sqlcipher.dll \
        src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionEpoch.cs
    do
        printf '%s\n' "$derive_revision_catalog__path" >> "$derive_revision_catalog__selected" || return 2
    done
    LC_ALL=C sort -u "$derive_revision_catalog__selected" > "$derive_revision_catalog__output" || return 2
}

require_revision_catalog()
{
    require_revision_catalog__revision=$1
    require_revision_catalog__catalog="$temp_root/catalog-$require_revision_catalog__revision.txt"
    require_revision_catalog__paths="$temp_root/catalog-paths-$require_revision_catalog__revision.txt"
    require_revision_catalog__derived="$temp_root/catalog-derived-$require_revision_catalog__revision.txt"
    git -C "$repo_root" show "$require_revision_catalog__revision:tests/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks/grimoire-admission-input-catalog-v1.txt" > "$require_revision_catalog__catalog" || return 2
    awk -F '\t' 'NF == 2 && ($1 == "R" || $1 == "O") { print $2; next } { exit 2 }' "$require_revision_catalog__catalog" > "$require_revision_catalog__paths" || return 2
    derive_revision_catalog "$require_revision_catalog__revision" "$require_revision_catalog__derived" || return 2
    cmp -s "$require_revision_catalog__paths" "$require_revision_catalog__derived"
}

require_instrument_bytes()
{
    require_instrument_bytes__harness=$1
    require_instrument_bytes__baseline=$2
    require_instrument_bytes__candidate=$3
    require_instrument_bytes__catalog="$temp_root/catalog-$require_instrument_bytes__harness.txt"
    while IFS="$(printf '\t')" read -r _ require_instrument_bytes__path
    do
        case "$require_instrument_bytes__path" in
            src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionGate.cs|src/RetroDownfall.Arcanum.Infrastructure/Data/GrimoireConnectionAdmissionEpoch.cs)
                continue
                ;;
        esac
        require_instrument_bytes__harness_file="$temp_root/instrument-harness"
        require_instrument_bytes__baseline_file="$temp_root/instrument-baseline"
        require_instrument_bytes__candidate_file="$temp_root/instrument-candidate"
        git -C "$repo_root" show "$require_instrument_bytes__harness:$require_instrument_bytes__path" > "$require_instrument_bytes__harness_file" || return 2
        git -C "$repo_root" show "$require_instrument_bytes__baseline:$require_instrument_bytes__path" > "$require_instrument_bytes__baseline_file" || return 2
        git -C "$repo_root" show "$require_instrument_bytes__candidate:$require_instrument_bytes__path" > "$require_instrument_bytes__candidate_file" || return 2
        cmp -s "$require_instrument_bytes__harness_file" "$require_instrument_bytes__baseline_file" || return 2
        cmp -s "$require_instrument_bytes__harness_file" "$require_instrument_bytes__candidate_file" || return 2
        cmp -s "$require_instrument_bytes__harness_file" "$repo_root/$require_instrument_bytes__path" || return 2
    done < "$require_instrument_bytes__catalog"
}

[ $# -ge 1 ] || invalid 'Expected --smoke, --calibrate, or --qualify.'

case "$1" in
    --smoke)
        [ $# -eq 1 ] || invalid '--smoke accepts no additional arguments.'
        create_workspace
        publish_dir="$temp_root/publish"
        publish_host "$repo_root" "$publish_dir" || exit 2
        run_host 150 "$publish_dir/RetroDownfall.Arcanum.GrimoireAdmission.Benchmarks" \
            --smoke \
            --source-root "$repo_root"
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
        [ $# -eq 9 ] || invalid 'Usage: --qualify --harness H --base B --candidate C --out DIRECTORY.'
        [ "$2" = '--harness' ] || invalid 'Qualification requires --harness.'
        [ "$4" = '--base' ] || invalid 'Qualification requires --base.'
        [ "$6" = '--candidate' ] || invalid 'Qualification requires --candidate.'
        [ "$8" = '--out' ] || invalid 'Qualification requires --out.'
        harness=$3
        base=$5
        candidate=$7
        output_dir=$9
        [ -z "$(git -C "$repo_root" status --porcelain --untracked-files=all)" ] || invalid 'Qualification requires a clean caller tree.'
        require_exact_commit "$harness" || invalid 'Harness is not an exact commit.'
        require_exact_commit "$base" || invalid 'Baseline is not an exact commit.'
        require_exact_commit "$candidate" || invalid 'Candidate is not an exact commit.'
        git -C "$repo_root" merge-base --is-ancestor "$harness" "$base" || invalid 'Harness must be an ancestor of baseline.'
        git -C "$repo_root" merge-base --is-ancestor "$base" "$candidate" || invalid 'Baseline must be an ancestor of candidate.'
        create_workspace
        require_revision_catalog "$harness" || invalid 'Harness catalog does not match its independently derived tracked input set.'
        require_revision_catalog "$base" || invalid 'Baseline catalog does not match its independently derived tracked input set.'
        require_revision_catalog "$candidate" || invalid 'Candidate catalog does not match its independently derived tracked input set.'
        require_instrument_bytes "$harness" "$base" "$candidate" || invalid 'The immutable benchmark instrument differs across H, caller, B, or C.'
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
        session=$(uuidgen) || exit 2
        pair=0
        while [ "$pair" -lt 6 ]
        do
            if [ $((pair % 2)) -eq 0 ]; then
                measure "$base_host" "$base_tree" "$base" "$session" "$pair" 0 B "$output_dir/runs/pair-$pair-B.json" || { status=$?; [ "$status" -ne 130 ] || exit 130; exit 2; }
                measure "$candidate_host" "$candidate_tree" "$candidate" "$session" "$pair" 1 C "$output_dir/runs/pair-$pair-C.json" || { status=$?; [ "$status" -ne 130 ] || exit 130; exit 2; }
            else
                measure "$candidate_host" "$candidate_tree" "$candidate" "$session" "$pair" 0 C "$output_dir/runs/pair-$pair-C.json" || { status=$?; [ "$status" -ne 130 ] || exit 130; exit 2; }
                measure "$base_host" "$base_tree" "$base" "$session" "$pair" 1 B "$output_dir/runs/pair-$pair-B.json" || { status=$?; [ "$status" -ne 130 ] || exit 130; exit 2; }
            fi
            pair=$((pair + 1))
        done
        run_host 150 "$base_host" \
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
