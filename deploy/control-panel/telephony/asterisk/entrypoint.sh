#!/usr/bin/env bash
set -euo pipefail

required=(
  ASTERISK_EXTERNAL_ADDRESS
  ASTERISK_SIP_SERVER
  ASTERISK_SIP_LOGIN
  ASTERISK_SIP_PASSWORD
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
export BEELINE_SIP_ENABLED="${BEELINE_SIP_ENABLED:-false}"
export BEELINE_SIP_PORT="${BEELINE_SIP_PORT:-5060}"
export BEELINE_SIP_TRANSPORT="${BEELINE_SIP_TRANSPORT:-udp}"
export BEELINE_SIP_AUTH_LOGIN="${BEELINE_SIP_AUTH_LOGIN:-${BEELINE_SIP_LOGIN:-}}"
export ASTERISK_OUTBOUND_PROVIDER="${ASTERISK_OUTBOUND_PROVIDER:-primary}"
export ASTERISK_DEFAULT_OFFICE_ID="${ASTERISK_DEFAULT_OFFICE_ID:-${ASTERISK_OFFICE_ID:-}}"

if [[ ! "${ASTERISK_DEFAULT_OFFICE_ID}" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]]; then
  echo "ASTERISK_DEFAULT_OFFICE_ID must contain the fallback office UUID." >&2
  exit 1
fi

case "${ASTERISK_SIP_TRANSPORT}" in
  tcp|udp) ;;
  *)
    echo "ASTERISK_SIP_TRANSPORT must be tcp or udp." >&2
    exit 1
    ;;
esac

case "${BEELINE_SIP_TRANSPORT}" in
  tcp|udp) ;;
  *)
    echo "BEELINE_SIP_TRANSPORT must be tcp or udp." >&2
    exit 1
    ;;
esac

case "${ASTERISK_OUTBOUND_PROVIDER}" in
  primary)
    export ASTERISK_OUTBOUND_ENDPOINT="orbita-provider"
    ;;
  beeline)
    if [[ "${BEELINE_SIP_ENABLED}" != "true" ]]; then
      echo "ASTERISK_OUTBOUND_PROVIDER=beeline requires BEELINE_SIP_ENABLED=true." >&2
      exit 1
    fi
    export ASTERISK_OUTBOUND_ENDPOINT="beeline-provider"
    ;;
  *)
    echo "ASTERISK_OUTBOUND_PROVIDER must be primary or beeline." >&2
    exit 1
    ;;
esac

envsubst '${ASTERISK_EXTERNAL_ADDRESS} ${ASTERISK_LOCAL_NET} ${ASTERISK_SIP_SERVER} ${ASTERISK_SIP_PORT} ${ASTERISK_SIP_LOGIN} ${ASTERISK_SIP_AUTH_LOGIN} ${ASTERISK_SIP_PASSWORD} ${ASTERISK_SIP_TRANSPORT} ${ASTERISK_DEFAULT_OFFICE_ID}' \
  < /opt/orbita-asterisk/pjsip.conf.template \
  > /etc/asterisk/pjsip.conf
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

if [[ "${BEELINE_SIP_ENABLED}" == "true" ]]; then
  beeline_required=(
    BEELINE_SIP_SERVER
    BEELINE_SIP_LOGIN
    BEELINE_SIP_PASSWORD
  )
  for name in "${beeline_required[@]}"; do
    if [[ -z "${!name:-}" ]]; then
      echo "Beeline SIP is enabled but required environment variable is missing: ${name}" >&2
      exit 1
    fi
  done
  envsubst '${BEELINE_SIP_SERVER} ${BEELINE_SIP_PORT} ${BEELINE_SIP_TRANSPORT} ${BEELINE_SIP_LOGIN} ${BEELINE_SIP_AUTH_LOGIN} ${BEELINE_SIP_PASSWORD} ${ASTERISK_DEFAULT_OFFICE_ID}' \
    < /opt/orbita-asterisk/pjsip.beeline.conf.template \
    > /etc/asterisk/orbita/pjsip.beeline.conf
else
  printf '; Beeline SIP trunk is disabled.\n' > /etc/asterisk/orbita/pjsip.beeline.conf
fi

chown asterisk:asterisk \
  /etc/asterisk/pjsip.conf \
  /etc/asterisk/extensions.conf \
  /etc/asterisk/http.conf \
  /etc/asterisk/rtp.conf \
  /etc/asterisk/modules.conf \
  /etc/asterisk/orbita/pjsip.webrtc.conf \
  /etc/asterisk/orbita/pjsip.beeline.conf

echo "Starting shared Orbita PBX: fallback office=${ASTERISK_DEFAULT_OFFICE_ID}, primary registrar=${ASTERISK_SIP_SERVER}:${ASTERISK_SIP_PORT}, transport=${ASTERISK_SIP_TRANSPORT}, outbound=${ASTERISK_OUTBOUND_PROVIDER}, legacy beeline=${BEELINE_SIP_ENABLED}."

/opt/orbita-asterisk/retry-pending.sh &
/opt/orbita-asterisk/watch-runtime-config.sh &
exec /usr/sbin/asterisk -f -U asterisk -G asterisk -vvv
