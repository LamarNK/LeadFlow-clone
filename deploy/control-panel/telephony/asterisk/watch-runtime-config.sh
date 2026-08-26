#!/usr/bin/env bash
set -euo pipefail

runtime_dir="${ASTERISK_RUNTIME_CONFIG_DIR:-/var/lib/orbita/telephony-runtime}"
asterisk_provider_config="/etc/asterisk/orbita/pjsip.beeline.conf"
asterisk_webrtc_config="/etc/asterisk/orbita/pjsip.webrtc.conf"
last_provider_config_hash=""
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

write_default_status() {
  local provider="$1"
  local office_id="$2"
  local status="$3"
  local detail="${4:-}"
  local path="${runtime_dir}/${provider}.${office_id}.status"
  local temporary="${path}.tmp"
  printf 'default=%s\n' "${status}" > "${temporary}"
  [[ -z "${detail}" ]] || printf 'default.detail=%s\n' "${detail}" >> "${temporary}"
  printf '%s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" >> "${temporary}"
  mv -f "${temporary}" "${path}"
}

write_beeline_accounts_status() {
  local office_id="$1"
  local status="$2"
  local accounts_path="${runtime_dir}/beeline.${office_id}.accounts"
  local status_path="${runtime_dir}/beeline.${office_id}.status"
  local temporary="${status_path}.tmp"
  : > "${temporary}"
  if [[ -f "${accounts_path}" ]]; then
    while IFS='=' read -r account_key registration_name; do
      [[ "${account_key}" =~ ^[a-z0-9_-]{1,16}$ ]] || continue
      [[ "${registration_name}" =~ ^beeline-[0-9a-f]{32}(-[a-z0-9_-]{1,16})?-registration$ ]] || continue
      printf '%s=%s\n' "${account_key}" "${status}" >> "${temporary}"
    done < "${accounts_path}"
  fi
  printf '%s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" >> "${temporary}"
  mv -f "${temporary}" "${status_path}"
}

write_plusofon_accounts_status() {
  local office_id="$1"
  local status="$2"
  local accounts_path="${runtime_dir}/plusofon.${office_id}.accounts"
  local status_path="${runtime_dir}/plusofon.${office_id}.status"
  local temporary="${status_path}.tmp"
  local account_written=false
  : > "${temporary}"
  if [[ -f "${accounts_path}" ]]; then
    while IFS='=' read -r account_key registration_name; do
      [[ "${account_key}" =~ ^[a-z0-9_-]{1,16}$ ]] || continue
      [[ "${registration_name}" =~ ^plusofon-[0-9a-f]{32}(-[a-z0-9_-]{1,16})?-registration$ ]] || continue
      printf '%s=%s\n' "${account_key}" "${status}" >> "${temporary}"
      account_written=true
    done < "${accounts_path}"
  fi
  if [[ "${account_written}" != "true" ]]; then
    printf 'default=%s\n' "${status}" >> "${temporary}"
  fi
  printf '%s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" >> "${temporary}"
  mv -f "${temporary}" "${status_path}"
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

is_ascii_pjsip_value() {
  local value="$1"
  ! LC_ALL=C grep -q '[^ -~]' <<< "${value}"
}

is_runtime_config_valid() {
  local file="$1"
  local key value
  while IFS='=' read -r key value; do
    case "${key}" in
      username|contact|outbound_proxy|from_user|from_domain|match|server_uri|client_uri|contact_user)
        if [[ -z "${value}" ]] || ! is_ascii_pjsip_value "${value}"; then
          return 1
        fi
        ;;
    esac
  done < "${file}"
  return 0
}

is_endpoint_loaded() {
  local endpoint="$1"
  /usr/sbin/asterisk -rx "pjsip show endpoint ${endpoint}" 2>/dev/null \
    | grep -Fq "Endpoint:  ${endpoint}"
}

