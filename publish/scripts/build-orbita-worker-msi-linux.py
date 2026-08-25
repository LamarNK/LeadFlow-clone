#!/usr/bin/env python3
"""Generate WiX v3 source consumable by wixl on Linux.

WiX 5 uses a Windows-native backend. The generated source intentionally stays
within the WiX v3 subset that wixl supports, while preserving the published
directory hierarchy inside the MSI.
"""

from __future__ import annotations

import argparse
import hashlib
import re
import uuid
from pathlib import Path
from xml.sax.saxutils import escape


UPGRADE_CODE = "a7c4e2f1-9b3d-4a6e-8c0f-1d2e3f4a5b6c"
WORKER_LAUNCH_ARGUMENT = "--update-restart"
XML_QUOT = "&" + "quot;"
XML_GT = "&" + "gt;"
XML_AMP = "&" + "amp;"
MSI_VERSION_PATTERN = re.compile(r"^(\d+)\.(\d+)\.(\d+)\.(\d+)$")
MSI_VERSION_FIELD_MAX = 65_535

# Explicit numbers pin wixl's topological sort. MajorUpgrade only adds a
# dependency on InstallValidate (1400); without a number, RemoveExistingProducts
# can land at the MSI default 6700 — after InstallFinalize and after launch.
INSTALL_VALIDATE_SEQUENCE = 1400
STOP_WORKER_SEQUENCE = 1448
INSTALL_INITIALIZE_SEQUENCE = 1500
# RemoveExistingProducts must be *inside* the MSI transaction.  Putting it
# before InstallInitialize uninstalls the old worker without rollback; one
# failed copy then leaves the client with neither version.  Immediately after
# InstallInitialize still clears the old files before InstallFiles.
REMOVE_EXISTING_PRODUCTS_SEQUENCE = INSTALL_INITIALIZE_SEQUENCE + 1
INSTALL_FINALIZE_SEQUENCE = 6600
SET_LAUNCH_WORKER_SEQUENCE = 6601
LAUNCH_WORKER_SEQUENCE = 6602

# Type 50 (EXE from property) + Continue (Return=ignore).
LAUNCH_CUSTOM_ACTION_TYPE_IGNORE = 114
# Type 51 (set property).
SET_PROPERTY_ACTION_TYPE = 51
# Type 18 (EXE from File table) + asyncNoWait — the upgrade-breaking form.
FILEKEY_LAUNCH_TYPE = 210


def xml(value: str) -> str:
    return escape(value, {'"': XML_QUOT})


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


def msi_product_version(version: str) -> str:
    """Map the four-part app version to a strictly increasing MSI version.

    Windows Installer compares only the first three ProductVersion fields.
    Publishing 1.0.1.96 followed by 1.0.1.97 therefore used to create a
    *same-version* major upgrade: it could remove the old product while
    refusing to install its replacement.  Keep the public app/FileVersion
    intact, but encode its build and revision into MSI's third field.
    """
    match = MSI_VERSION_PATTERN.fullmatch(version)
    if match is None:
        raise ValueError(f"Worker version must have four numeric fields: {version}")

    major, minor, build, revision = (int(part) for part in match.groups())
    if major > 255 or minor > 255:
        raise ValueError(f"MSI major/minor version fields must be at most 255: {version}")
    if revision >= 10_000:
        raise ValueError(f"Worker revision must be below 10000 for MSI version encoding: {version}")

    encoded_build = build * 10_000 + revision
    if encoded_build > MSI_VERSION_FIELD_MAX:
        raise ValueError(f"Worker build/revision is too large for MSI ProductVersion: {version}")
    return f"{major}.{minor}.{encoded_build}"


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


