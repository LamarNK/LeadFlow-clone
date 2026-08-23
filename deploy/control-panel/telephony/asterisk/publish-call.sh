#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 9 ]]; then
  exit 2
fi

call_id="$1"
recording="$8"
office_id="${9,,}"
recording_root='/var/spool/asterisk/recording/'
if [[ ! "$recording" =~ ^${recording_root}[0-9.]+\.wav$ ]]; then
  exit 3
fi
if [[ ! "$office_id" =~ ^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$ ]]; then
  exit 4
fi

meta="${recording}.meta"
temporary="${meta}.$$"
printf '%s\n' "$recording" "$call_id" "$2" "$3" "$4" "$5" "$6" "$7" "$office_id" > "$temporary"
mv -f "$temporary" "$meta"
/opt/orbita-asterisk/deliver-recording.sh "$meta" || true
