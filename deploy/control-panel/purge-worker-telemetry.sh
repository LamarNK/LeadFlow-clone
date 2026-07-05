#!/bin/bash
set -euo pipefail

# Purge worker telemetry from Orbita DB while keeping registered workers.
# Keeps: Workers (identity/config), Offices, panel users, Bitrix settings, audit logs.
# Removes: events, snapshots, logs, diagnostics, candidate responses, synced accounts.
#
# Usage:
#   ./purge-worker-telemetry.sh --confirm
#   ./purge-worker-telemetry.sh --confirm --skip-backup
#   ./purge-worker-telemetry.sh --dry-run

ORBITA_DIR="${ORBITA_DIR:-/opt/orbita}"
COMPOSE_FILE="${COMPOSE_FILE:-${ORBITA_DIR}/docker-compose.images.yml}"
CONFIRM=false
DRY_RUN=false
SKIP_BACKUP=false

usage() {
  cat <<'EOF'
Purge Orbita worker telemetry (events, responses, logs, snapshots, accounts).

Options:
  --confirm       Required to apply changes.
  --dry-run       Show row counts only; do not modify data.
  --skip-backup   Skip pre-purge DB backup (not recommended).
  -h, --help      Show this help.

Examples:
  ./purge-worker-telemetry.sh --dry-run
  ./purge-worker-telemetry.sh --confirm
EOF
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --confirm) CONFIRM=true ;;
    --dry-run) DRY_RUN=true ;;
    --skip-backup) SKIP_BACKUP=true ;;
    -h|--help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage; exit 1 ;;
  esac
  shift
done

compose() {
  docker compose -f "$COMPOSE_FILE" --project-directory "$ORBITA_DIR" "$@"
}

psql_orbita() {
  compose exec -T postgres psql -U orbita -d orbita -v ON_ERROR_STOP=1 "$@"
}

print_counts() {
  psql_orbita <<'SQL'
SELECT 'Workers' AS table_name, count(*) FROM "Workers"
UNION ALL SELECT 'WorkerEvents', count(*) FROM "WorkerEvents"
UNION ALL SELECT 'WorkerSnapshots', count(*) FROM "WorkerSnapshots"
UNION ALL SELECT 'WorkerAccounts', count(*) FROM "WorkerAccounts"
UNION ALL SELECT 'WorkerLogEntries', count(*) FROM "WorkerLogEntries"
UNION ALL SELECT 'WorkerDiagnosticAttachments', count(*) FROM "WorkerDiagnosticAttachments"
UNION ALL SELECT 'CandidateResponses', count(*) FROM "CandidateResponses"
ORDER BY table_name;
SQL
}

echo "[$(date -Is)] Current row counts:"
print_counts

if [[ "$DRY_RUN" == true ]]; then
  echo "[$(date -Is)] Dry run complete. No changes applied."
  exit 0
fi

if [[ "$CONFIRM" != true ]]; then
  echo "Refusing to purge without --confirm." >&2
  echo "Run with --dry-run first to inspect counts." >&2
  exit 1
fi

if [[ "$SKIP_BACKUP" != true ]]; then
  echo "[$(date -Is)] Creating pre-purge backup..."
  ORBITA_DIR="$ORBITA_DIR" COMPOSE_FILE="$COMPOSE_FILE" "${ORBITA_DIR}/backup-db.sh"
fi

echo "[$(date -Is)] Purging worker telemetry..."
psql_orbita <<'SQL'
BEGIN;

DELETE FROM "CandidateResponses";
DELETE FROM "WorkerEvents";
DELETE FROM "WorkerSnapshots";
DELETE FROM "WorkerLogEntries";
DELETE FROM "WorkerDiagnosticAttachments";
DELETE FROM "WorkerAccounts";

UPDATE "Workers"
SET
  "AppVersion" = '',
  "MonitoringStatus" = 'Stopped',
  "MonitoringStatusMessage" = NULL,
  "IsMonitoringActive" = false,
  "NextCycleCheckAtUtc" = NULL,
  "LastSeenAtUtc" = NULL,
  "LastCpuPercent" = NULL,
  "LastRamPercent" = NULL,
  "LastRamUsedMb" = NULL,
  "LastRamTotalMb" = NULL,
  "LastUpdateVersion" = NULL,
  "LastUpdateSuccess" = NULL,
  "LastUpdateMessage" = NULL,
  "LastUpdateAtUtc" = NULL,
  "IpAddress" = NULL,
  "OperatingSystem" = NULL,
  "StartedAtUtc" = NULL,
  "AgentVersion" = NULL,
  "PendingCommand" = NULL,
  "PendingCommandAtUtc" = NULL,
  "ActivityPhase" = NULL,
  "ActivityMessage" = NULL,
  "ActivityAccountId" = NULL,
  "ActivityAccountName" = NULL,
  "ActivitySubProfileId" = NULL,
  "ActivitySubProfileName" = NULL,
  "ActivityUpdatedAtUtc" = NULL,
  "ActivityNextCycleAtUtc" = NULL,
  "ActivityActiveAccountsJson" = '[]';

COMMIT;
SQL

echo "[$(date -Is)] Clearing diagnostics volume files..."
compose exec -T api sh -lc 'rm -rf /app/Data/diagnostics/* 2>/dev/null || true'

echo "[$(date -Is)] Row counts after purge:"
print_counts
echo "[$(date -Is)] Worker telemetry purge complete."