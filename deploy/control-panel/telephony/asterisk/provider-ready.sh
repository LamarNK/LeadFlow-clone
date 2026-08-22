#!/usr/bin/env bash
set -euo pipefail

output="$(asterisk -rx 'pjsip show registration orbita-provider-registration' 2>&1)"
printf '%s\n' "${output}"

if grep -Eq '(^|[[:space:]])Registered([[:space:]]|$)' <<< "${output}"; then
  exit 0
fi

echo 'Upstream SIP trunk is not registered.' >&2
exit 1
