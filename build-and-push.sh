#!/usr/bin/env bash
set -euo pipefail

IMAGE_NAME="holosheep/docker-swarm-webhook"
TAG="latest"
DOCKERFILE="DockerSwarmWebhook/Dockerfile"
CONTEXT="."
NO_LATEST=0
SKIP_LOGIN=0
DRY_RUN=0

usage() {
  cat <<EOF
Usage: ./build-and-push.sh [options]

Options:
  --tag <tag>           Tag to push (default: latest)
  --image-name <name>   Image name (default: holosheep/docker-swarm-webhook)
  --dockerfile <path>   Dockerfile path (default: DockerSwarmWebhook/Dockerfile)
  --context <path>      Docker build context (default: .)
  --no-latest           Do not also push :latest when tag is custom
  --skip-login          Skip docker login check
  --dry-run             Print commands without executing them
  -h, --help            Show this help
EOF
}

step() {
  local message="$1"
  shift

  echo "==> ${message}"
  if [[ "$DRY_RUN" == "1" ]]; then
	printf 'DRY-RUN:'
	printf ' %q' "$@"
	printf '\n'
	return 0
  fi

  "$@"
}

test_docker_login() {
  local config_path="${DOCKER_CONFIG:-$HOME/.docker}/config.json"
  [[ -f "$config_path" ]] || return 1
  grep -q '"auths"' "$config_path"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
	--tag)
	  TAG="$2"
	  shift 2
	  ;;
	--image-name)
	  IMAGE_NAME="$2"
	  shift 2
	  ;;
	--dockerfile)
	  DOCKERFILE="$2"
	  shift 2
	  ;;
	--context)
	  CONTEXT="$2"
	  shift 2
	  ;;
	--no-latest)
	  NO_LATEST=1
	  shift
	  ;;
	--skip-login)
	  SKIP_LOGIN=1
	  shift
	  ;;
	--dry-run)
	  DRY_RUN=1
	  shift
	  ;;
	-h|--help)
	  usage
	  exit 0
	  ;;
	*)
	  echo "Unknown option: $1" >&2
	  usage >&2
	  exit 1
	  ;;
  esac
done

command -v docker >/dev/null 2>&1 || { echo "Docker CLI is not installed or not available in PATH." >&2; exit 1; }
[[ -f "$DOCKERFILE" ]] || { echo "Dockerfile not found: $DOCKERFILE" >&2; exit 1; }

tags=("${IMAGE_NAME}:${TAG}")
if [[ "$NO_LATEST" != "1" && "$TAG" != "latest" ]]; then
  tags+=("${IMAGE_NAME}:latest")
fi

if [[ "$SKIP_LOGIN" != "1" ]] && ! test_docker_login; then
  step "Docker Hub login" docker login
fi

step "Building image ${tags[0]}" docker build -f "$DOCKERFILE" -t "${tags[0]}" "$CONTEXT"

for ((i=1; i<${#tags[@]}; i++)); do
  step "Tagging image as ${tags[$i]}" docker tag "${tags[0]}" "${tags[$i]}"
done

for image_tag in "${tags[@]}"; do
  step "Pushing ${image_tag}" docker push "$image_tag"
done

echo "Done. Pushed: ${tags[*]}"
