#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
OUTPUT_DIR="${REPO_ROOT}/artifacts/nuget"
FEED_URL="https://api.nuget.org/v3/index.json"

if [[ -z "${NUGET_API_KEY:-}" ]]; then
  echo "error: NUGET_API_KEY environment variable is not set" >&2
  exit 1
fi

rm -rf "${OUTPUT_DIR}"
mkdir -p "${OUTPUT_DIR}"

pushd "${REPO_ROOT}" >/dev/null
  echo "Packing Skynet projects into ${OUTPUT_DIR}"
  dotnet pack Skynet.sln --configuration Release --output "${OUTPUT_DIR}" --include-symbols --include-source

  echo "Publishing packages to ${FEED_URL}"
  for package in "${OUTPUT_DIR}"/*.nupkg; do
    [[ -f "${package}" ]] || continue
    dotnet nuget push "${package}" --api-key "${NUGET_API_KEY}" --source "${FEED_URL}" --skip-duplicate
  done
popd >/dev/null

echo "Packages published successfully."
