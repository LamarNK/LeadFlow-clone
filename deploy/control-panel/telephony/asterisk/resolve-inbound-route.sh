#!/usr/bin/env bash
set -euo pipefail

office_id="${1:-}"
caller="${2:-}"
called="${3:-}"
provider="${4:-}"
account="${5:-}"
if [[ ! "$office_id" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]]; then
  exit 0
fi
if [[ ! "$caller" =~ ^[0-9+]{10,20}$ ]] || [[ ! "$called" =~ ^[0-9+]{1,20}$ ]]; then
  exit 0
fi
if [[ -n "$provider" && ! "$provider" =~ ^(plusofon|beeline|sipout)$ ]]; then
  exit 0
fi
if [[ -n "$account" && ! "$account" =~ ^[a-zA-Z0-9_-]{1,16}$ ]]; then
  exit 0
fi

runtime_dir="${ASTERISK_RUNTIME_CONFIG_DIR:-/var/lib/orbita/telephony-runtime}"
receiver_file="${runtime_dir}/receiver.${office_id,,}.conf"
public_id=""
webhook_secret=""
if [[ -f "$receiver_file" ]]; then
  public_id="$(awk -F= '$1 == "public_id" { print substr($0, index($0, "=") + 1); exit }' "$receiver_file")"
  webhook_secret="$(awk -F= '$1 == "secret" { print substr($0, index($0, "=") + 1); exit }' "$receiver_file")"
elif [[ -n "${ASTERISK_PUBLIC_ID:-}" && -n "${ASTERISK_WEBHOOK_SECRET:-}" ]] \
  && [[ "${office_id,,}" == "${ASTERISK_DEFAULT_OFFICE_ID:-${ASTERISK_OFFICE_ID:-}}" ]]; then
  public_id="$ASTERISK_PUBLIC_ID"
  webhook_secret="$ASTERISK_WEBHOOK_SECRET"
fi
if [[ ! "$public_id" =~ ^[0-9a-fA-F-]{36}$ ]] || [[ -z "$webhook_secret" ]]; then
  exit 0
fi

url="${TELEPHONY_GATEWAY_INTERNAL_URL%/}/api/v1/integrations/telephony/asterisk/${public_id}/route"
response="$(curl \
  --fail \
  --silent \
  --show-error \
  --connect-timeout 1 \
  --max-time 3 \
  -H "X-Orbita-Webhook-Secret: ${webhook_secret}" \
  --get \
  --data-urlencode "caller=${caller}" \
  --data-urlencode "called=${called}" \
  --data-urlencode "provider=${provider}" \
  --data-urlencode "account=${account}" \
  "$url" 2>/dev/null || true)"

# The trusted API returns only an extension, a separator and PJSIP targets.
# Suppress an unexpected response so the dialplan safely falls back to the
# configured default extension.
if [[ "$response" =~ ^[0-9]{0,8}\^([A-Za-z0-9_/-]+(&[A-Za-z0-9_/-]+)*)?$ ]]; then
  printf '%s' "$response"
fi
