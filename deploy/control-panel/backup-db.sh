#!/bin/bash
set -euo pipefail

ORBITA_DIR="${ORBITA_DIR:-/opt/orbita}"
COMPOSE_FILE="${COMPOSE_FILE:-${ORBITA_DIR}/docker-compose.images.yml}"
BACKUP_DIR="${ORBITA_DIR}/backups"
RETENTION_DAYS=14
STAMP=$(date +%Y%m%d_%H%M%S)

mkdir -p "$BACKUP_DIR"

backup_db() {
  local service="$1"
  local user="$2"
  local db="$3"
  local prefix="$4"
  local file="$BACKUP_DIR/${prefix}_${STAMP}.sql.gz"

  docker compose -f "$COMPOSE_FILE" --project-directory "$ORBITA_DIR" exec -T "$service" pg_dump -U "$user" "$db" | gzip > "$file"
  find "$BACKUP_DIR" -name "${prefix}_*.sql.gz" -mtime +$RETENTION_DAYS -delete
  echo "[$(date -Is)] Backup saved: $file ($(du -h "$file" | cut -f1))"
}

backup_db postgres orbita orbita orbita
backup_db notifybot-postgres notifybot notifybot notifybot