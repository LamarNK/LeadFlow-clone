#!/usr/bin/env python3
"""Generate WiX v3 source consumable by wixl on Linux.

WiX 5 uses a Windows-native backend. The generated source intentionally stays
within the WiX v3 subset that wixl supports, while preserving the published
directory hierarchy inside the MSI.
"""

from __future__ import annotations

import argparse
import hashlib
import uuid
from pathlib import Path
from xml.sax.saxutils import escape


UPGRADE_CODE = "a7c4e2f1-9b3d-4a6e-8c0f-1d2e3f4a5b6c"


def xml(value: str) -> str:
    return escape(value, {'"': '&quot;'})


def wix_id(prefix: str, value: str) -> str:
    digest = hashlib.sha256(value.encode("utf-8")).hexdigest()[:16]
    return f"{prefix}_{digest}"


def component_guid(version: str, identity: str) -> str:
    """Return a stable component GUID that changes for every release.

    A major upgrade built by wixl must not reuse component codes from the
    package it removes. Otherwise MSI can decide a file is already present,
    then RemoveExistingProducts deletes that very file from the old package.
    """
    return str(uuid.uuid5(uuid.NAMESPACE_URL, f"orbita-worker/{version}/{identity}")).upper()


def directory_tree(parent: str, children: dict[str, dict]) -> list[str]:
    lines: list[str] = []
    for name in sorted(children):
        child = children[name]
        relative = child["path"]
        directory_id = wix_id("Directory", relative)
        lines.append(f'          <Directory Id="{directory_id}" Name="{xml(name)}">')
        lines.extend(directory_tree(relative, child["children"]))
        lines.append("          </Directory>")
    return lines


def add_to_tree(tree: dict[str, dict], path: Path) -> None:
    current = tree
    segments: list[str] = []
    for segment in path.parts:
        segments.append(segment)
        if segment not in current:
            current[segment] = {"path": "/".join(segments), "children": {}}
        current = current[segment]["children"]


def directory_id(relative_parent: Path) -> str:
    return "INSTALLFOLDER" if relative_parent == Path(".") else wix_id("Directory", relative_parent.as_posix())