all_provider_auths_loaded() {
  local auth_name output
  while IFS= read -r auth_name; do
    [[ "${auth_name}" =~ ^(beeline|plusofon)-[0-9a-f]{32}(-[a-z0-9_-]{1,16})?-auth$ ]] || continue
    output="$(/usr/sbin/asterisk -rx "pjsip show auth ${auth_name}" 2>/dev/null || true)"
    # Do not print this command's output: some Asterisk versions expose
    # credential metadata. The object name is enough to verify the reload.
    if ! grep -Fq "${auth_name}/" <<< "${output}"; then
      return 1
    fi
  done < <(sed -nE 's/^\[([^]]+-auth)\]$/\1/p' "${asterisk_provider_config}")
  return 0
}

registration_detail() {
  local output="$1" client_uri latest
  client_uri="$(sed -nE 's/^[[:space:]]*client_uri[[:space:]]*:[[:space:]]*(.*)$/\1/p' <<< "${output}" | head -n 1)"
  [[ -n "${client_uri}" && -f /var/log/asterisk/messages.log ]] || return 0
  latest="$(grep -F "registration attempt to '${client_uri}'" /var/log/asterisk/messages.log 2>/dev/null | tail -n 1 || true)"
  case "${latest}" in
    *'403 Forbidden'*|*"Fatal response '403'"*) printf '403 Forbidden' ;;
    *'401 Unauthorized'*|*"Fatal response '401'"*) printf '401 Unauthorized' ;;
    *'408 Request Timeout'*|*"Fatal response '408'"*) printf '408 Request Timeout' ;;
  esac
}

apply_configs_if_changed() {
  local provider_config_hash webrtc_config_hash
  provider_config_hash="$(hash_files 'beeline.*.conf'):$(hash_files 'plusofon.*.conf')"
  if [[ "${ASTERISK_WEBRTC_ENABLED:-false}" == "true" ]]; then
    webrtc_config_hash="$(hash_files 'webrtc.*.conf')"
  else
    webrtc_config_hash="disabled"
  fi
  if [[ "${provider_config_hash}" == "${last_provider_config_hash}" \
        && "${webrtc_config_hash}" == "${last_webrtc_config_hash}" ]]; then
    return 0
  fi

  local provider_temporary="${asterisk_provider_config}.tmp"
  local webrtc_temporary="${asterisk_webrtc_config}.tmp"
  printf '; Generated from all configured Orbita offices.\n' > "${provider_temporary}"
  local provider
  for provider in beeline plusofon; do
    while IFS= read -r -d '' file; do
      local office_id
      office_id="$(office_from_file "${file}" "${provider}." '.conf')"
      [[ -n "${office_id}" ]] || continue
      if ! is_runtime_config_valid "${file}"; then
        echo "Ignoring invalid ${provider} runtime config for office ${office_id}." >&2
        if [[ "${provider}" == "beeline" ]]; then
          write_beeline_accounts_status "${office_id}" "reload-failed"
        else
          write_plusofon_accounts_status "${office_id}" "reload-failed"
        fi
        continue
      fi
      printf '\n; %s office %s\n' "${provider}" "${office_id}" >> "${provider_temporary}"
      cat "${file}" >> "${provider_temporary}"
    done < <(find "${runtime_dir}" -maxdepth 1 -type f -name "${provider}.*.conf" -print0 | sort -z)
  done

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

  install -o asterisk -g asterisk -m 0600 "${provider_temporary}" "${asterisk_provider_config}"
  install -o asterisk -g asterisk -m 0600 "${webrtc_temporary}" "${asterisk_webrtc_config}"
  rm -f "${provider_temporary}" "${webrtc_temporary}"

  if /usr/sbin/asterisk -rx 'pjsip reload' >/dev/null 2>&1 && all_provider_auths_loaded; then
    last_provider_config_hash="${provider_config_hash}"
    last_webrtc_config_hash="${webrtc_config_hash}"
    last_routes_hash=""

    while IFS= read -r -d '' file; do
      local office_id account_key registration_name
      office_id="$(office_from_file "${file}" 'beeline.' '.accounts')"
      [[ -n "${office_id}" ]] || continue
      while IFS='=' read -r account_key registration_name; do
        [[ "${registration_name}" =~ ^beeline-[0-9a-f]{32}(-[a-z0-9_-]{1,16})?-registration$ ]] || continue
        /usr/sbin/asterisk -rx "pjsip send register ${registration_name}" >/dev/null 2>&1 || true
      done < "${file}"
    done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'beeline.*.accounts' -print0 | sort -z)

    while IFS= read -r -d '' file; do
      local office_id account_key registration_name
      office_id="$(office_from_file "${file}" 'plusofon.' '.accounts')"
      [[ -n "${office_id}" ]] || continue
      write_plusofon_accounts_status "${office_id}" "pending"
      while IFS='=' read -r account_key registration_name; do
        [[ "${registration_name}" =~ ^plusofon-[0-9a-f]{32}(-[a-z0-9_-]{1,16})?-registration$ ]] || continue
        /usr/sbin/asterisk -rx "pjsip send register ${registration_name}" >/dev/null 2>&1 || true
      done < "${file}"
    done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'plusofon.*.accounts' -print0 | sort -z)

    while IFS= read -r -d '' file; do
      local office_id office_key
      office_id="$(office_from_file "${file}" 'plusofon.' '.conf')"
      [[ -n "${office_id}" ]] || continue
      [[ ! -f "${runtime_dir}/plusofon.${office_id}.accounts" ]] || continue
      office_key="$(endpoint_key "${office_id}")"
      write_default_status "plusofon" "${office_id}" "pending"
      /usr/sbin/asterisk -rx "pjsip send register plusofon-${office_key}-registration" >/dev/null 2>&1 || true
    done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'plusofon.*.conf' -print0 | sort -z)
  else
    for provider in beeline plusofon; do
      while IFS= read -r -d '' file; do
        local office_id
        office_id="$(office_from_file "${file}" "${provider}." '.conf')"
        [[ -n "${office_id}" ]] || continue
        if [[ "${provider}" == "beeline" ]]; then
          write_beeline_accounts_status "${office_id}" "reload-failed"
        else
          write_plusofon_accounts_status "${office_id}" "reload-failed"
        fi
      done < <(find "${runtime_dir}" -maxdepth 1 -type f -name "${provider}.*.conf" -print0 | sort -z)
    done
  fi
}

