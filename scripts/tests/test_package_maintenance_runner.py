from __future__ import annotations

import gzip
import hashlib
import importlib.util
import io
import pathlib
import tarfile
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / "package_maintenance_runner.py"
spec = importlib.util.spec_from_file_location("package_maintenance_runner", SCRIPT)
package_runner = importlib.util.module_from_spec(spec)
assert spec.loader is not None
spec.loader.exec_module(package_runner)


class PackageMaintenanceRunnerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.publish = self.root / "publish"
        self.publish.mkdir()
        (self.publish / "Slipmat.MaintenanceRunner").write_bytes(b"apphost")
        (self.publish / "Slipmat.MaintenanceRunner").chmod(0o755)
        (self.publish / "Slipmat.MaintenanceRunner.dll").write_bytes(b"assembly")
        (self.publish / "Slipmat.MaintenanceRunner.deps.json").write_text(
            '{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0/linux-x64"}}'
        )
        (self.publish / "Slipmat.MaintenanceRunner.runtimeconfig.json").write_text("{}")

    def read_archive(self, path: pathlib.Path) -> dict[str, tuple[bytes, int, int]]:
        with gzip.open(path, "rb") as stream, tarfile.open(fileobj=stream, mode="r:") as archive:
            return {
                item.name: (archive.extractfile(item).read(), item.mode, item.mtime)
                for item in archive.getmembers()
                if item.isfile()
            }

    def test_archive_has_fixed_modes_hashes_and_timestamp(self) -> None:
        first = self.root / "first.tar.gz"
        second = self.root / "second.tar.gz"
        package_runner.package(self.publish, first, "0.5.2.12", "linux-x64")
        package_runner.package(self.publish, second, "0.5.2.12", "linux-x64")

        self.assertEqual(first.read_bytes(), second.read_bytes())
        entries = self.read_archive(first)
        apphost = entries["slipmat-maintenance/Slipmat.MaintenanceRunner"]
        self.assertEqual(apphost[1:], (0o755, package_runner.source_date_epoch()))
        self.assertEqual(entries["slipmat-maintenance/custody.py"][1], 0o755)
        self.assertEqual(entries["slipmat-maintenance/host_guard.py"][1], 0o755)
        sums = entries["slipmat-maintenance/SHA256SUMS"][0].decode("ascii")
        for name, (content, _, _) in entries.items():
            if name.endswith("/SHA256SUMS"):
                continue
            relative_name = name.removeprefix("slipmat-maintenance/")
            self.assertIn(f"{hashlib.sha256(content).hexdigest()}  {relative_name}\n", sums)

    def test_rejects_invalid_version_and_missing_publish_files(self) -> None:
        with self.assertRaisesRegex(ValueError, "four-part"):
            package_runner.package(self.publish, self.root / "bad.tar.gz", "latest", "linux-x64")
        (self.publish / "Slipmat.MaintenanceRunner.runtimeconfig.json").unlink()
        with self.assertRaisesRegex(ValueError, "required regular file missing"):
            package_runner.package(self.publish, self.root / "missing.tar.gz", "0.5.2.12", "linux-x64")

    def test_rejects_a_runtime_packaged_for_another_architecture(self) -> None:
        with self.assertRaisesRegex(ValueError, "does not match requested RID"):
            package_runner.package(self.publish, self.root / "wrong-rid.tar.gz", "0.5.2.12", "linux-arm64")


if __name__ == "__main__":
    unittest.main()
