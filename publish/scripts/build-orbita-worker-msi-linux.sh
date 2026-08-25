#!/usr/bin/env bash
set -euo pipefail

usage() {
  echo "Usage: $0 --version <major.minor.build.revision> [--output-dir <path>]" >&2
  exit 64
}

version=""
output_dir=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version)
      version="${2:-}"
      shift 2
      ;;
    --output-dir)
      output_dir="${2:-}"
      shift 2
      ;;
    *)
      usage
      ;;
  esac
done

[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || usage
command -v dotnet >/dev/null || { echo "dotnet SDK is required." >&2; exit 1; }
command -v python3 >/dev/null || { echo "python3 is required." >&2; exit 1; }
command -v wixl >/dev/null || { echo "wixl is required (Ubuntu: apt-get install wixl)." >&2; exit 1; }
command -v msiinfo >/dev/null || { echo "msiinfo is required (Ubuntu: apt-get install msitools)." >&2; exit 1; }
command -v msibuild >/dev/null || { echo "msibuild is required (Ubuntu: apt-get install msitools)." >&2; exit 1; }
command -v wrestool >/dev/null || { echo "wrestool is required (Ubuntu: apt-get install icoutils)." >&2; exit 1; }

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
python3 "$script_dir/test_build_orbita_worker_msi_linux.py"

repo_root="$(cd -- "$script_dir/../.." && pwd)"
output_dir="${output_dir:-$repo_root/publish/out/orbita-worker/$version}"
publish_dir="$repo_root/publish/tmp/orbita-worker-publish-linux/$version"
wxs_path="$repo_root/publish/tmp/orbita-worker-wixl/$version/Package.wxs"
msi_path="$output_dir/Orbita.Worker.Setup-$version.msi"
manifest_path="$output_dir/orbita-build.json"

rm -rf -- "$publish_dir" "$(dirname -- "$wxs_path")"
mkdir -p -- "$publish_dir" "$(dirname -- "$wxs_path")" "$output_dir"

dotnet publish "$repo_root/Orbita.Worker/Orbita.Worker.csproj" \
  -c Release \
  -r win-x64 \
  --self-contained true \
  -p:EnableWindowsTargeting=true \
  -p:Version="$version" \
  -p:FileVersion="$version" \
  -p:InformationalVersion="$version" \
  -p:IncludeSourceRevisionInInformationalVersion=false \
  -t:Rebuild \
  -o "$publish_dir"

find "$publish_dir" -type f -name '*.pdb' -delete
exe_version="$(wrestool -x --raw -t 16 "$publish_dir/Orbita.Worker.exe" | strings -el | awk 'previous == "FileVersion" && !printed { print; printed = 1 } { previous = $0 }')"
if [[ "$exe_version" != "$version" ]]; then
  echo "Orbita.Worker.exe FileVersion is '$exe_version', expected '$version'." >&2
  exit 1
fi
python3 "$script_dir/build-orbita-worker-msi-linux.py" \
  --publish-dir "$publish_dir" \
  --output "$wxs_path" \
  --version "$version"
if grep -Fq 'Guid="*"' "$wxs_path"; then
  echo "Generated WiX source reuses automatic component GUIDs across releases." >&2
  exit 1
fi
wixl -a x64 -o "$msi_path" "$wxs_path"

# wixl does not implement WiX's File/@Version and therefore writes every
# payload row with a blank Version column. During a major upgrade Windows
# Installer then skips the new EXE as "already present" and the removal of
# the prior package deletes it. Stamp the generated MSI table directly; the
# compiled-MSI contract below verifies this before any artifact is published.
printf -v msi_file_version_query "UPDATE \`File\` SET \`Version\` = '%s'" "$version"
msibuild "$msi_path" -q "$msi_file_version_query"

if ! python3 "$script_dir/test_build_orbita_worker_msi_linux.py" --msi "$msi_path"; then
  echo "---- InstallExecuteSequence ----" >&2
  msiinfo export "$msi_path" InstallExecuteSequence >&2 || true
  echo "---- CustomAction ----" >&2
  msiinfo export "$msi_path" CustomAction >&2 || true
  exit 1
fi

sha256="$(sha256sum "$msi_path" | awk '{print $1}')"
python3 - "$manifest_path" "$version" "$msi_path" "$sha256" <<'PY'
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

path, version, package, sha256 = sys.argv[1:]
package_path = Path(package)
Path(path).write_text(json.dumps({
    "target": "orbita-worker",
    "version": version,
    "configuration": "Release",
    "runtime": "win-x64",
    "sourceProject": "Orbita.Worker/Orbita.Worker.csproj",
    "packageFile": package_path.name,
    "verifiedPackagePath": str(package_path.resolve()),
    "fileSize": package_path.stat().st_size,
    "sha256": sha256,
    "builtAtUtc": datetime.now(timezone.utc).isoformat(),
}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
PY

echo "Built $msi_path"
echo "SHA256: $sha256"
