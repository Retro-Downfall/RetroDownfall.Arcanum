#!/bin/sh

set -eu

repo_root=$(CDPATH='' cd -- "$(dirname -- "$0")/.." && pwd -P)

for prerequisite in dotnet git uname
 do
    command -v "$prerequisite" >/dev/null 2>&1 || { printf '%s\n' "Missing benchmark prerequisite: $prerequisite" >&2; exit 2; }
done

case "$(uname -s):$(uname -m)" in
    Darwin:arm64) benchmark_rid=osx-arm64 ;;
    *) printf '%s\n' 'The benchmark script requires the shipping osx-arm64 Native AOT host.' >&2; exit 2 ;;
esac

[ "$#" -gt 0 ] || { printf '%s\n' 'Use --fixture, --smoke, or explicit --ollama-endpoint; see --help.' >&2; exit 2; }

project="$repo_root/tests/RetroDownfall.Arcanum.PromptCache.Benchmarks/RetroDownfall.Arcanum.PromptCache.Benchmarks.csproj"
publish_dir="$repo_root/tests/RetroDownfall.Arcanum.PromptCache.Benchmarks/artifacts/publish/$benchmark_rid"

dotnet publish "$project" -c Release -r "$benchmark_rid" --self-contained true -p:RestoreLockedMode=true -o "$publish_dir"

if [ "$#" -eq 1 ] && [ "$1" = '--help' ]; then
    exec "$publish_dir/RetroDownfall.Arcanum.PromptCache.Benchmarks" --help
fi

# The host owns bounded cancellation and an isolated ephemeral credential mirror for its ABA fixture.
# Explicit source identity is read here; no installation configuration or credential is loaded.
exec "$publish_dir/RetroDownfall.Arcanum.PromptCache.Benchmarks" "$@" --source-root "$repo_root"
