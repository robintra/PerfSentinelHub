#!/usr/bin/env python3
"""Create reproducible NativeAOT runtime and symbol archives."""

from __future__ import annotations

import argparse
import datetime
import gzip
import hashlib
import os
import re
import shutil
import stat
import tarfile
import tempfile
import unicodedata
import zipfile
from pathlib import Path


ARCHIVE_RANGE_ERROR = "commit time is outside the supported archive range"
RIDS = frozenset(("linux-x64", "linux-arm64", "osx-arm64", "win-x64"))
VERSION = re.compile(
    r"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?"
    r"(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$"
)
SYMBOL_SUFFIXES = frozenset((".dbg", ".dwarf", ".pdb"))
BUFFER_SIZE = 1024 * 1024


def valid_version(value: str) -> bool:
    match = VERSION.fullmatch(value)
    return bool(
        match
        and all(
            not (part.isdecimal() and len(part) > 1 and part.startswith("0"))
            for part in (match.group(4) or "").split(".")
        )
    )


def parse_commit_time(value: str) -> int:
    if re.fullmatch(r"[1-9][0-9]*", value) is None:
        raise ValueError("commit time must be a canonical Unix timestamp")
    timestamp = int(value)
    if not 315532800 <= timestamp <= 0xFFFFFFFF:
        raise ValueError(ARCHIVE_RANGE_ERROR)
    try:
        year = datetime.datetime.fromtimestamp(timestamp, datetime.timezone.utc).year
    except (OverflowError, OSError, ValueError) as error:
        raise ValueError(ARCHIVE_RANGE_ERROR) from error
    if not 1980 <= year <= 2106:
        raise ValueError(ARCHIVE_RANGE_ERROR)
    return timestamp


def inside(path: Path, root: Path) -> bool:
    try:
        path.relative_to(root)
        return True
    except ValueError:
        return False


def archive_name(path: Path, root: Path) -> str:
    name = path.relative_to(root).as_posix()
    if "\\" in name or ":" in name or any(part in ("", ".", "..") for part in name.split("/")):
        raise ValueError(f"unsafe archive path: {name}")
    return name


def staging_entry(path: Path, root: Path, is_directory: bool, names: set[str]):
    relative = archive_name(path, root)
    collision_key = "/".join(
        unicodedata.normalize("NFC", part).rstrip(" .").casefold()
        for part in relative.split("/")
    )
    if collision_key in names:
        raise ValueError(f"archive path collision: {relative}")
    names.add(collision_key)
    metadata = path.lstat()
    if stat.S_ISLNK(metadata.st_mode):
        raise ValueError(f"symlinks are not permitted: {relative}")
    if not inside(path.resolve(strict=True), root):
        raise ValueError(f"path escapes staging directory: {relative}")
    if is_directory:
        if not stat.S_ISDIR(metadata.st_mode):
            raise ValueError(f"non-directory encountered during traversal: {relative}")
        return None
    if not stat.S_ISREG(metadata.st_mode):
        raise ValueError(f"only regular files are permitted: {relative}")
    symbol = any(part.casefold().endswith(".dsym") for part in Path(relative).parts) or path.suffix.casefold() in SYMBOL_SUFFIXES
    return path, relative, metadata, symbol


def scan_staging(root: Path):
    def fail(error):
        raise error

    entries = []
    names = set()
    for current, directories, files in os.walk(root, topdown=True, onerror=fail, followlinks=False):
        directories.sort()
        files.sort()
        current_path = Path(current)
        for name in (*directories, *files):
            entry = staging_entry(current_path / name, root, name in directories, names)
            if entry is not None:
                entries.append(entry)
    entries.sort(key=lambda entry: entry[1])
    return entries


def metadata_signature(metadata):
    return (
        stat.S_IFMT(metadata.st_mode),
        metadata.st_dev,
        metadata.st_ino,
        metadata.st_size,
        metadata.st_mtime_ns,
        metadata.st_ctime_ns,
    )


def snapshot_entries(entries, directory: Path):
    snapshots = []
    for index, (path, relative, expected, symbol) in enumerate(entries):
        snapshot = directory / f"{index:08x}"
        flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
        descriptor = os.open(path, flags)
        try:
            before = os.fstat(descriptor)
            if metadata_signature(before) != metadata_signature(expected):
                raise ValueError(f"file changed during packaging: {relative}")
            source = os.fdopen(descriptor, "rb")
            descriptor = -1
            digest = hashlib.sha256()
            copied = 0
            with source, snapshot.open("xb") as target:
                while chunk := source.read(BUFFER_SIZE):
                    target.write(chunk)
                    digest.update(chunk)
                    copied += len(chunk)
                after = os.fstat(source.fileno())
            if copied != before.st_size or metadata_signature(after) != metadata_signature(before):
                raise ValueError(f"file changed during packaging: {relative}")
            snapshot.chmod(0o400)
            snapshots.append((snapshot, relative, expected, symbol, digest.digest()))
        finally:
            if descriptor >= 0:
                os.close(descriptor)
    return snapshots


