#!/usr/bin/env bash
# Runs the Skynet baseline benchmark scenarios (docs/benchmarks.md) and archives results.
# Usage: scripts/run-benchmarks.sh [duration_seconds]
set -euo pipefail

cd "$(dirname "$0")/.."
duration="${1:-5}"
stamp="$(date +%Y%m%d-%H%M%S)"
out_dir="benchmarks/results"
mkdir -p "$out_dir"

echo "Building benchmarks (Release)..."
dotnet build benchmarks/Skynet.Benchmarks -c Release --nologo -v q

for scenario in local-tell local-call remote-call; do
	echo "=== $scenario (${duration}s) ==="
	dotnet run --project benchmarks/Skynet.Benchmarks -c Release --no-build -- \
		--scenario "$scenario" --duration "$duration" \
		--json "$out_dir/$stamp-$scenario.json" \
		| tee "$out_dir/$stamp-$scenario.txt"
done

echo "Results archived under $out_dir/$stamp-*.txt/.json"