def custom_action_lines(worker_executable_id: str) -> list[str]:
    """Custom actions that survive a silent major upgrade under wixl.

    FileKey / Type 18 is the upgrade bug: Windows Installer binds that CA to
    the File table component action. On a first install the component action
    is InstallLocal, so CreateProcess works. On a major upgrade the nested
    RemoveExistingProducts session plus MigrateFeatureStates leaves the new
    File row in a state where Type 18 is skipped or resolves a stale path.
    The launched process is also a msiexec child, so ``msiexec /qn`` can kill
    it when the installer job object goes away.

    Type 50 + ``cmd /c start`` breaks away from msiexec.  Do not use a
    ``[#FileId]`` reference here: Windows Installer can reject it after the
    transaction with Error 2753 ("file is not marked for installation"),
    even though InstallFiles succeeded.  ``INSTALLFOLDER`` is resolved in the
    running package and remains valid after the early major-upgrade removal.
    ``--update-restart`` makes the worker retry the single-instance mutex
    instead of exiting on the first attempt.
    """
    stop_cmd = (
        f"/d /c taskkill /F /IM Orbita.Worker.exe {XML_GT}nul 2{XML_GT}{XML_AMP}1 "
        f"{XML_AMP} ping -n 3 127.0.0.1 {XML_GT}nul"
    )
    launch_cmd = (
        f"/d /c start {XML_QUOT}{XML_QUOT} {XML_QUOT}[INSTALLFOLDER]Orbita.Worker.exe{XML_QUOT} "
        f"{WORKER_LAUNCH_ARGUMENT}"
    )
    condition = f"NOT REMOVE~={XML_QUOT}ALL{XML_QUOT}"
    return [
        '    <CustomAction Id="SetStopWorkerCommand" Property="StopWorkerCommand"',
        '                  Value="[SystemFolder]cmd.exe" Execute="immediate" />',
        '    <CustomAction Id="StopWorkerBeforeUpgrade" Property="StopWorkerCommand"',
        f'                  ExeCommand="{stop_cmd}"',
        '                  Execute="immediate" Return="ignore" Impersonate="yes" />',
        '    <CustomAction Id="SetLaunchWorkerCommand" Property="LaunchWorkerCommand"',
        '                  Value="[SystemFolder]cmd.exe" Execute="immediate" />',
        '    <CustomAction Id="LaunchWorkerAfterInstall" Property="LaunchWorkerCommand"',
        f'                  ExeCommand="{launch_cmd}"',
        '                  Execute="immediate" Return="ignore" Impersonate="yes" />',
        '    <InstallExecuteSequence>',
        f'      <RemoveExistingProducts Sequence="{REMOVE_EXISTING_PRODUCTS_SEQUENCE}" After="InstallInitialize" />',
        f'      <Custom Action="SetStopWorkerCommand" Sequence="{STOP_WORKER_SEQUENCE - 1}" Before="StopWorkerBeforeUpgrade">',
        f'        {condition}',
        '      </Custom>',
        f'      <Custom Action="StopWorkerBeforeUpgrade" Sequence="{STOP_WORKER_SEQUENCE}" Before="RemoveExistingProducts">',
        f'        {condition}',
        '      </Custom>',
        f'      <Custom Action="SetLaunchWorkerCommand" Sequence="{SET_LAUNCH_WORKER_SEQUENCE}" After="InstallFinalize">',
        f'        {condition}',
        '      </Custom>',
        f'      <Custom Action="LaunchWorkerAfterInstall" Sequence="{LAUNCH_WORKER_SEQUENCE}" After="SetLaunchWorkerCommand">',
        f'        {condition}',
        '      </Custom>',
        '    </InstallExecuteSequence>',
    ]


def assert_wxs_upgrade_contract(text: str, worker_executable_id: str) -> None:
    """Fail the build if the generated source regresses the upgrade launch."""
    errors: list[str] = []
    if 'Guid="*"' in text:
        errors.append("component GUIDs must be unique per release, not Guid=\"*\"")
    if "FileKey=" in text:
        errors.append("LaunchWorkerAfterInstall must not use FileKey/Type 18")
    if 'Property="LaunchWorkerCommand"' not in text:
        errors.append("LaunchWorkerAfterInstall must be a Type 50 property CA")
    if WORKER_LAUNCH_ARGUMENT not in text:
        errors.append(f"launch command must pass {WORKER_LAUNCH_ARGUMENT}")
    if f"start {XML_QUOT}{XML_QUOT}" not in text:
        errors.append("launch command must use cmd start to detach from msiexec")
    if "[#" in text:
        errors.append("launch command must not reference a File table row after InstallFinalize")
    if "[INSTALLFOLDER]Orbita.Worker.exe" not in text:
        errors.append("launch command must resolve Orbita.Worker.exe from INSTALLFOLDER")
    if "taskkill /F /IM Orbita.Worker.exe" not in text:
        errors.append("StopWorkerBeforeUpgrade must force-kill the running worker")
    if "taskkill /IM Orbita.Worker.exe /T" in text:
        errors.append("StopWorkerBeforeUpgrade must not use /T (it can kill msiexec)")
    if f'Sequence="{REMOVE_EXISTING_PRODUCTS_SEQUENCE}"' not in text:
        errors.append("RemoveExistingProducts must be pinned immediately after InstallInitialize")
    if f'Sequence="{LAUNCH_WORKER_SEQUENCE}"' not in text:
        errors.append("LaunchWorkerAfterInstall must be pinned after InstallFinalize")
    if f"NOT REMOVE~={XML_QUOT}ALL{XML_QUOT}" not in text:
        errors.append("launch/stop conditions must run on install and major upgrade")
    if errors:
        raise RuntimeError("Generated WiX source fails the MSI upgrade contract: " + "; ".join(errors))


