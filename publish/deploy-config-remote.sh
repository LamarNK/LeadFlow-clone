#!/usr/bin/env bash
set -euo pipefail

remote_dir="${1:-/opt/orbita}"
config_dir="${2:-/tmp/leadflow-orbita-config}"

if [[ ! -d "$config_dir" ]]; then
  echo "Config staging directory not found: $config_dir"
  exit 1
fi

mkdir -p "$remote_dir"

for file in docker-compose.images.yml Caddyfile backup-db.sh; do
  if [[ -f "$config_dir/$file" ]]; then
    cp "$config_dir/$file" "$remote_dir/$file"
    chmod +x "$remote_dir/$file" 2>/dev/null || true
  fi
done

if command -v caddy >/dev/null 2>&1 && [[ -f "$remote_dir/Caddyfile" ]]; then
  if [[ -w /etc/caddy/Caddyfile ]]; then
    cp "$remote_dir/Caddyfile" /etc/caddy/Caddyfile
    systemctl reload caddy
    echo "Caddy reloaded"
  elif sudo -n true 2>/dev/null; then
    sudo -n cp "$remote_dir/Caddyfile" /etc/caddy/Caddyfile
    sudo -n systemctl reload caddy
    echo "Caddy reloaded"
  else
    echo "Caddyfile updated in $remote_dir; reload Caddy manually if needed"
  fi
fi

rm -rf "$config_dir"
echo "Server config updated in $remote_dir"