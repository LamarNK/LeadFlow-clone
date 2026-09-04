#!/usr/bin/env bash
set -euo pipefail

meta="${1:-}"
recording_root='/var/spool/asterisk/recording/'
if [[ ! "$meta" =~ ^${recording_root}[0-9.]+\.wav\.meta$ ]] || [[ ! -f "$meta" ]]; then
  exit 2
fi

sending="${meta}.sending"
if ! mv "$meta" "$sending" 2>/dev/null; then
  exit 0
fi

mapfile -t values < "$sending"
recording="${values[0]:-}"
if [[ ! "$recording" =~ ^${recording_root}[0-9.]+\.wav$ ]]; then
  rm -f "$sending"
  exit 3
fi

office_id="${values[8]:-}"
runtime_dir="${ASTERISK_RUNTIME_CONFIG_DIR:-/var/lib/orbita/telephony-runtime}"
receiver_file="${runtime_dir}/receiver.${office_id}.conf"
public_id=""
webhook_secret=""
if [[ "$office_id" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]] && [[ -f "$receiver_file" ]]; then
  public_id="$(awk -F= '$1 == "public_id" { print substr($0, index($0, "=") + 1); exit }' "$receiver_file")"
  webhook_secret="$(awk -F= '$1 == "secret" { print substr($0, index($0, "=") + 1); exit }' "$receiver_file")"
elif [[ -n "${ASTERISK_PUBLIC_ID:-}" && -n "${ASTERISK_WEBHOOK_SECRET:-}" ]] \
  && [[ "$office_id" == "${ASTERISK_DEFAULT_OFFICE_ID:-${ASTERISK_OFFICE_ID:-}}" ]]; then
  public_id="$ASTERISK_PUBLIC_ID"
  webhook_secret="$ASTERISK_WEBHOOK_SECRET"
fi
if [[ ! "$public_id" =~ ^[0-9a-fA-F-]{36}$ ]] || [[ -z "$webhook_secret" ]]; then
  mv -f "$sending" "$meta"
  exit 4
fi

url="${TELEPHONY_GATEWAY_INTERNAL_URL%/}/api/v1/integrations/telephony/asterisk/${public_id}"
curl_args=(
  --fail-with-body
  --silent
  --show-error
  --connect-timeout 10
  --max-time 300
  -H "X-Orbita-Webhook-Secret: ${webhook_secret}"
  --form-string "external_call_id=${values[1]:-}"
  --form-string "caller_phone=${values[2]:-}"
  --form-string "called_phone=${values[3]:-}"
  --form-string "direction=${values[4]:-}"
  --form-string "internal=${values[5]:-}"
  --form-string "started_at=${values[6]:-}"
  --form-string "duration_seconds=${values[7]:-0}"
  --form-string "disposition=${values[9]:-}"
  --form-string "dial_status=${values[10]:-}"
  --form-string "hangup_cause=${values[11]:-}"
)
if [[ -s "$recording" ]]; then
  curl_args+=(--form "recording=@${recording};type=audio/wav")
fi

if curl "${curl_args[@]}" "$url" >/dev/null; then
  rm -f "$recording" "$sending"
else
  mv -f "$sending" "$meta"
  exit 1
fi