def _tab_rows(export_text: str) -> list[list[str]]:
    rows: list[list[str]] = []
    for raw in export_text.splitlines():
        line = raw.rstrip("\r")
        if not line:
            continue
        rows.append(line.split("\t"))
    return rows


def _sequence_map(sequence_table: str) -> dict[str, tuple[str, int]]:
    values: dict[str, tuple[str, int]] = {}
    for fields in _tab_rows(sequence_table):
        if len(fields) < 3:
            continue
        action, condition, sequence = fields[0], fields[1], fields[2]
        try:
            values[action] = (condition, int(sequence))
        except ValueError:
            continue
    return values


def _custom_action_map(custom_action_table: str) -> dict[str, tuple[int, str, str]]:
    values: dict[str, tuple[int, str, str]] = {}
    for fields in _tab_rows(custom_action_table):
        if len(fields) < 4:
            continue
        action, type_text, source, target = fields[0], fields[1], fields[2], fields[3]
        try:
            values[action] = (int(type_text), source, target)
        except ValueError:
            continue
    return values


def validate_msi_tables(msi_tables: str, sequence_table: str, custom_action_table: str) -> None:
    """Validate the compiled MSI tables that actually run on Windows."""
    errors: list[str] = []
    table_names = {line.strip() for line in msi_tables.splitlines() if line.strip()}
    if "File" not in table_names:
        errors.append("MSI is missing the File table")
    if "Upgrade" not in table_names:
        errors.append("MSI is missing the Upgrade table (MajorUpgrade was dropped by wixl)")

    sequence = _sequence_map(sequence_table)
    actions = _custom_action_map(custom_action_table)

    def seq(name: str) -> int | None:
        item = sequence.get(name)
        return item[1] if item else None

    def cond(name: str) -> str:
        item = sequence.get(name)
        return item[0] if item else ""

    remove_existing = seq("RemoveExistingProducts")
    install_files = seq("InstallFiles")
    install_initialize = seq("InstallInitialize")
    install_finalize = seq("InstallFinalize")
    stop_worker = seq("StopWorkerBeforeUpgrade")
    set_launch = seq("SetLaunchWorkerCommand")
    launch_worker = seq("LaunchWorkerAfterInstall")

    if remove_existing is None or install_files is None or remove_existing >= install_files:
        errors.append("RemoveExistingProducts must run before InstallFiles")
    if (
        install_initialize is None
        or remove_existing is None
        or remove_existing != install_initialize + 1
    ):
        errors.append(
            "RemoveExistingProducts must run immediately after InstallInitialize "
            "inside the transaction so a failed upgrade rolls back the old worker"
        )
    if stop_worker is None or remove_existing is None or stop_worker >= remove_existing:
        errors.append("StopWorkerBeforeUpgrade must run before RemoveExistingProducts")
    if launch_worker is None or install_finalize is None or launch_worker <= install_finalize:
        errors.append("LaunchWorkerAfterInstall must run after InstallFinalize")
    if set_launch is None or install_finalize is None or set_launch <= install_finalize:
        errors.append("SetLaunchWorkerCommand must run after InstallFinalize")
    if set_launch is not None and launch_worker is not None and set_launch >= launch_worker:
        errors.append("SetLaunchWorkerCommand must run before LaunchWorkerAfterInstall")

    if "NOT REMOVE~=\"ALL\"" not in cond("LaunchWorkerAfterInstall"):
        errors.append("LaunchWorkerAfterInstall must run on first install and major upgrade, but not uninstall")

    stop = actions.get("StopWorkerBeforeUpgrade")
    set_stop = actions.get("SetStopWorkerCommand")
    launch = actions.get("LaunchWorkerAfterInstall")
    set_launch_ca = actions.get("SetLaunchWorkerCommand")

    if set_stop is None or set_stop[0] != SET_PROPERTY_ACTION_TYPE or set_stop[2] != "[SystemFolder]cmd.exe":
        errors.append("SetStopWorkerCommand must resolve [SystemFolder]cmd.exe at execute time")
    if stop is None or "/F" not in stop[2] or "taskkill" not in stop[2]:
        errors.append("StopWorkerBeforeUpgrade must force-kill Orbita.Worker.exe")
    if stop is not None and re.search(r"/T\b", stop[2]):
        errors.append("StopWorkerBeforeUpgrade must not use taskkill /T")
    if set_launch_ca is None or set_launch_ca[0] != SET_PROPERTY_ACTION_TYPE or set_launch_ca[2] != "[SystemFolder]cmd.exe":
        errors.append("SetLaunchWorkerCommand must resolve [SystemFolder]cmd.exe at execute time")
    if launch is None:
        errors.append("LaunchWorkerAfterInstall custom action is missing")
    else:
        type_code, source, target = launch
        if type_code == FILEKEY_LAUNCH_TYPE or source.startswith("File_"):
            errors.append("LaunchWorkerAfterInstall must not be Type 18 FileKey (skipped on major upgrade)")
        if type_code % 64 != 50 or source != "LaunchWorkerCommand":
            errors.append("LaunchWorkerAfterInstall must be Type 50 (property EXE), not FileKey")
        if "start" not in target or WORKER_LAUNCH_ARGUMENT not in target:
            errors.append("LaunchWorkerAfterInstall must cmd-start the worker with --update-restart")
        if "[#" in target:
            errors.append("LaunchWorkerAfterInstall must not use [#FileId] (it causes Error 2753 after InstallFinalize)")
        if "[INSTALLFOLDER]Orbita.Worker.exe" not in target:
            errors.append("LaunchWorkerAfterInstall must use [INSTALLFOLDER]Orbita.Worker.exe")

    if errors:
        raise RuntimeError("Compiled MSI fails the upgrade contract: " + "; ".join(errors))