class SnapshotReader:
    def __init__(self, entry):
        path, relative, expected, _, expected_digest = entry
        flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
        descriptor = os.open(path, flags)
        opened = os.fstat(descriptor)
        if not stat.S_ISREG(opened.st_mode) or opened.st_size != expected.st_size:
            os.close(descriptor)
            raise ValueError(f"snapshot changed during packaging: {relative}")
        try:
            self.stream = os.fdopen(descriptor, "rb")
        except BaseException:
            os.close(descriptor)
            raise
        self.relative = relative
        self.expected_size = expected.st_size
        self.expected_digest = expected_digest
        self.opened = metadata_signature(opened)
        self.digest = hashlib.sha256()
        self.size = 0

    def read(self, size=-1):
        chunk = self.stream.read(size)
        self.digest.update(chunk)
        self.size += len(chunk)
        return chunk

    def verify(self):
        closed = metadata_signature(os.fstat(self.stream.fileno()))
        if (
            closed != self.opened
            or self.size != self.expected_size
            or self.digest.digest() != self.expected_digest
        ):
            raise ValueError(f"snapshot changed during packaging: {self.relative}")

    def __enter__(self):
        return self

    def __exit__(self, *exception):
        self.stream.close()


def normalized_mode(entry) -> int:
    _, relative, metadata, symbol, _ = entry
    return 0o755 if not symbol and (metadata.st_mode & 0o111 or Path(relative).suffix.casefold() == ".exe") else 0o644


def write_tar(path: Path, entries, commit_time: int) -> None:
    with path.open("wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, compresslevel=9, mtime=commit_time) as compressed:
            with tarfile.open(fileobj=compressed, mode="w", format=tarfile.PAX_FORMAT) as archive:
                for entry in entries:
                    _, relative, metadata, _, _ = entry
                    info = tarfile.TarInfo(relative)
                    info.size = metadata.st_size
                    info.mode = normalized_mode(entry)
                    info.mtime = commit_time
                    info.uid = info.gid = 0
                    info.uname = info.gname = ""
                    with SnapshotReader(entry) as source:
                        archive.addfile(info, source)
                        source.verify()


def write_zip(path: Path, entries, commit_time: int) -> None:
    timestamp = datetime.datetime.fromtimestamp(commit_time, datetime.timezone.utc).timetuple()[:6]
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for entry in entries:
            _, relative, metadata, _, _ = entry
            info = zipfile.ZipInfo(relative, timestamp)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 3
            info.external_attr = (stat.S_IFREG | normalized_mode(entry)) << 16
            with SnapshotReader(entry) as source, archive.open(
                info, "w", force_zip64=metadata.st_size >= 2 * 1024**3
            ) as target:
                shutil.copyfileobj(source, target, BUFFER_SIZE)
                source.verify()


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        while chunk := stream.read(BUFFER_SIZE):
            digest.update(chunk)
    return digest.hexdigest()


def package(rid: str, version: str, commit_time: int, staging: Path, output: Path) -> None:
    if rid not in RIDS:
        raise ValueError("RID must be one of linux-x64, linux-arm64, osx-arm64, win-x64")
    if not valid_version(version):
        raise ValueError("version must be canonical SemVer without a v prefix")
    if staging.is_symlink() or not staging.is_dir():
        raise ValueError("input must be a real staging directory")
    root = staging.resolve(strict=True)
    try:
        resolved_output = output.resolve(strict=False)
    except (OSError, RuntimeError) as error:
        raise ValueError("output directory cannot be resolved") from error
    if inside(resolved_output, root):
        raise ValueError("output directory must be outside the staging directory")
    entries = scan_staging(root)
    extension = ".zip" if rid == "win-x64" else ".tar.gz"
    base = f"perf-sentinel-hub-{version}-{rid}"
    names = (f"{base}{extension}", f"{base}-symbols{extension}")
    writer = write_zip if rid == "win-x64" else write_tar

    with tempfile.TemporaryDirectory(prefix="perf-sentinel-hub-") as snapshot_directory:
        snapshots = snapshot_entries(entries, Path(snapshot_directory))
        runtime = [entry for entry in snapshots if not entry[3]]
        symbols = [entry for entry in snapshots if entry[3]]
        output.mkdir(parents=True, exist_ok=True)
        temporary = []
        try:
            for name, selected in zip(names, (runtime, symbols), strict=True):
                descriptor, temporary_name = tempfile.mkstemp(dir=output, prefix=".package-")
                os.close(descriptor)
                temporary_path = Path(temporary_name)
                temporary.append(temporary_path)
                writer(temporary_path, selected, commit_time)
            sums = "".join(
                f"{sha256(path)}  {name}\n"
                for name, path in sorted(zip(names, temporary, strict=True))
            )
            descriptor, sums_name = tempfile.mkstemp(dir=output, prefix=".package-")
            with os.fdopen(descriptor, "w", encoding="ascii", newline="\n") as stream:
                stream.write(sums)
            temporary_sums = Path(sums_name)
            temporary.append(temporary_sums)
            for source, name in zip(temporary[:2], names, strict=True):
                os.replace(source, output / name)
            os.replace(temporary_sums, output / "SHA256SUMS")
        finally:
            for path in temporary:
                path.unlink(missing_ok=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--rid", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit-time", required=True)
    parser.add_argument("--input", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    arguments = parser.parse_args()
    try:
        timestamp = parse_commit_time(arguments.commit_time)
        package(arguments.rid, arguments.version, timestamp, arguments.input, arguments.output)
    except (OSError, ValueError) as error:
        parser.error(str(error))


if __name__ == "__main__":
    main()
