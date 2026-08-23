#!/usr/bin/env bash
set -euo pipefail

output="$(asterisk -rx 'core show uptime seconds' 2>&1)"
grep -qi 'System uptime' <<< "${output}"
