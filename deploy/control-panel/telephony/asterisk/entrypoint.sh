#!/usr/bin/env bash
set -euo pipefail

required=(
  ASTERISK_EXTERNAL_ADDRESS
  ASTERISK_DEFAULT_EXTENSION
  TELEPHONY_GATEWAY_INTERNAL_URL
)
for name in "${required[@]}"; do
  if [[ -z "${!name:-}" ]]; then
    echo "Required environment variable is missing: ${name}" >&2
    exit 1
  fi
done

export ASTERISK_SIP_PORT="${ASTERISK_SIP_PORT:-5060}"
export ASTERISK_SIP_TRANSPORT="${ASTERISK_SIP_TRANSPORT:-tcp}"
export ASTERISK_LOCAL_NET="${ASTERISK_LOCAL_NET:-172.16.0.0/12}"
export ASTERISK_SIP_AUTH_LOGIN="${ASTERISK_SIP_AUTH_LOGIN:-${ASTERISK_SIP_LOGIN}}"
export ASTERISK_WEBRTC_ENABLED="${ASTERISK_WEBRTC_ENABLED:-false}"
export ASTERISK_PRIMARY_TRUNK_ENABLED="${ASTERISK_PRIMARY_TRUNK_ENABLED:-auto}"
export ASTERISK_DEFAULT_OFFICE_ID="${ASTERISK_DEFAULT_OFFICE_ID:-${ASTERISK_OFFICE_ID:-}}"

case "${ASTERISK_PRIMARY_TRUNK_ENABLED}" in
  auto)
    if [[ -n "${ASTERISK_SIP_SERVER:-}" || -n "${ASTERISK_SIP_LOGIN:-}" || -n "${ASTERISK_SIP_PASSWORD:-}" ]]; then
      export ASTERISK_PRIMARY_TRUNK_ENABLED=true
    else
      export ASTERISK_PRIMARY_TRUNK_ENABLED=false
    fi
    ;;
  true|false) ;;
  *)
    echo "ASTERISK_PRIMARY_TRUNK_ENABLED must be true, false or auto." >&2
    exit 1
    ;;
esac

if [[ "${ASTERISK_PRIMARY_TRUNK_ENABLED}" == "true" ]]; then
  primary_required=(
    ASTERISK_SIP_SERVER
    ASTERISK_SIP_LOGIN
    ASTERISK_SIP_PASSWORD
  )
  for name in "${primary_required[@]}"; do
    if [[ -z "${!name:-}" ]]; then
      echo "Required primary trunk variable is missing: ${name}" >&2
      exit 1
    fi
  done
  if [[ ! "${ASTERISK_DEFAULT_OFFICE_ID}" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]]; then
    echo "ASTERISK_DEFAULT_OFFICE_ID must contain the fallback office UUID when the primary trunk is enabled." >&2
    exit 1
  fi
  case "${ASTERISK_SIP_TRANSPORT}" in
    tcp|udp) ;;
    *)
      echo "ASTERISK_SIP_TRANSPORT must be tcp or udp." >&2
      exit 1
      ;;
  esac
  export ASTERISK_OUTBOUND_ENDPOINT="orbita-provider"
else
  # Every office-managed line is resolved through the shared runtime volume.
  # A deliberate non-existent endpoint fails safely if a user has no route.
  export ASTERISK_OUTBOUND_ENDPOINT="unconfigured-outbound"
fi

envsubst '${ASTERISK_EXTERNAL_ADDRESS} ${ASTERISK_LOCAL_NET} ${ASTERISK_SIP_SERVER} ${ASTERISK_SIP_PORT} ${ASTERISK_SIP_LOGIN} ${ASTERISK_SIP_AUTH_LOGIN} ${ASTERISK_SIP_PASSWORD} ${ASTERISK_SIP_TRANSPORT} ${ASTERISK_DEFAULT_OFFICE_ID}' \
  < /opt/orbita-asterisk/pjsip.conf.template \
  > /etc/asterisk/pjsip.conf
if [[ "${ASTERISK_PRIMARY_TRUNK_ENABLED}" == "true" ]]; then
  envsubst '${ASTERISK_SIP_SERVER} ${ASTERISK_SIP_PORT} ${ASTERISK_SIP_LOGIN} ${ASTERISK_SIP_AUTH_LOGIN} ${ASTERISK_SIP_PASSWORD} ${ASTERISK_SIP_TRANSPORT} ${ASTERISK_DEFAULT_OFFICE_ID}' \
    < /opt/orbita-asterisk/pjsip.primary.conf.template \
    > /etc/asterisk/orbita/pjsip.primary.conf
else
  printf '; Static upstream SIP trunk is disabled. Office lines are managed in Orbita.\n' \
    > /etc/asterisk/orbita/pjsip.primary.conf
fi
envsubst '${ASTERISK_DEFAULT_EXTENSION} ${ASTERISK_OUTBOUND_ENDPOINT}' \
  < /opt/orbita-asterisk/extensions.conf.template \
  > /etc/asterisk/extensions.conf

install -m 0644 /opt/orbita-asterisk/http.conf /etc/asterisk/http.conf
install -m 0644 /opt/orbita-asterisk/rtp.conf /etc/asterisk/rtp.conf
install -m 0644 /opt/orbita-asterisk/modules.conf /etc/asterisk/modules.conf

if [[ "${ASTERISK_WEBRTC_ENABLED}" == "true" ]]; then
  printf '; Browser WebRTC endpoints are loaded from the Orbita runtime config.\n' \
    > /etc/asterisk/orbita/pjsip.webrtc.conf
else
  printf '; Browser WebRTC endpoints are disabled.\n' > /etc/asterisk/orbita/pjsip.webrtc.conf
fi

# This file is populated by watch-runtime-config.sh from the database-backed
# office configuration. It must never be generated from .env credentials.
printf '; Office SIP lines are loaded from the Orbita runtime configuration.\n' \
  > /etc/asterisk/orbita/pjsip.beeline.conf

chown asterisk:asterisk \
  /etc/asterisk/pjsip.conf \
  /etc/asterisk/orbita/pjsip.primary.conf \
  /etc/asterisk/extensions.conf \
  /etc/asterisk/http.conf \
  /etc/asterisk/rtp.conf \
  /etc/asterisk/modules.conf \
  /etc/asterisk/orbita/pjsip.webrtc.conf \
  /etc/asterisk/orbita/pjsip.beeline.conf

echo "Starting shared Orbita PBX: fallback office=${ASTERISK_DEFAULT_OFFICE_ID:-none}, primary trunk=${ASTERISK_PRIMARY_TRUNK_ENABLED}, runtime office lines enabled."

/opt/orbita-asterisk/retry-pending.sh &
/opt/orbita-asterisk/watch-runtime-config.sh &
exec /usr/sbin/asterisk -f -U asterisk -G asterisk -vvv
