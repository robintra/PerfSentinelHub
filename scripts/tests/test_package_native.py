import hashlib
import importlib.util
import os
import stat
import subprocess
import sys
import tarfile
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch


REPOSITORY = Path(__file__).resolve().parents[2]
PACKAGER = REPOSITORY / "scripts" / "package-native.py"
COMMIT_TIME = "1786406400"
VERSION = "0.1.0"
SPEC = importlib.util.spec_from_file_location("package_native", PACKAGER)
packager = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(packager)


class NativePackageTests(unittest.TestCase):
    def test_metadata_fields_drops_what_windows_does_not_carry(self):
        with tempfile.TemporaryDirectory() as directory:
            probe = Path(directory) / "probe.bin"
            probe.write_bytes(b"x")
            metadata = probe.stat()
            with patch.object(packager, "POSIX_STAT", True):
                self.assertEqual(
                    {"type", "size", "mtime_ns", "dev", "ino", "ctime_ns"},
                    set(packager.metadata_fields(metadata)),
                )
            # st_ino is not guaranteed identical between a stat by path and an
            # fstat by handle on Windows, and st_ctime is the creation time
            # there. Comparing either rejects a file nothing has touched.
            with patch.object(packager, "POSIX_STAT", False):
                self.assertEqual(
                    {"type", "size", "mtime_ns"},
                    set(packager.metadata_fields(metadata)),
                )

    def test_a_change_names_the_field_that_moved(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            small, large = root / "a.bin", root / "b.bin"
            small.write_bytes(b"x")
            large.write_bytes(b"xx")
            message = packager.describe_change("a.bin", small.stat(), large.stat())
            # The Windows failure said only which file, never which field, which
            # is what made it a guess rather than a diagnosis.
            self.assertIn("a.bin", message)
            self.assertIn("size: 1 -> 2", message)

    def test_the_windows_field_set_still_catches_a_same_size_mutation(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish = self.publish_tree(root)
            mutable = publish / "mutable.bin"
            mutable.write_bytes(b"AAAA")
            output = root / "dist"
            output.mkdir()

            scan_staging = packager.scan_staging

            def scan_then_mutate(staging):
                entries = scan_staging(staging)
                before = mutable.stat()
                with mutable.open("r+b") as stream:
                    stream.write(b"BBBB")
                    stream.flush()
                    os.fsync(stream.fileno())
                os.utime(mutable, ns=(before.st_atime_ns, before.st_mtime_ns + 1_000_000_000))
                return entries

            # Dropping st_ino and st_ctime_ns must not blunt the check: type,
            # size and st_mtime_ns still reject a rewrite of the same length.
            with (
                patch.object(packager, "POSIX_STAT", False),
                patch.object(packager, "scan_staging", side_effect=scan_then_mutate),
                self.assertRaisesRegex(ValueError, r"changed during packaging.*mtime_ns"),
            ):
                packager.package("osx-arm64", VERSION, int(COMMIT_TIME), publish, output)

    def test_rejects_same_inode_same_size_mutation_before_publication(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish = self.publish_tree(root)
            mutable = publish / "mutable.bin"
            mutable.write_bytes(b"AAAA")
            output = root / "dist"
            output.mkdir()
            old_outputs = {
                output / f"perf-sentinel-hub-{VERSION}-osx-arm64.tar.gz": b"old-runtime",
                output / f"perf-sentinel-hub-{VERSION}-osx-arm64-symbols.tar.gz": b"old-symbols",
                output / "SHA256SUMS": b"old-checksums",
            }
            for path, content in old_outputs.items():
                path.write_bytes(content)

            scan_staging = packager.scan_staging

            def scan_then_mutate(staging):
                entries = scan_staging(staging)
                before = mutable.stat()
                with mutable.open("r+b") as stream:
                    stream.write(b"BBBB")
                    stream.flush()
                    os.fsync(stream.fileno())
                os.utime(
                    mutable,
                    ns=(before.st_atime_ns, before.st_mtime_ns + 1_000_000_000),
                )
                after = mutable.stat()
                self.assertEqual((before.st_dev, before.st_ino, before.st_size), (after.st_dev, after.st_ino, after.st_size))
                return entries

            with (
                patch.object(packager, "scan_staging", side_effect=scan_then_mutate),
                self.assertRaisesRegex(ValueError, "changed during packaging"),
            ):
                packager.package("osx-arm64", VERSION, int(COMMIT_TIME), publish, output)

            for path, content in old_outputs.items():
                self.assertEqual(content, path.read_bytes())

    def test_ignores_publish_file_mtimes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish = self.publish_tree(root)
            first = root / "first"
            first_run = self.run_packager(publish, first)
            self.assertEqual(0, first_run.returncode, first_run.stderr)

            for path in publish.iterdir():
                os.utime(path, (1_600_000_000, 1_700_000_000))
            second = root / "second"
            second_run = self.run_packager(publish, second)
            self.assertEqual(0, second_run.returncode, second_run.stderr)

            self.assertEqual(self.output_hashes(first), self.output_hashes(second))

    def test_tar_archives_separate_symbols_and_normalize_metadata(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            output = root / "dist"
            result = self.run_packager(self.publish_tree(root), output)
            self.assertEqual(0, result.returncode, result.stderr)

            main = output / f"perf-sentinel-hub-{VERSION}-osx-arm64.tar.gz"
            symbols = output / f"perf-sentinel-hub-{VERSION}-osx-arm64-symbols.tar.gz"
            with tarfile.open(main, "r:gz") as archive:
                members = archive.getmembers()
                self.assertEqual(["appsettings.json", "perf-sentinel-hub"], [member.name for member in members])
                self.assertEqual([0o644, 0o755], [member.mode for member in members])
                self.assertEqual([int(COMMIT_TIME)] * 2, [member.mtime for member in members])
                self.assertEqual([(0, 0, "", "")] * 2, [(member.uid, member.gid, member.uname, member.gname) for member in members])
            with tarfile.open(symbols, "r:gz") as archive:
                members = archive.getmembers()
                self.assertEqual(["perf-sentinel-hub.dbg"], [member.name for member in members])
                self.assertEqual([0o644], [member.mode for member in members])

            archive_names = sorted((main.name, symbols.name))
            expected_sums = "".join(
                f"{hashlib.sha256((output / name).read_bytes()).hexdigest()}  {name}\n"
                for name in archive_names
            )
            self.assertEqual(expected_sums, (output / "SHA256SUMS").read_text(encoding="ascii"))

    def test_zip_archives_separate_symbols_and_normalize_modes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish = root / "publish"
            publish.mkdir()
            (publish / "perf-sentinel-hub.exe").write_bytes(b"PE\0\0")
            (publish / "perf-sentinel-hub.pdb").write_bytes(b"symbols")
            output = root / "dist"
            result = self.run_packager(publish, output, rid="win-x64")
            self.assertEqual(0, result.returncode, result.stderr)

            main = output / f"perf-sentinel-hub-{VERSION}-win-x64.zip"
            symbols = output / f"perf-sentinel-hub-{VERSION}-win-x64-symbols.zip"
            with zipfile.ZipFile(main) as archive:
                self.assertEqual(["perf-sentinel-hub.exe"], archive.namelist())
                info = archive.getinfo("perf-sentinel-hub.exe")
                self.assertEqual(0o755, stat.S_IMODE(info.external_attr >> 16))
                self.assertEqual((2026, 8, 11, 0, 0, 0), info.date_time)
            with zipfile.ZipFile(symbols) as archive:
                self.assertEqual(["perf-sentinel-hub.pdb"], archive.namelist())
                info = archive.getinfo("perf-sentinel-hub.pdb")
                self.assertEqual(0o644, stat.S_IMODE(info.external_attr >> 16))

    def test_sorts_nested_archive_entries(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish = root / "publish"
            (publish / "aaa").mkdir(parents=True)
            (publish / "zzz").write_bytes(b"root")
            (publish / "aaa" / "nested").write_bytes(b"nested")
            output = root / "dist"
            result = self.run_packager(publish, output)
            self.assertEqual(0, result.returncode, result.stderr)

            archive_path = output / f"perf-sentinel-hub-{VERSION}-osx-arm64.tar.gz"
            with tarfile.open(archive_path, "r:gz") as archive:
                self.assertEqual(["aaa/nested", "zzz"], archive.getnames())

    def test_rejects_symlinks_inside_publish_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish = self.publish_tree(root)
            outside = root / "outside"
            outside.write_text("secret", encoding="utf-8")
            (publish / "escape").symlink_to(outside)
            result = self.run_packager(publish, root / "dist")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("error:", result.stderr)
            self.assertFalse((root / "dist").exists())

    def test_rejects_symlink_staging_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish = self.publish_tree(root)
            alias = root / "publish-link"
            alias.symlink_to(publish, target_is_directory=True)
            result = self.run_packager(alias, root / "dist")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("error:", result.stderr)

    def test_rejects_archive_name_collisions(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish = self.publish_tree(root)
            (publish / "config").write_bytes(b"one")
            (publish / "config.").write_bytes(b"two")
            result = self.run_packager(publish, root / "dist")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("error:", result.stderr)

    def test_rejects_output_inside_staging_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish = self.publish_tree(root)
            result = self.run_packager(publish, publish / "dist")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("error:", result.stderr)
            self.assertFalse((publish / "dist").exists())

    def test_rejects_unreadable_staging_directories(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish = self.publish_tree(root)
            blocked = publish / "blocked"
            blocked.mkdir()
            (blocked / "missing-from-archive").write_bytes(b"data")
            blocked.chmod(0)
            try:
                result = self.run_packager(publish, root / "dist")
            finally:
                blocked.chmod(0o755)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("error:", result.stderr)

    def test_rejects_output_symlink_loops(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            publish = self.publish_tree(root)
            loop = root / "loop"
            loop.symlink_to(loop)
            result = self.run_packager(publish, loop)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("error:", result.stderr)
            self.assertNotIn("Traceback", result.stderr)

    def test_rejects_invalid_rid_version_and_timestamp(self):
        cases = (
            ("linux-ppc64", VERSION, COMMIT_TIME),
            ("linux-x64", "../0.1.0", COMMIT_TIME),
            ("linux-x64", "01.0.0", COMMIT_TIME),
            ("linux-x64", VERSION, "yesterday"),
            ("linux-x64", VERSION, "315532799"),
            ("linux-x64", VERSION, "4294967296"),
            ("linux-x64", VERSION, "4354819200"),
        )
        for rid, version, commit_time in cases:
            with (
                self.subTest(rid=rid, version=version, commit_time=commit_time),
                tempfile.TemporaryDirectory() as directory,
            ):
                root = Path(directory)
                result = self.run_packager(
                    self.publish_tree(root),
                    root / "dist",
                    rid=rid,
                    version=version,
                    commit_time=commit_time,
                )
                self.assertNotEqual(0, result.returncode)
                self.assertIn("error:", result.stderr)
                self.assertFalse((root / "dist").exists())

    def publish_tree(self, root):
        publish = root / "publish"
        publish.mkdir()
        binary = publish / "perf-sentinel-hub"
        binary.write_bytes(b"native-binary")
        binary.chmod(0o755)
        (publish / "appsettings.json").write_text("{}\n", encoding="utf-8")
        symbols = publish / "perf-sentinel-hub.dbg"
        symbols.write_bytes(b"symbols")
        symbols.chmod(0o755)
        return publish

    def run_packager(self, publish, output, *, rid="osx-arm64", version=VERSION, commit_time=COMMIT_TIME):
        return subprocess.run(
            [
                sys.executable,
                str(PACKAGER),
                "--rid",
                rid,
                "--version",
                version,
                "--commit-time",
                commit_time,
                "--input",
                str(publish),
                "--output",
                str(output),
            ],
            cwd=REPOSITORY,
            text=True,
            capture_output=True,
            check=False,
        )

    def output_hashes(self, output):
        return {
            path.name: hashlib.sha256(path.read_bytes()).hexdigest()
            for path in output.iterdir()
        }


if __name__ == "__main__":
    unittest.main()