def generate(publish_dir: Path, output: Path, version: str) -> None:
    files = sorted(path for path in publish_dir.rglob("*") if path.is_file())
    if not files:
        raise RuntimeError(f"No files found in {publish_dir}")

    tree: dict[str, dict] = {}
    for file in files:
        relative = file.relative_to(publish_dir)
        if relative.parent != Path("."):
            add_to_tree(tree, relative.parent)

    lines = [
        '<?xml version="1.0" encoding="UTF-8"?>',
        '<Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">',
        '  <Product Id="*" Name="Orbita Worker" Language="1049"',
        f'           Version="{xml(version)}" Manufacturer="Orbita" UpgradeCode="{UPGRADE_CODE}">',
        '    <Package InstallerVersion="200" Compressed="yes" InstallScope="perUser" />',
        '    <MajorUpgrade AllowSameVersionUpgrades="yes"',
        '                  DowngradeErrorMessage="A newer version of Orbita Worker is already installed." />',
        '    <MediaTemplate EmbedCab="yes" />',
        '    <Directory Id="TARGETDIR" Name="SourceDir">',
        '      <Directory Id="LocalAppDataFolder">',
        '        <Directory Id="CompanyFolder" Name="Orbita">',
        '          <Directory Id="INSTALLFOLDER" Name="Worker">',
    ]
    lines.extend(directory_tree("", tree))
    lines.extend([
        '          </Directory>',
        '        </Directory>',
        '      </Directory>',
        '    </Directory>',
    ])

    component_ids: list[str] = []
    worker_executable_id: str | None = None
    for index, file in enumerate(files, start=1):
        relative = file.relative_to(publish_dir)
        component_id = f"FileComponent_{index}"
        file_id = f"File_{index}"
        component_ids.append(component_id)
        if relative.as_posix().lower() == "orbita.worker.exe":
            worker_executable_id = file_id
        parent_id = directory_id(relative.parent)
        lines.extend([
            f'    <DirectoryRef Id="{parent_id}">',
            f'      <Component Id="{component_id}" Guid="{component_guid(version, relative.as_posix())}">',
            f'        <File Id="{file_id}" Source="{xml(str(file.resolve()))}" KeyPath="yes" />',
            '      </Component>',
            '    </DirectoryRef>',
        ])

    if worker_executable_id is None:
        raise RuntimeError("Orbita.Worker.exe is missing from the publish directory")

    lines.extend([
        '    <DirectoryRef Id="INSTALLFOLDER">',
        f'      <Component Id="AutoStartComponent" Guid="{component_guid(version, "auto-start")}">',
        '        <RegistryValue Root="HKCU" Key="Software\\Microsoft\\Windows\\CurrentVersion\\Run"',
        '                       Name="OrbitaWorker" Type="string"',
        '                       Value="&quot;[INSTALLFOLDER]Orbita.Worker.exe&quot;" KeyPath="yes" />',
        '      </Component>',
        f'      <Component Id="CleanupInstallFolder" Guid="{component_guid(version, "cleanup-install-folder")}">',
        '        <RemoveFolder Id="RemoveInstallFolder" On="uninstall" />',
        '        <RegistryValue Root="HKCU" Key="Software\\Orbita\\Worker" Name="InstallFolder"',
        '                       Type="string" Value="1" KeyPath="yes" />',
        '      </Component>',
        '    </DirectoryRef>',
        '    <DirectoryRef Id="CompanyFolder">',
        f'      <Component Id="CleanupCompanyFolder" Guid="{component_guid(version, "cleanup-company-folder")}">',
        '        <RemoveFolder Id="RemoveCompanyFolder" On="uninstall" />',
        '        <RegistryValue Root="HKCU" Key="Software\\Orbita" Name="WorkerRoot"',
        '                       Type="string" Value="1" KeyPath="yes" />',
        '      </Component>',
        '    </DirectoryRef>',
        '    <Feature Id="MainFeature" Title="Orbita Worker" Level="1">',
    ])
    lines.extend(f'      <ComponentRef Id="{component_id}" />' for component_id in component_ids)
    lines.extend([
        '      <ComponentRef Id="AutoStartComponent" />',
        '      <ComponentRef Id="CleanupInstallFolder" />',
        '      <ComponentRef Id="CleanupCompanyFolder" />',
        '    </Feature>',
        '    <CustomAction Id="SetStopWorkerCommand" Property="StopWorkerCommand"',
        '                  Value="[SystemFolder]cmd.exe" Execute="immediate" />',
        '    <CustomAction Id="StopWorkerBeforeUpgrade" Property="StopWorkerCommand"',
        '                  ExeCommand="/d /c taskkill /IM Orbita.Worker.exe /T &gt;nul 2&gt;&amp;1"',
        '                  Execute="immediate" Return="ignore" Impersonate="yes" />',
        f'    <CustomAction Id="LaunchWorkerAfterInstall" FileKey="{worker_executable_id}"',
        '                  ExeCommand="" Execute="immediate"',
        '                  Return="asyncNoWait" Impersonate="yes" />',
        '    <InstallExecuteSequence>',
        '      <RemoveExistingProducts Before="InstallInitialize" />',
        '      <Custom Action="SetStopWorkerCommand" Before="StopWorkerBeforeUpgrade">',
        '        NOT REMOVE~=&quot;ALL&quot;',
        '      </Custom>',
        '      <Custom Action="StopWorkerBeforeUpgrade" Before="RemoveExistingProducts">',
        '        NOT REMOVE~=&quot;ALL&quot;',
        '      </Custom>',
        '      <Custom Action="LaunchWorkerAfterInstall" After="InstallFinalize">',
        '        NOT REMOVE~=&quot;ALL&quot;',
        '      </Custom>',
        '    </InstallExecuteSequence>',
        '  </Product>',
        '</Wix>',
        '',
    ])

    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text("\n".join(lines), encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish-dir", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    generate(args.publish_dir.resolve(), args.output.resolve(), args.version)


if __name__ == "__main__":
    main()
