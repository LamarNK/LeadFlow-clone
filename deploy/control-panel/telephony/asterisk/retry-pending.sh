#!/usr/bin/env bash
set -u

while true; do
  while IFS= read -r -d '' meta; do
    /opt/orbita-asterisk/deliver-recording.sh "$meta" || true
  done < <(find /var/spool/asterisk/recording -maxdepth 1 -type f -name '*.wav.meta' -print0)
  sleep 30
done
