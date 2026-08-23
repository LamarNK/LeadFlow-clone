#!/usr/bin/env bash
set -euo pipefail

required=(
  ASTERISK_DEFAULT_EXTENSION
  TELEPHONY_GATEWAY_INTERNAL_URL
)
for name in "${required[@]}"; do
  if [[ -z "${!name:-}" ]]; then
    echo "Required environment variable is missing: ${name}" >&2
    exit 1
  fi
done

/opt/orbita-asterisk/healthcheck.sh
echo 'Asterisk process: ready'

curl --fail --silent --show-error --connect-timeout 5 --max-time 10 \
  "${TELEPHONY_GATEWAY_INTERNAL_URL%/}/health/ready" >/dev/null
echo 'Orbita telephony gateway: ready'

/opt/orbita-asterisk/provider-ready.sh >/dev/null
echo 'Upstream SIP registration: registered'

endpoint_output="$(asterisk -rx "pjsip show endpoint ${ASTERISK_DEFAULT_EXTENSION}" 2>&1)"
if grep -qiE 'Unable to find object|No objects found' <<< "${endpoint_output}"; then
  echo "Manager endpoint ${ASTERISK_DEFAULT_EXTENSION} is not configured." >&2
  exit 1
fi
echo "Manager endpoint ${ASTERISK_DEFAULT_EXTENSION}: configured"

recording_probe="/var/spool/asterisk/recording/.readiness-$$"
touch "${recording_probe}"
rm -f "${recording_probe}"
echo 'Recording spool: writable'

echo 'PBX pilot prerequisites are ready. The manager softphone may now register.'
