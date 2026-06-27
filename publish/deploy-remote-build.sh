#!/usr/bin/env bash
set -euo pipefail

context_archive="${1:?context archive is required}"
dockerfile_rel="${2:?dockerfile path is required}"
image_tag="${3:?image tag is required}"
service="${4:?compose service is required}"
remote_dir="${5:-/opt/orbita}"

if [[ ! -f "$context_archive" ]]; then
  echo "Context archive not found: $context_archive"
  exit 1
fi

if [[ ! -d "$remote_dir" ]]; then
  echo "Remote directory not found: $remote_dir"
  exit 1
fi

workdir="/tmp/leadflow-build-$$"
mkdir -p "$workdir"
tar -xzf "$context_archive" -C "$workdir"
cd "$workdir"

if [[ ! -f "$dockerfile_rel" ]]; then
  echo "Dockerfile not found in context: $dockerfile_rel"
  exit 1
fi

docker build -f "$dockerfile_rel" -t "$image_tag" .
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
  docker compose -f docker-compose.images.yml --env-file .env up -d notifybot-postgres notifybot-api
else
  docker compose -f docker-compose.images.yml --env-file .env up -d "$service"
fi

docker compose -f docker-compose.images.yml ps
rm -rf "$workdir" "$context_archive"
echo "Built and deployed $image_tag ($service)"