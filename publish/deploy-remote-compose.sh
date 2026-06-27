#!/usr/bin/env bash
set -euo pipefail

service="${1:?compose service is required}"
remote_dir="${2:-/opt/orbita}"

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
echo "Recreated $service from updated compose"