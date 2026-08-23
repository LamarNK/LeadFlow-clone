#!/usr/bin/env bash
set -euo pipefail

runtime_dir="${ASTERISK_RUNTIME_CONFIG_DIR:-/var/lib/orbita/telephony-runtime}"
asterisk_beeline_config="/etc/asterisk/orbita/pjsip.beeline.conf"
asterisk_webrtc_config="/etc/asterisk/orbita/pjsip.webrtc.conf"
last_beeline_config_hash=""
last_webrtc_config_hash=""
last_routes_hash=""

mkdir -p "${runtime_dir}"

is_office_id() {
  [[ "${1:-}" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]]
}

office_from_file() {
  local name prefix suffix office
  name="$(basename "$1")"
  prefix="$2"
  suffix="$3"
  office="${name#${prefix}}"
  office="${office%${suffix}}"
  if is_office_id "${office}"; then
    printf '%s' "${office,,}"
  fi
}

endpoint_key() {
  printf '%s' "${1//-/}"
}

write_status() {
  local office_id="$1"
  local status="$2"
  local path="${runtime_dir}/beeline.${office_id}.status"
  local temporary="${path}.tmp"
  printf '%s\n%s\n' "${status}" "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" > "${temporary}"
  mv -f "${temporary}" "${path}"
}

wait_for_asterisk() {
  until /usr/sbin/asterisk -rx 'core show uptime' >/dev/null 2>&1; do
    sleep 1
  done
}

hash_files() {
  local pattern="$1"
  local matches=()
  while IFS= read -r -d '' file; do
    matches+=("${file}")
  done < <(find "${runtime_dir}" -maxdepth 1 -type f -name "${pattern}" -print0 | sort -z)

  if [[ "${#matches[@]}" -eq 0 ]]; then
    printf 'empty'
    return
  fi
  sha256sum "${matches[@]}" | sha256sum | awk '{print $1}'
}

apply_configs_if_changed() {
  local beeline_config_hash webrtc_config_hash
  beeline_config_hash="$(hash_files 'beeline.*.conf')"
  if [[ "${ASTERISK_WEBRTC_ENABLED:-false}" == "true" ]]; then
    webrtc_config_hash="$(hash_files 'webrtc.*.conf')"
  else
    webrtc_config_hash="disabled"
  fi
  if [[ "${beeline_config_hash}" == "${last_beeline_config_hash}" \
        && "${webrtc_config_hash}" == "${last_webrtc_config_hash}" ]]; then
    return 0
  fi

  local beeline_temporary="${asterisk_beeline_config}.tmp"
  local webrtc_temporary="${asterisk_webrtc_config}.tmp"
  printf '; Generated from all configured Orbita offices.\n' > "${beeline_temporary}"
  while IFS= read -r -d '' file; do
    local office_id
    office_id="$(office_from_file "${file}" 'beeline.' '.conf')"
    [[ -n "${office_id}" ]] || continue
    printf '\n; Office %s\n' "${office_id}" >> "${beeline_temporary}"
    cat "${file}" >> "${beeline_temporary}"
  done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'beeline.*.conf' -print0 | sort -z)

  if [[ "${ASTERISK_WEBRTC_ENABLED:-false}" == "true" ]]; then
    printf '; Generated browser endpoints from all configured Orbita offices.\n' > "${webrtc_temporary}"
    while IFS= read -r -d '' file; do
      local office_id
      office_id="$(office_from_file "${file}" 'webrtc.' '.conf')"
      [[ -n "${office_id}" ]] || continue
      printf '\n; Office %s\n' "${office_id}" >> "${webrtc_temporary}"
      cat "${file}" >> "${webrtc_temporary}"
    done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'webrtc.*.conf' -print0 | sort -z)
  else
    printf '; Browser WebRTC endpoints are disabled.\n' > "${webrtc_temporary}"
  fi

  install -o asterisk -g asterisk -m 0600 "${beeline_temporary}" "${asterisk_beeline_config}"
  install -o asterisk -g asterisk -m 0600 "${webrtc_temporary}" "${asterisk_webrtc_config}"
  rm -f "${beeline_temporary}" "${webrtc_temporary}"
  if /usr/sbin/asterisk -rx 'pjsip reload' >/dev/null 2>&1; then
    last_beeline_config_hash="${beeline_config_hash}"
    last_webrtc_config_hash="${webrtc_config_hash}"
    while IFS= read -r -d '' file; do
      local office_id
      office_id="$(office_from_file "${file}" 'beeline.' '.conf')"
      [[ -n "${office_id}" ]] && write_status "${office_id}" "pending"
    done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'beeline.*.conf' -print0 | sort -z)
  else
    while IFS= read -r -d '' file; do
      local office_id
      office_id="$(office_from_file "${file}" 'beeline.' '.conf')"
      [[ -n "${office_id}" ]] && write_status "${office_id}" "reload-failed"
    done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'beeline.*.conf' -print0 | sort -z)
  fi
}

