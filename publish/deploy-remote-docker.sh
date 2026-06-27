#!/usr/bin/env bash
set -euo pipefail

archive="${1:?image archive is required}"
service="${2:?compose service is required}"
remote_dir="${3:-/opt/orbita}"

if [[ ! -f "$archive" ]]; then
  echo "Archive not found: $archive"
  exit 1
fi

if [[ ! -d "$remote_dir" ]]; then
  echo "Remote directory not found: $remote_dir"
  exit 1
fi

docker load -i "$archive"
rm -f "$archive"

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
echo "Deployed service: $service"