#!/usr/bin/env bash
# Builds the Skynet examples Docker image used by docker-compose.yml.
# Usage: scripts/docker-build.sh [--tag <name:tag>] [--push]
#   --tag   image repository:tag to apply (default: skynet/examples:local)
#   --push  placeholder only: pushing to a registry is not implemented yet, see
#           docs/release-guide.md "Docker Compose 部署" for the manual push steps.
set -euo pipefail

cd "$(dirname "$0")/.."

image="skynet/examples:local"
push=false

while [[ $# -gt 0 ]]; do
	case "$1" in
		--tag)
			[[ $# -ge 2 ]] || { echo "error: --tag requires a value" >&2; exit 1; }
			image="$2"
			shift 2
			;;
		--push)
			push=true
			shift
			;;
		*)
			echo "error: unknown option '$1'" >&2
			echo "usage: scripts/docker-build.sh [--tag <name:tag>] [--push]" >&2
			exit 1
			;;
	esac
done

if ! command -v docker >/dev/null 2>&1; then
	echo "error: docker CLI not found; install Docker Engine first" >&2
	exit 1
fi

echo "Building ${image} (context: $(pwd))..."
docker build -t "${image}" .

echo "Built ${image}:"
docker image inspect "${image}" --format '  id={{.Id}} size={{.Size}}'

if [[ "${push}" == true ]]; then
	# TODO(E-10 follow-up): wire registry credentials and a multi-arch buildx matrix once
	# a target registry is chosen for this repository.
	echo "note: --push is a placeholder; nothing was pushed. Prefix the tag with your registry and run 'docker push ${image}' manually." >&2
fi

echo "Done."
