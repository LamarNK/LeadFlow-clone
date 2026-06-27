#!/usr/bin/env bash
set -euo pipefail

images_dir="${1:?images dir is required}"
compose_file="${2:-/opt/orbita/docker-compose.images.yml}"
env_file="${3:-/opt/orbita/.env}"

cd /opt/orbita

reassemble() {
  local name="$1"
  local parts
  parts=$(ls -1 "${images_dir}/${name}.part"* 2>/dev/null | sort -V || true)
  if [[ -z "$parts" ]]; then
    return 0
  fi

  echo "Reassembling ${name}.tar"
  cat $parts > "${images_dir}/${name}.tar"
}

for image in postgres-16 orbita-api orbita-web; do
  reassemble "$image"
done

load_image() {
  local archive="$1"
  if [[ -f "$archive" ]]; then
    echo "Loading $(basename "$archive")"
    docker load -i "$archive"
  fi
}

load_image "${images_dir}/postgres-16.tar"
load_image "${images_dir}/orbita-api.tar"
load_image "${images_dir}/orbita-web.tar"

if [[ ! -f "$compose_file" ]]; then
  echo "Compose file not found: $compose_file"
  exit 1
fi

if [[ ! -f "$env_file" ]]; then
  echo "Env file not found: $env_file"
  exit 1
fi

docker compose -f "$compose_file" --env-file "$env_file" up -d --remove-orphans
docker compose -f "$compose_file" ps

echo "Orbita deploy complete"