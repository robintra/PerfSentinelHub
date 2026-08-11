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
        raise ValueError("commit time is outside the supported archive range")
    try:
        year = datetime.datetime.fromtimestamp(timestamp, datetime.timezone.utc).year
    except (OverflowError, OSError, ValueError) as error:
        raise ValueError("commit time is outside the supported archive range") from error
    if not 1980 <= year <= 2106:
        raise ValueError("commit time is outside the supported archive range")
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
            path = current_path / name
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
            if name in directories:
                if not stat.S_ISDIR(metadata.st_mode):
                    raise ValueError(f"non-directory encountered during traversal: {relative}")
                continue
            if not stat.S_ISREG(metadata.st_mode):
                raise ValueError(f"only regular files are permitted: {relative}")
            symbol = any(part.casefold().endswith(".dsym") for part in Path(relative).parts) or path.suffix.casefold() in SYMBOL_SUFFIXES
            entries.append((path, relative, metadata, symbol))
    entries.sort(key=lambda entry: entry[1])
    return entries


def open_entry(entry):
    path, relative, expected, _ = entry
    flags = os.O_RDONLY | getattr(os, "O_NOFOLLOW", 0)
    descriptor = os.open(path, flags)
    actual = os.fstat(descriptor)
    if not stat.S_ISREG(actual.st_mode) or (actual.st_dev, actual.st_ino, actual.st_size) != (
        expected.st_dev,
        expected.st_ino,
        expected.st_size,
    ):
        os.close(descriptor)
        raise ValueError(f"file changed during packaging: {relative}")
    return os.fdopen(descriptor, "rb")


def normalized_mode(entry) -> int:
    path, _, metadata, symbol = entry
    return 0o755 if not symbol and (metadata.st_mode & 0o111 or path.suffix.casefold() == ".exe") else 0o644


def write_tar(path: Path, entries, commit_time: int) -> None:
    with path.open("wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, compresslevel=9, mtime=commit_time) as compressed:
            with tarfile.open(fileobj=compressed, mode="w", format=tarfile.PAX_FORMAT) as archive:
                for entry in entries:
                    _, relative, metadata, _ = entry
                    info = tarfile.TarInfo(relative)
                    info.size = metadata.st_size
                    info.mode = normalized_mode(entry)
                    info.mtime = commit_time
                    info.uid = info.gid = 0
                    info.uname = info.gname = ""
                    with open_entry(entry) as source:
                        archive.addfile(info, source)


def write_zip(path: Path, entries, commit_time: int) -> None:
    timestamp = datetime.datetime.fromtimestamp(commit_time, datetime.timezone.utc).timetuple()[:6]
    with zipfile.ZipFile(path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=9) as archive:
        for entry in entries:
            _, relative, metadata, _ = entry
            info = zipfile.ZipInfo(relative, timestamp)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 3
            info.external_attr = (stat.S_IFREG | normalized_mode(entry)) << 16
            with open_entry(entry) as source, archive.open(
                info, "w", force_zip64=metadata.st_size >= 2 * 1024**3
            ) as target:
                shutil.copyfileobj(source, target, BUFFER_SIZE)


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
    if inside(output.resolve(strict=False), root):
        raise ValueError("output directory must be outside the staging directory")
    entries = scan_staging(root)
    runtime = [entry for entry in entries if not entry[3]]
    symbols = [entry for entry in entries if entry[3]]
    extension = ".zip" if rid == "win-x64" else ".tar.gz"
    base = f"perf-sentinel-hub-{version}-{rid}"
    names = (f"{base}{extension}", f"{base}-symbols{extension}")
    writer = write_zip if rid == "win-x64" else write_tar

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