resolve_endpoint() {
  local provider="$1"
  local office_id="$2"
  local office_key
  office_key="$(endpoint_key "${office_id}")"

  case "${provider}" in
    default)
      # "По маршруту провайдера" means the office default, not the optional
      # legacy orbita-provider trunk. Resolve it here so a newly assigned
      # extension immediately receives the active Plusofon/Beeline endpoint.
      local office_provider=""
      if [[ -f "${runtime_dir}/outbound.${office_id}.conf" ]]; then
        office_provider="$(tr -d '\r\n ' < "${runtime_dir}/outbound.${office_id}.conf")"
      elif [[ -f "${runtime_dir}/beeline.${office_id}.outbound" ]]; then
        office_provider="$(tr -d '\r\n ' < "${runtime_dir}/beeline.${office_id}.outbound")"
      fi
      if [[ -n "${office_provider}" && "${office_provider}" != "default" ]]; then
        resolve_endpoint "${office_provider}" "${office_id}"
      else
        printf 'orbita-provider'
      fi
      ;;
    beeline)
      if [[ -f "${runtime_dir}/beeline.${office_id}.conf" ]] && is_endpoint_loaded "beeline-${office_key}"; then
        printf 'beeline-%s' "${office_key}"
      else
        printf 'orbita-provider'
      fi
      ;;
    beeline-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]*)
      if is_endpoint_loaded "${provider}"; then
        printf '%s' "${provider}"
      else
        printf 'orbita-provider'
      fi
      ;;
    plusofon)
      if [[ -f "${runtime_dir}/plusofon.${office_id}.conf" ]] && is_endpoint_loaded "plusofon-${office_key}"; then
        printf 'plusofon-%s' "${office_key}"
      else
        printf 'orbita-provider'
      fi
      ;;
    plusofon-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]*)
      if is_endpoint_loaded "${provider}"; then
        printf '%s' "${provider}"
      else
        printf 'orbita-provider'
      fi
      ;;
    *)
      printf 'orbita-provider'
      ;;
  esac
}

