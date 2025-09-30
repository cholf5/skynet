#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
OUTPUT_DIR="${REPO_ROOT}/artifacts/nuget"
SAMPLE_DIR="$(mktemp -d)"

cleanup() {
  rm -rf "${SAMPLE_DIR}"
}
trap cleanup EXIT

rm -rf "${OUTPUT_DIR}"
mkdir -p "${OUTPUT_DIR}"

pushd "${REPO_ROOT}" >/dev/null
  echo "Packing Skynet projects into ${OUTPUT_DIR}"
  dotnet pack Skynet.sln --configuration Release --output "${OUTPUT_DIR}" --include-symbols --include-source
popd >/dev/null

pushd "${SAMPLE_DIR}" >/dev/null
  echo "Creating verification console app under ${SAMPLE_DIR}"
  dotnet new console --framework net9.0
  cp "${REPO_ROOT}/nuget.config" ./nuget.config
  dotnet nuget add source "${OUTPUT_DIR}" --name skynet-local --configfile nuget.config 2>/dev/null || true
  dotnet restore --configfile nuget.config
  dotnet add package Skynet.Core --source "${OUTPUT_DIR}" --configfile nuget.config
  cat <<'SRC' > Program.cs
using System;
using System.Threading.Tasks;
using Skynet.Core;

internal class Program
{
  private static async Task Main()
  {
    await using var system = new ActorSystem(new ActorSystemOptions
    {
      TransportFactory = () => new InProcTransport(new InProcTransportOptions())
    });

    Console.WriteLine($"Skynet ActorSystem ready: {system != null}");
  }
}
SRC
  dotnet run --configfile nuget.config
popd >/dev/null

echo "Package verification complete."
