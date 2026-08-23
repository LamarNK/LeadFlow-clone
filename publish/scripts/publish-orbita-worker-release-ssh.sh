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
script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd -- "$script_dir/../.." && pwd)"
source_commit=""
source_subject=""
if git -C "$repo_root" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
  source_commit="$(git -C "$repo_root" rev-parse --short=7 HEAD)"
  source_subject="$(git -C "$repo_root" log -1 --format=%s HEAD)"
fi

if [[ -n "$source_commit" && -n "$source_subject" ]]; then
  release_notes="$source_commit · $source_subject · Jenkins build #$build_number"
else
  release_notes="Jenkins build #$build_number"
fi

release_metadata="$(mktemp)"
RELEASE_VERSION="$version" \
RELEASE_NOTES="$release_notes" \
RELEASE_SIZE="$(stat -c %s "$msi_path")" \
RELEASE_SHA256="$expected_sha" \
RELEASE_UPLOADED_AT="$(date -u +%Y-%m-%dT%H:%M:%S.%NZ)" \
python3 - "$release_metadata" <<'PY'
import json
import os
import sys

with open(sys.argv[1], "w", encoding="utf-8") as file:
    json.dump({
        "version": os.environ["RELEASE_VERSION"],
        "releaseNotes": os.environ["RELEASE_NOTES"],
        "fileSize": int(os.environ["RELEASE_SIZE"]),
        "sha256": os.environ["RELEASE_SHA256"],
        "uploadedAtUtc": os.environ["RELEASE_UPLOADED_AT"],
    }, file, ensure_ascii=False, indent=2)
    file.write("\n")
PY

remote_path="/tmp/orbita-worker-${version//./-}-${RANDOM}.msi"
remote_metadata="${remote_path%.msi}.json"
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
  rm -f -- "$release_metadata"
  ssh "${ssh_options[@]}" "$ORBITA_USER@$ORBITA_HOST" "rm -f -- '$remote_path' '$remote_metadata'" >/dev/null 2>&1 || true
}
trap cleanup_remote EXIT

scp "${scp_options[@]}" "$msi_path" "$ORBITA_USER@$ORBITA_HOST:$remote_path"
scp "${scp_options[@]}" "$release_metadata" "$ORBITA_USER@$ORBITA_HOST:$remote_metadata"
ssh "${ssh_options[@]}" "$ORBITA_USER@$ORBITA_HOST" \
  "VERSION='$version' EXPECTED_SHA='$expected_sha' REMOTE_PATH='$remote_path' REMOTE_METADATA='$remote_metadata' bash -s" <<'REMOTE'
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
docker cp "$REMOTE_METADATA" "$container:/app/Data/releases/worker/v$VERSION/version.json"

docker exec -e VERSION -e EXPECTED_SHA "$container" sh -ec '
  release_dir="/app/Data/releases/worker/v$VERSION"
  package_path="$release_dir/update.msi"
  actual_sha="$(sha256sum "$package_path" | awk "{print \$1}")"
  test "$actual_sha" = "$EXPECTED_SHA"
  cp "$release_dir/version.json" /app/Data/releases/worker/latest.json
'
REMOTE

echo "Published Orbita Worker $version"