apply_routes_if_changed() {
  local routes_hash outbound_hash combined_hash
  routes_hash="$(hash_files 'routes.*.conf')"
  outbound_hash="$(hash_files 'beeline.*.outbound')"
  combined_hash="${routes_hash}:${outbound_hash}"
  if [[ "${combined_hash}" == "${last_routes_hash}" ]]; then
    return 0
  fi

  /usr/sbin/asterisk -rx 'database deltree orbita_user outbound_endpoint' >/dev/null 2>&1 || true
  /usr/sbin/asterisk -rx 'database deltree orbita_user office_id' >/dev/null 2>&1 || true
  /usr/sbin/asterisk -rx 'database deltree orbita_office outbound_endpoint' >/dev/null 2>&1 || true

  declare -A seen_extensions=()
  while IFS= read -r -d '' file; do
    local office_id office_key
    office_id="$(office_from_file "${file}" 'routes.' '.conf')"
    [[ -n "${office_id}" ]] || continue
    office_key="$(endpoint_key "${office_id}")"

    while IFS='=' read -r extension provider; do
      [[ "${extension}" =~ ^[0-9]{1,8}$ ]] || continue
      if [[ -n "${seen_extensions[${extension}]:-}" ]]; then
        echo "Duplicate Asterisk extension ${extension} in offices ${seen_extensions[${extension}]} and ${office_id}; keeping the first route." >&2
        continue
      fi
      seen_extensions["${extension}"]="${office_id}"

      local endpoint
      if [[ "${provider}" == "beeline" ]]; then
        endpoint="beeline-${office_key}"
      elif [[ "${provider}" =~ ^beeline-[0-9a-f]{32}-[a-z0-9_-]{1,16}$ ]]; then
        endpoint="${provider}"
      else
        endpoint="orbita-provider"
      fi
      /usr/sbin/asterisk -rx "database put orbita_user outbound_endpoint/${extension} ${endpoint}" >/dev/null
      /usr/sbin/asterisk -rx "database put orbita_user office_id/${extension} ${office_id}" >/dev/null
    done < "${file}"
  done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'routes.*.conf' -print0 | sort -z)

  while IFS= read -r -d '' file; do
    local office_id office_key outbound endpoint
    office_id="$(office_from_file "${file}" 'beeline.' '.outbound')"
    [[ -n "${office_id}" ]] || continue
    office_key="$(endpoint_key "${office_id}")"
    outbound="$(tr -d '\r\n ' < "${file}")"
    if [[ "${outbound}" == "beeline" ]]; then
      endpoint="beeline-${office_key}"
    elif [[ "${outbound}" =~ ^beeline-[0-9a-f]{32}-[a-z0-9_-]{1,16}$ ]]; then
      endpoint="${outbound}"
    else
      endpoint="orbita-provider"
    fi
    /usr/sbin/asterisk -rx "database put orbita_office outbound_endpoint/${office_id} ${endpoint}" >/dev/null
  done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'beeline.*.outbound' -print0 | sort -z)

  last_routes_hash="${combined_hash}"
}

update_registration_statuses() {
  while IFS= read -r -d '' file; do
    local office_id status_path temporary
    office_id="$(office_from_file "${file}" 'beeline.' '.accounts')"
    [[ -n "${office_id}" ]] || continue
    status_path="${runtime_dir}/beeline.${office_id}.status"
    temporary="${status_path}.tmp"
    : > "${temporary}"
    while IFS='=' read -r account_key registration_name; do
      [[ "${account_key}" =~ ^[a-z0-9_-]{1,16}$ ]] || continue
      [[ "${registration_name}" =~ ^beeline-[0-9a-f]{32}(-[a-z0-9_-]{1,16})?-registration$ ]] || continue
      local output status
      output="$(/usr/sbin/asterisk -rx "pjsip show registration ${registration_name}" 2>/dev/null || true)"
      if grep -qiE '(^|[[:space:]])Registered([[:space:]]|$)' <<< "${output}"; then
        status="registered"
      elif grep -qiE 'Rejected|Forbidden|Auth\. Sent' <<< "${output}"; then
        status="rejected"
      elif grep -qiE 'Unregistered|Stopped' <<< "${output}"; then
        status="unregistered"
      else
        status="pending"
      fi
      printf '%s=%s\n' "${account_key}" "${status}" >> "${temporary}"
    done < "${file}"
    printf '%s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" >> "${temporary}"
    mv -f "${temporary}" "${status_path}"
  done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'beeline.*.accounts' -print0 | sort -z)
}

wait_for_asterisk
while true; do
  apply_configs_if_changed
  apply_routes_if_changed
  update_registration_statuses
  sleep 5
done
