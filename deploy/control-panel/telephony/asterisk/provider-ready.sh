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

if [[ "${found}" != "true" ]]; then
  echo 'No office SIP lines are configured in Orbita.' >&2
  exit 1
fi
if [[ "${registered}" != "true" ]]; then
  echo 'No office SIP line is registered.' >&2
  exit 1
fi