expects_runtime_endpoint() {
  local provider="$1"
  local office_id="$2"
  case "${provider}" in
    plusofon|plusofon-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]*|beeline|beeline-[0-9a-f][0-9a-f][0-9a-f][0-9a-f]*)
      return 0
      ;;
    default)
      local office_provider=""
      if [[ -f "${runtime_dir}/outbound.${office_id}.conf" ]]; then
        office_provider="$(tr -d '\r\n ' < "${runtime_dir}/outbound.${office_id}.conf")"
      elif [[ -f "${runtime_dir}/beeline.${office_id}.outbound" ]]; then
        office_provider="$(tr -d '\r\n ' < "${runtime_dir}/beeline.${office_id}.outbound")"
      fi
      [[ -n "${office_provider}" && "${office_provider}" != "default" ]] \
        && expects_runtime_endpoint "${office_provider}" "${office_id}"
      return $?
      ;;
  esac
  return 1
}

apply_routes_if_changed() {
  local routes_hash outbound_hash providers_hash combined_hash routes_pending
  routes_hash="$(hash_files 'routes.*.conf')"
  outbound_hash="$(hash_files 'outbound.*.conf'):$(hash_files 'beeline.*.outbound'):$(hash_files 'plusofon.*.callerids'):$(hash_files 'plusofon.*.callerid')"
  providers_hash="$(hash_files 'beeline.*.conf'):$(hash_files 'plusofon.*.conf')"
  combined_hash="${routes_hash}:${outbound_hash}:${providers_hash}"
  if [[ "${combined_hash}" == "${last_routes_hash}" ]]; then
    return 0
  fi
  routes_pending=0

  /usr/sbin/asterisk -rx 'database deltree orbita_user outbound_endpoint' >/dev/null 2>&1 || true
  /usr/sbin/asterisk -rx 'database deltree orbita_user office_id' >/dev/null 2>&1 || true
  /usr/sbin/asterisk -rx 'database deltree orbita_office outbound_endpoint' >/dev/null 2>&1 || true
  /usr/sbin/asterisk -rx 'database deltree orbita_endpoint outbound_caller_id' >/dev/null 2>&1 || true
  /usr/sbin/asterisk -rx 'database deltree orbita_endpoint outbound_domain' >/dev/null 2>&1 || true

  declare -A seen_extensions=()
  while IFS= read -r -d '' file; do
    local office_id
    office_id="$(office_from_file "${file}" 'routes.' '.conf')"
    [[ -n "${office_id}" ]] || continue
    while IFS='=' read -r extension provider; do
      [[ "${extension}" =~ ^[0-9]{1,8}$ ]] || continue
      if [[ -n "${seen_extensions[${extension}]:-}" ]]; then
        echo "Duplicate Asterisk extension ${extension} in offices ${seen_extensions[${extension}]} and ${office_id}; keeping the first route." >&2
        continue
      fi
      seen_extensions["${extension}"]="${office_id}"
      local endpoint
      endpoint="$(resolve_endpoint "${provider}" "${office_id}")"
      if [[ "${endpoint}" == "orbita-provider" ]] \
          && expects_runtime_endpoint "${provider}" "${office_id}"; then
        routes_pending=1
      fi
      /usr/sbin/asterisk -rx "database put orbita_user outbound_endpoint/${extension} ${endpoint}" >/dev/null
      /usr/sbin/asterisk -rx "database put orbita_user office_id/${extension} ${office_id}" >/dev/null
    done < "${file}"
  done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'routes.*.conf' -print0 | sort -z)

  declare -A seen_offices=()
  while IFS= read -r -d '' file; do
    local office_id outbound endpoint
    office_id="$(office_from_file "${file}" 'outbound.' '.conf')"
    [[ -n "${office_id}" ]] || continue
    seen_offices["${office_id}"]=1
    outbound="$(tr -d '\r\n ' < "${file}")"
    endpoint="$(resolve_endpoint "${outbound}" "${office_id}")"
    if [[ "${endpoint}" == "orbita-provider" ]] \
        && expects_runtime_endpoint "${outbound}" "${office_id}"; then
      routes_pending=1
    fi
    /usr/sbin/asterisk -rx "database put orbita_office outbound_endpoint/${office_id} ${endpoint}" >/dev/null
  done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'outbound.*.conf' -print0 | sort -z)

  # Compatibility with offices configured before the generic outbound selector.
  while IFS= read -r -d '' file; do
    local office_id outbound endpoint
    office_id="$(office_from_file "${file}" 'beeline.' '.outbound')"
    [[ -n "${office_id}" && -z "${seen_offices[${office_id}]:-}" ]] || continue
    outbound="$(tr -d '\r\n ' < "${file}")"
    endpoint="$(resolve_endpoint "${outbound}" "${office_id}")"
    if [[ "${endpoint}" == "orbita-provider" ]] \
        && expects_runtime_endpoint "${outbound}" "${office_id}"; then
      routes_pending=1
    fi
    /usr/sbin/asterisk -rx "database put orbita_office outbound_endpoint/${office_id} ${endpoint}" >/dev/null
  done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'beeline.*.outbound' -print0 | sort -z)

  while IFS= read -r -d '' file; do
    local office_id office_key account_key value endpoint caller_id domain
    office_id="$(office_from_file "${file}" 'plusofon.' '.callerids')"
    [[ -n "${office_id}" ]] || continue
    office_key="$(endpoint_key "${office_id}")"
    while IFS='=' read -r account_key value; do
      [[ "${account_key}" =~ ^[a-z0-9_-]{1,16}$ ]] || continue
      caller_id="${value%%|*}"
      domain="${value#*|}"
      [[ "${value}" == *'|'* ]] || continue
      [[ "${caller_id}" =~ ^7[0-9]{10}$ ]] || continue
      [[ "${domain}" =~ ^[A-Za-z0-9.-]{1,253}$ ]] || continue
      if [[ "${account_key}" == "default" ]]; then
        endpoint="plusofon-${office_key}"
      else
        endpoint="plusofon-${office_key}-${account_key}"
      fi
      /usr/sbin/asterisk -rx "database put orbita_endpoint outbound_caller_id/${endpoint} ${caller_id}" >/dev/null
      /usr/sbin/asterisk -rx "database put orbita_endpoint outbound_domain/${endpoint} ${domain}" >/dev/null
    done < "${file}"
  done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'plusofon.*.callerids' -print0 | sort -z)

  while IFS= read -r -d '' file; do
    local office_id office_key endpoint caller_id domain
    office_id="$(office_from_file "${file}" 'plusofon.' '.callerid')"
    [[ -n "${office_id}" ]] || continue
    [[ ! -f "${runtime_dir}/plusofon.${office_id}.callerids" ]] || continue
    office_key="$(endpoint_key "${office_id}")"
    endpoint="plusofon-${office_key}"
    caller_id="$(sed -n '1p' "${file}" | tr -d '\r\n ')"
    domain="$(sed -n '2p' "${file}" | tr -d '\r\n ')"
    [[ "${caller_id}" =~ ^7[0-9]{10}$ ]] || continue
    [[ "${domain}" =~ ^[A-Za-z0-9.-]{1,253}$ ]] || continue
    /usr/sbin/asterisk -rx "database put orbita_endpoint outbound_caller_id/${endpoint} ${caller_id}" >/dev/null
    /usr/sbin/asterisk -rx "database put orbita_endpoint outbound_domain/${endpoint} ${domain}" >/dev/null
  done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'plusofon.*.callerid' -print0 | sort -z)

  if [[ "${routes_pending}" == "1" ]]; then
    # A provider config can appear just before pjsip finishes loading it. Keep
    # retrying instead of permanently caching the legacy fallback endpoint.
    last_routes_hash=""
  else
    last_routes_hash="${combined_hash}"
  fi
}

