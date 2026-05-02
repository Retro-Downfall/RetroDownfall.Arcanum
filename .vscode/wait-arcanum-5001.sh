#!/usr/bin/env bash
set -euo pipefail

# Waits until something answers HTTP on loopback :5001 (any status, including 404).
# Used by VS Code before launching "Cli: ask gets local time" so the CLI does not
# immediately hit a refused connection / noisy first-chance HttpRequestException.

deadline=$((SECONDS + 45))

while (( SECONDS < deadline )); do

  code="$(curl -s -o /dev/null -w '%{http_code}' --connect-timeout 1 'http://127.0.0.1:5001/' 2>/dev/null || echo 000)"

  if [[ "${code}" != "000" ]]; then

    exit 0

  fi

  sleep 1

done

echo 'Arcanum: timed out waiting for http://127.0.0.1:5001/. Start "Cli: serve (API on :5001)" or "Api.DevHost: slim API" first, then retry.' >&2

exit 1
