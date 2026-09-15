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
  dotnet new console --framework net10.0
  cp "${REPO_ROOT}/nuget.config" ./nuget.config
  # The repo nuget.config already defines the "skynet-local" source with a repo-relative path;
  # retarget it to the freshly packed output instead of registering a duplicate source name.
  sed -i.bak "s|value=\"./artifacts/nuget\"|value=\"${OUTPUT_DIR}\"|" ./nuget.config && rm ./nuget.config.bak
  local_version="$(ls "${OUTPUT_DIR}" | grep -m1 'Skynet\.Core\..*\.nupkg$' | sed 's/Skynet\.Core\.\(.*\)\.nupkg/\1/')"
  project_file="$(basename "${SAMPLE_DIR}").csproj"
  echo "Referencing Skynet.Core ${local_version} from the local source"
  # Overwrite the template csproj wholesale so the local package reference is the only addition.
  cat > "${project_file}" <<REF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Skynet.Core" Version="${local_version}" />
  </ItemGroup>
</Project>
REF
  # Restore with the copied nuget.config so packageSourceMapping routes Skynet.* to the local
  # source and transitive dependencies to nuget.org.
  dotnet restore --configfile nuget.config
  cat <<'SRC' > Program.cs
using System;
using System.Threading.Tasks;
using Skynet.Core;

internal class Program
{
  private static async Task Main()
  {
    await using var system = new ActorSystem(
      options: new ActorSystemOptions(),
      transportFactory: sys => new InProcTransport(sys, new InProcTransportOptions()));

    Console.WriteLine($"Skynet ActorSystem ready: {system != null}");
  }
}
SRC
  dotnet run --configfile nuget.config
popd >/dev/null

echo "Package verification complete."