update_beeline_registration_statuses() {
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
      local output status detail
      output="$(/usr/sbin/asterisk -rx "pjsip show registration ${registration_name}" 2>/dev/null || true)"
      detail=""
      if grep -qiE '(^|[[:space:]])Registered([[:space:]]|$)' <<< "${output}"; then
        status="registered"
      elif grep -qiE 'Rejected|Forbidden|Auth\. Sent' <<< "${output}"; then
        status="rejected"
        detail="$(registration_detail "${output}")"
      elif grep -qiE 'Unregistered|Stopped' <<< "${output}"; then
        status="unregistered"
      else
        status="pending"
      fi
      printf '%s=%s\n' "${account_key}" "${status}" >> "${temporary}"
      [[ -z "${detail}" ]] || printf '%s.detail=%s\n' "${account_key}" "${detail}" >> "${temporary}"
    done < "${file}"
    printf '%s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" >> "${temporary}"
    mv -f "${temporary}" "${status_path}"
  done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'beeline.*.accounts' -print0 | sort -z)
}

update_plusofon_registration_statuses() {
  while IFS= read -r -d '' file; do
    local office_id status_path temporary
    office_id="$(office_from_file "${file}" 'plusofon.' '.accounts')"
    [[ -n "${office_id}" ]] || continue
    status_path="${runtime_dir}/plusofon.${office_id}.status"
    temporary="${status_path}.tmp"
    : > "${temporary}"
    while IFS='=' read -r account_key registration_name; do
      [[ "${account_key}" =~ ^[a-z0-9_-]{1,16}$ ]] || continue
      [[ "${registration_name}" =~ ^plusofon-[0-9a-f]{32}(-[a-z0-9_-]{1,16})?-registration$ ]] || continue
      local output status detail
      output="$(/usr/sbin/asterisk -rx "pjsip show registration ${registration_name}" 2>/dev/null || true)"
      detail=""
      if grep -qiE '(^|[[:space:]])Registered([[:space:]]|$)' <<< "${output}"; then
        status="registered"
      elif grep -qiE 'Rejected|Forbidden|Auth\. Sent' <<< "${output}"; then
        status="rejected"
        detail="$(registration_detail "${output}")"
      elif grep -qiE 'Unregistered|Stopped' <<< "${output}"; then
        status="unregistered"
      else
        status="pending"
      fi
      printf '%s=%s\n' "${account_key}" "${status}" >> "${temporary}"
      [[ -z "${detail}" ]] || printf '%s.detail=%s\n' "${account_key}" "${detail}" >> "${temporary}"
    done < "${file}"
    printf '%s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" >> "${temporary}"
    mv -f "${temporary}" "${status_path}"
  done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'plusofon.*.accounts' -print0 | sort -z)

  while IFS= read -r -d '' file; do
    local office_id office_key output status detail
    office_id="$(office_from_file "${file}" 'plusofon.' '.conf')"
    [[ -n "${office_id}" ]] || continue
    [[ ! -f "${runtime_dir}/plusofon.${office_id}.accounts" ]] || continue
    if ! is_runtime_config_valid "${file}"; then
      write_default_status "plusofon" "${office_id}" "reload-failed"
      continue
    fi
    office_key="$(endpoint_key "${office_id}")"
    output="$(/usr/sbin/asterisk -rx "pjsip show registration plusofon-${office_key}-registration" 2>/dev/null || true)"
    detail=""
    if grep -qiE '(^|[[:space:]])Registered([[:space:]]|$)' <<< "${output}"; then
      status="registered"
    elif grep -qiE 'Rejected|Forbidden|Auth\. Sent' <<< "${output}"; then
      status="rejected"
      detail="$(registration_detail "${output}")"
    elif grep -qiE 'Unregistered|Stopped' <<< "${output}"; then
      status="unregistered"
    else
      status="pending"
    fi
    write_default_status "plusofon" "${office_id}" "${status}" "${detail}"
  done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'plusofon.*.conf' -print0 | sort -z)
}

wait_for_asterisk
while true; do
  apply_configs_if_changed
  apply_routes_if_changed
  update_beeline_registration_statuses
  update_plusofon_registration_statuses
  sleep 5
done
