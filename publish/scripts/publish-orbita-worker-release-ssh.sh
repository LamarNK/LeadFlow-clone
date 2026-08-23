#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 --msi <path> --version <major.minor.build.revision>" >&2
  exit 64
}

msi_path=""
version=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --msi)
      msi_path="${2:-}"
      shift 2
      ;;
    --version)
      version="${2:-}"
      shift 2
      ;;
    *)
      usage
      ;;
  esac
done

[[ -f "$msi_path" ]] || { echo "MSI file was not found: $msi_path" >&2; exit 1; }
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || usage

: "${ORBITA_SSH_KEY:?ORBITA_SSH_KEY is required}"
: "${ORBITA_SSH_KNOWN_HOSTS:?ORBITA_SSH_KNOWN_HOSTS is required}"
: "${ORBITA_HOST:?ORBITA_HOST is required}"
: "${ORBITA_PORT:?ORBITA_PORT is required}"
: "${ORBITA_USER:?ORBITA_USER is required}"

expected_sha="$(sha256sum "$msi_path" | awk '{print $1}')"
build_number="${BUILD_NUMBER:-manual}"
remote_path="/tmp/orbita-worker-${version//./-}-${RANDOM}.msi"
ssh_options=(
  -o BatchMode=yes
  -o StrictHostKeyChecking=yes
  -o UserKnownHostsFile="$ORBITA_SSH_KNOWN_HOSTS"
  -i "$ORBITA_SSH_KEY"
  -p "$ORBITA_PORT"
)
scp_options=(
  -o BatchMode=yes
  -o StrictHostKeyChecking=yes
  -o UserKnownHostsFile="$ORBITA_SSH_KNOWN_HOSTS"
  -i "$ORBITA_SSH_KEY"
  -P "$ORBITA_PORT"
)

cleanup_remote() {
  ssh "${ssh_options[@]}" "$ORBITA_USER@$ORBITA_HOST" "rm -f -- '$remote_path'" >/dev/null 2>&1 || true
}
trap cleanup_remote EXIT

scp "${scp_options[@]}" "$msi_path" "$ORBITA_USER@$ORBITA_HOST:$remote_path"
ssh "${ssh_options[@]}" "$ORBITA_USER@$ORBITA_HOST" \
  "VERSION='$version' EXPECTED_SHA='$expected_sha' BUILD_NUMBER='$build_number' REMOTE_PATH='$remote_path' bash -s" <<'REMOTE'
set -euo pipefail

container="$(docker ps --filter 'name=^/orbita-api-1$' --format '{{.Names}}')"
test "$container" = "orbita-api-1"

docker exec -e VERSION -e EXPECTED_SHA -e BUILD_NUMBER -e REMOTE_PATH "$container" sh -ec '
  release_dir="/app/Data/releases/worker/v$VERSION"
  package_path="$release_dir/update.msi"
  mkdir -p "$release_dir"
  rm -f "$package_path"
'
docker cp "$REMOTE_PATH" "$container:/app/Data/releases/worker/v$VERSION/update.msi"

docker exec -e VERSION -e EXPECTED_SHA -e BUILD_NUMBER "$container" sh -ec '
  release_dir="/app/Data/releases/worker/v$VERSION"
  package_path="$release_dir/update.msi"
  actual_sha="$(sha256sum "$package_path" | awk "{print \$1}")"
  test "$actual_sha" = "$EXPECTED_SHA"
  size="$(stat -c %s "$package_path")"
  uploaded="$(date -u +%Y-%m-%dT%H:%M:%S.%NZ)"
  printf "{\n  \"version\": \"%s\",\n  \"releaseNotes\": \"Jenkins build #%s\",\n  \"fileSize\": %s,\n  \"sha256\": \"%s\",\n  \"uploadedAtUtc\": \"%s\"\n}\n" "$VERSION" "$BUILD_NUMBER" "$size" "$actual_sha" "$uploaded" > "$release_dir/version.json"
  cp "$release_dir/version.json" /app/Data/releases/worker/latest.json
'
REMOTE

echo "Published Orbita Worker $version"
