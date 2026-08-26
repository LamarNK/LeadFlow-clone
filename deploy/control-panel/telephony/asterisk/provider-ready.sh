#!/usr/bin/env bash
set -euo pipefail

if [[ "${ASTERISK_PRIMARY_TRUNK_ENABLED:-auto}" == "true" ]]; then
  output="$(asterisk -rx 'pjsip show registration orbita-provider-registration' 2>&1)"
  printf '%s\n' "${output}"
  if grep -Eq '(^|[[:space:]])Registered([[:space:]]|$)' <<< "${output}"; then
    exit 0
  fi
  echo 'Static upstream SIP trunk is not registered.' >&2
  exit 1
fi

runtime_dir="${ASTERISK_RUNTIME_CONFIG_DIR:-/var/lib/orbita/telephony-runtime}"
found=false
registered=false
while IFS= read -r -d '' file; do
  while IFS='=' read -r account_key registration_name; do
    [[ "${account_key}" =~ ^[a-z0-9_-]{1,16}$ ]] || continue
    [[ "${registration_name}" =~ ^beeline-[0-9a-f]{32}(-[a-z0-9_-]{1,16})?-registration$ ]] || continue
    found=true
    output="$(asterisk -rx "pjsip show registration ${registration_name}" 2>&1)"
    printf '%s\n' "${output}"
    if grep -Eq '(^|[[:space:]])Registered([[:space:]]|$)' <<< "${output}"; then
      registered=true
    fi
  done < "${file}"
done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'beeline.*.accounts' -print0 | sort -z)

while IFS= read -r -d '' file; do
  while IFS='=' read -r account_key registration_name; do
    [[ "${account_key}" =~ ^[a-z0-9_-]{1,16}$ ]] || continue
    [[ "${registration_name}" =~ ^plusofon-[0-9a-f]{32}(-[a-z0-9_-]{1,16})?-registration$ ]] || continue
    found=true
    output="$(asterisk -rx "pjsip show registration ${registration_name}" 2>&1)"
    printf '%s\n' "${output}"
    if grep -Eq '(^|[[:space:]])Registered([[:space:]]|$)' <<< "${output}"; then
      registered=true
    fi
  done < "${file}"
done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'plusofon.*.accounts' -print0 | sort -z)

# Compatibility with a Plusofon line written before multi-line account indexes
# were introduced. New writes always include the .accounts companion file.
while IFS= read -r -d '' file; do
  name="$(basename "${file}")"
  office_id="${name#plusofon.}"
  office_id="${office_id%.conf}"
  [[ "${office_id}" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]] || continue
  [[ ! -f "${runtime_dir}/plusofon.${office_id,,}.accounts" ]] || continue
  office_key="${office_id//-/}"
  registration_name="plusofon-${office_key,,}-registration"
  found=true
  output="$(asterisk -rx "pjsip show registration ${registration_name}" 2>&1)"
  printf '%s\n' "${output}"
  if grep -Eq '(^|[[:space:]])Registered([[:space:]]|$)' <<< "${output}"; then
    registered=true
  fi
done < <(find "${runtime_dir}" -maxdepth 1 -type f -name 'plusofon.*.conf' -print0 | sort -z)

if [[ "${found}" != "true" ]]; then
  echo 'No office SIP lines are configured in Orbita.' >&2
  exit 1
fi
if [[ "${registered}" != "true" ]]; then
  echo 'No office SIP line is registered.' >&2
  exit 1
fi
