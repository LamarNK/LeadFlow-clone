#!/usr/bin/env bash
set -euo pipefail

context_archive="${1:?context archive is required}"
dockerfile_rel="${2:?dockerfile path is required}"
image_tag="${3:?image tag is required}"
service="${4:?compose service is required}"
remote_dir="${5:-/opt/orbita}"
deploy_mode="${6:-full}"

if [[ ! -f "$context_archive" ]]; then
  echo "Context archive not found: $context_archive"
  exit 1
fi

if [[ ! -d "$remote_dir" ]]; then
  echo "Remote directory not found: $remote_dir"
  exit 1
fi

cache_dir="$remote_dir/.build-cache/$service"
extract_dir="/tmp/leadflow-extract-$$"
mkdir -p "$cache_dir" "$extract_dir"

if [[ "$deploy_mode" == "full" ]]; then
  rm -rf "$cache_dir"
  mkdir -p "$cache_dir"
  tar -xzf "$context_archive" -C "$cache_dir"
else
  tar -xzf "$context_archive" -C "$extract_dir"

  if [[ -f "$extract_dir/deleted.txt" ]]; then
    while IFS= read -r rel || [[ -n "$rel" ]]; do
      [[ -z "$rel" ]] && continue
      rm -rf "$cache_dir/$rel"
    done < "$extract_dir/deleted.txt"
    rm -f "$extract_dir/deleted.txt"
  fi

  (
    cd "$extract_dir"
    tar -cf - . | tar -xf - -C "$cache_dir"
  )
fi

rm -rf "$extract_dir" "$context_archive"

cd "$cache_dir"

if [[ ! -f "$dockerfile_rel" ]]; then
  if [[ "$deploy_mode" == "delta" ]]; then
    echo "Build cache incomplete for delta deploy; missing $dockerfile_rel"
    echo "Retrying with a full upload is required."
    exit 42
  fi
  echo "Dockerfile not found in context: $dockerfile_rel"
  exit 1
fi

docker build -f "$dockerfile_rel" -t "$image_tag" .
compose_src="$cache_dir/deploy/control-panel/docker-compose.images.yml"
if [[ -f "$compose_src" ]]; then
  cp "$compose_src" "$remote_dir/docker-compose.images.yml"
  cp "$compose_src" "$remote_dir/docker-compose.yml"
  echo "Updated docker-compose.images.yml and docker-compose.yml in $remote_dir"
fi

cd "$remote_dir"

if [[ ! -f "docker-compose.images.yml" ]]; then
  echo "docker-compose.images.yml not found in $remote_dir"
  exit 1
fi

if [[ ! -f ".env" ]]; then
  echo ".env not found in $remote_dir"
  exit 1
fi

if [[ "$service" == "notifybot-api" ]]; then
  docker compose -f docker-compose.images.yml --env-file .env up -d --force-recreate notifybot-postgres notifybot-api
else
  docker compose -f docker-compose.images.yml --env-file .env up -d --force-recreate "$service"
fi

docker compose -f docker-compose.images.yml ps
echo "Built and deployed $image_tag ($service) using $deploy_mode context"