def generate(publish_dir: Path, output: Path, version: str) -> None:
    files = sorted(path for path in publish_dir.rglob("*") if path.is_file())
    if not files:
        raise RuntimeError(f"No files found in {publish_dir}")

    tree: dict[str, dict] = {}
    for file in files:
        relative = file.relative_to(publish_dir)
        if relative.parent != Path("."):
            add_to_tree(tree, relative.parent)

    product_version = msi_product_version(version)
    lines = [
        '<?xml version="1.0" encoding="UTF-8"?>',
        '<Wix xmlns="http://schemas.microsoft.com/wix/2006/wi">',
        '  <Product Id="*" Name="Orbita Worker" Language="1049"',
        f'           Version="{xml(product_version)}" Manufacturer="Orbita" UpgradeCode="{UPGRADE_CODE}">',
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

    autorun_value = f"{XML_QUOT}[INSTALLFOLDER]Orbita.Worker.exe{XML_QUOT}"
    lines.extend([
        '    <DirectoryRef Id="INSTALLFOLDER">',
        f'      <Component Id="AutoStartComponent" Guid="{component_guid(version, "auto-start")}">',
        '        <RegistryValue Root="HKCU" Key="Software\\Microsoft\\Windows\\CurrentVersion\\Run"',
        f'                       Name="OrbitaWorker" Type="string"',
        f'                       Value="{autorun_value}" KeyPath="yes" />',
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
    ])
    lines.extend(custom_action_lines(worker_executable_id))
    lines.extend([
        '  </Product>',
        '</Wix>',
        '',
    ])

    text = "\n".join(lines)
    assert_wxs_upgrade_contract(text, worker_executable_id)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(text, encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish-dir", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    generate(args.publish_dir.resolve(), args.output.resolve(), args.version)


if __name__ == "__main__":
    main()
