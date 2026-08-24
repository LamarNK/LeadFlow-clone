#!/usr/bin/env python3
"""Checks for the Linux Orbita Worker MSI upgrade/launch contract."""

from __future__ import annotations

import importlib.util
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


def load_builder():
    path = Path(__file__).with_name("build-orbita-worker-msi-linux.py")
    spec = importlib.util.spec_from_file_location("orbita_worker_msi", path)
    module = importlib.util.module_from_spec(spec)
    assert spec.loader is not None
    spec.loader.exec_module(module)
    return module


builder = load_builder()


GOOD_SEQUENCE = """Action\tCondition\tSequence
s72\tS255\tI2
Action\tInstallExecuteSequence
InstallValidate\t\t1400
SetStopWorkerCommand\tNOT REMOVE~=\"ALL\"\t1447
StopWorkerBeforeUpgrade\tNOT REMOVE~=\"ALL\"\t1448
RemoveExistingProducts\t\t1450
InstallInitialize\t\t1500
InstallFiles\t\t4000
InstallFinalize\t\t6600
SetLaunchWorkerCommand\tNOT REMOVE~=\"ALL\"\t6601
LaunchWorkerAfterInstall\tNOT REMOVE~=\"ALL\"\t6602
"""

GOOD_CUSTOM_ACTION = """Action\tType\tSource\tTarget
s72\ti2\tS64\tS255
Action\tCustomAction
SetStopWorkerCommand\t51\tStopWorkerCommand\t[SystemFolder]cmd.exe
StopWorkerBeforeUpgrade\t114\tStopWorkerCommand\t/d /c taskkill /F /IM Orbita.Worker.exe
SetLaunchWorkerCommand\t51\tLaunchWorkerCommand\t[SystemFolder]cmd.exe
LaunchWorkerAfterInstall\t114\tLaunchWorkerCommand\t/d /c start \"\" \"[#File_1]\" --update-restart
"""

GOOD_TABLES = "File\nUpgrade\nCustomAction\nInstallExecuteSequence\n"


class WorkerMsiContractTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)
        self.publish = self.root / "publish"
        self.publish.mkdir()
        (self.publish / "Orbita.Worker.exe").write_bytes(b"MZ")
        (self.publish / "readme.txt").write_text("ok", encoding="utf-8")
        self.output = self.root / "Package.wxs"

    def tearDown(self) -> None:
        self._tmp.cleanup()

    def test_generated_wxs_launches_with_start_and_update_restart(self) -> None:
        builder.generate(self.publish, self.output, "1.2.3.4")
        text = self.output.read_text(encoding="utf-8")
        self.assertIn('Property="LaunchWorkerCommand"', text)
        self.assertNotIn("FileKey=", text)
        self.assertIn("--update-restart", text)
        self.assertIn("[#File_", text)
        self.assertIn("taskkill /F /IM Orbita.Worker.exe", text)
        self.assertNotIn("taskkill /IM Orbita.Worker.exe /T", text)
        self.assertIn('Sequence="1450"', text)
        self.assertIn('Sequence="6602"', text)
        self.assertIn("start " + builder.XML_QUOT + builder.XML_QUOT, text)
        self.assertNotIn('Guid="*"', text)

    def test_component_guids_change_between_releases(self) -> None:
        builder.generate(self.publish, self.output, "1.0.0.1")
        first = self.output.read_text(encoding="utf-8")
        builder.generate(self.publish, self.output, "1.0.0.2")
        second = self.output.read_text(encoding="utf-8")
        self.assertNotEqual(first, second)
        self.assertIn("FileComponent_1", first)
        self.assertIn("FileComponent_1", second)

    def test_wxs_contract_rejects_filekey_launch(self) -> None:
        with self.assertRaises(RuntimeError):
            builder.assert_wxs_upgrade_contract(
                '<CustomAction Id="LaunchWorkerAfterInstall" FileKey="File_1" />',
                "File_1",
            )

    def test_compiled_tables_accept_type50_start_launch(self) -> None:
        builder.validate_msi_tables(GOOD_TABLES, GOOD_SEQUENCE, GOOD_CUSTOM_ACTION)

    def test_compiled_tables_reject_type18_filekey(self) -> None:
        bad_ca = GOOD_CUSTOM_ACTION.replace(
            "LaunchWorkerAfterInstall\t114\tLaunchWorkerCommand\t/d /c start \"\" \"[#File_1]\" --update-restart",
            "LaunchWorkerAfterInstall\t210\tFile_1\t",
        )
        with self.assertRaises(RuntimeError) as raised:
            builder.validate_msi_tables(GOOD_TABLES, GOOD_SEQUENCE, bad_ca)
        self.assertIn("Type 18", str(raised.exception))

    def test_compiled_tables_reject_late_remove_existing_products(self) -> None:
        late = GOOD_SEQUENCE.replace("RemoveExistingProducts\t\t1450", "RemoveExistingProducts\t\t6700")
        with self.assertRaises(RuntimeError) as raised:
            builder.validate_msi_tables(GOOD_TABLES, late, GOOD_CUSTOM_ACTION)
        self.assertIn("RemoveExistingProducts", str(raised.exception))

    def test_compiled_tables_reject_launch_before_installfinalize(self) -> None:
        early = GOOD_SEQUENCE.replace(
            "LaunchWorkerAfterInstall\tNOT REMOVE~=\"ALL\"\t6602",
            "LaunchWorkerAfterInstall\tNOT REMOVE~=\"ALL\"\t3999",
        )
        with self.assertRaises(RuntimeError):
            builder.validate_msi_tables(GOOD_TABLES, early, GOOD_CUSTOM_ACTION)

    def test_compiled_tables_require_upgrade_table(self) -> None:
        with self.assertRaises(RuntimeError) as raised:
            builder.validate_msi_tables("File\nCustomAction\n", GOOD_SEQUENCE, GOOD_CUSTOM_ACTION)
        self.assertIn("Upgrade", str(raised.exception))


def validate_compiled_msi(msi_path: Path) -> None:
    def export(table: str) -> str:
        return subprocess.check_output(["msiinfo", "export", str(msi_path), table], text=True)

    tables = subprocess.check_output(["msiinfo", "tables", str(msi_path)], text=True)
    builder.validate_msi_tables(
        tables,
        export("InstallExecuteSequence"),
        export("CustomAction"),
    )


def main() -> int:
    if len(sys.argv) >= 3 and sys.argv[1] == "--msi":
        validate_compiled_msi(Path(sys.argv[2]))
        print(f"MSI upgrade contract OK: {sys.argv[2]}")
        return 0
    suite = unittest.defaultTestLoader.loadTestsFromTestCase(WorkerMsiContractTests)
    result = unittest.TextTestRunner(verbosity=2).run(suite)
    return 0 if result.wasSuccessful() else 1


if __name__ == "__main__":
    raise SystemExit(main())
