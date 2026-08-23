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
command -v wrestool >/dev/null || { echo "wrestool is required (Ubuntu: apt-get install icoutils)." >&2; exit 1; }

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
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
exe_version="$(wrestool -x --raw -t 16 "$publish_dir/Orbita.Worker.exe" | strings -el | awk 'previous == "FileVersion" { print; exit } { previous = $0 }')"
if [[ "$exe_version" != "$version" ]]; then
  echo "Orbita.Worker.exe FileVersion is '$exe_version', expected '$version'." >&2
  exit 1
fi
python3 "$script_dir/build-orbita-worker-msi-linux.py" \
  --publish-dir "$publish_dir" \
  --output "$wxs_path" \
  --version "$version"
wixl -a x64 -o "$msi_path" "$wxs_path"

if ! msiinfo tables "$msi_path" | grep -qx 'File'; then
  echo "wixl did not produce a valid MSI database: $msi_path" >&2
  exit 1
fi
if ! msiinfo export "$msi_path" CustomAction | grep -q '^StopWorkerBeforeUpgrade'; then
  echo "MSI does not contain the required StopWorkerBeforeUpgrade action." >&2
  exit 1
fi
if ! msiinfo export "$msi_path" InstallExecuteSequence | grep '^StopWorkerBeforeUpgrade' | grep -Fq 'NOT REMOVE~="ALL"'; then
  echo "MSI does not stop Orbita Worker before replacing its files." >&2
  exit 1
fi
if ! msiinfo export "$msi_path" CustomAction | grep '^LaunchWorkerAfterInstall' | grep -Fq 'start "" /D "[INSTALLFOLDER]"'; then
  echo "MSI does not start Orbita Worker from its installation folder." >&2
  exit 1
fi
if ! msiinfo export "$msi_path" InstallExecuteSequence | grep -q '^LaunchWorkerAfterInstall'; then
  echo "MSI does not schedule LaunchWorkerAfterInstall after installation." >&2
  exit 1
fi
if ! msiinfo export "$msi_path" InstallExecuteSequence | grep '^LaunchWorkerAfterInstall' | grep -Fq 'NOT REMOVE~="ALL"'; then
  echo "MSI does not launch Orbita Worker after an upgrade." >&2
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
