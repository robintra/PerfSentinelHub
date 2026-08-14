#!/usr/bin/env python3
"""Validate an OCI layout and emit its immutable index digest."""

from __future__ import annotations

import argparse
import hashlib
import io
import json
import os
import re
import sys
import tarfile
import tempfile
from pathlib import Path, PurePosixPath


INDEX_FILE = "index.json"
SHA256 = re.compile(r"^sha256:([0-9a-f]{64})$")
PLATFORM = re.compile(r"^(linux)/(amd64|arm64)$")
INDEX_MEDIA_TYPES = {
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
}
ALL_PLATFORMS = frozenset((('linux', 'amd64'), ('linux', 'arm64')))


def load_json(content: bytes, description: str):
    def no_duplicates(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"{description} contains duplicate JSON key {key!r}")
            result[key] = value
        return result

    try:
        return json.loads(content.decode("utf-8"), object_pairs_hook=no_duplicates)
    except (UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"{description} is not valid JSON: {error}") from error


def archive_member_content(archive, member, entries):
    pure = PurePosixPath(member.name)
    if member.name != pure.as_posix() or pure.is_absolute() or ".." in pure.parts:
        raise ValueError("OCI tar archive contains an unsafe path")
    if member.isdir():
        return None
    if not member.isfile() or member.name in entries:
        raise ValueError("OCI tar archive entries must be unique regular files")
    stream = archive.extractfile(member)
    if stream is None:
        raise ValueError("OCI tar archive entry cannot be read")
    content = stream.read()
    if len(content) != member.size:
        raise ValueError("OCI tar archive entry is truncated")
    return content


class Layout:
    def __init__(self, path: Path):
        self.path = path
        self.entries = self._read()
        self.referenced_blobs = set()

    def _read(self):
        if self.path.is_dir() and not self.path.is_symlink():
            return self._read_directory()
        if not self.path.is_file() or self.path.is_symlink():
            raise ValueError("OCI layout must be a regular directory or tar archive")
        return self._read_archive()

    def _read_directory(self):
        entries = {}
        for candidate in self.path.rglob("*"):
            if candidate.is_symlink() or (candidate.exists() and not candidate.is_file() and not candidate.is_dir()):
                raise ValueError("OCI layout contains a non-regular entry")
            if candidate.is_file():
                entries[candidate.relative_to(self.path).as_posix()] = candidate.read_bytes()
        return entries

    def _read_archive(self):
        raw = self.path.read_bytes()
        if len(raw) < 1024 or raw[-1024:] != bytes(1024):
            raise ValueError("OCI tar archive has noncanonical trailing data")
        entries = {}
        try:
            with tarfile.open(self.path, "r:*") as archive:
                for member in archive:
                    content = archive_member_content(archive, member, entries)
                    if content is not None:
                        entries[member.name] = content
        except tarfile.TarError as error:
            raise ValueError(f"OCI tar archive is invalid: {error}") from error
        return entries

    def required(self, name: str) -> bytes:
        try:
            return self.entries[name]
        except KeyError as error:
            raise ValueError(f"OCI layout is missing {name}") from error

    def blob(self, descriptor: dict) -> bytes:
        if not isinstance(descriptor, dict):
            raise ValueError("OCI descriptor must be an object")
        digest = descriptor.get("digest")
        match = SHA256.fullmatch(digest) if isinstance(digest, str) else None
        if match is None or type(descriptor.get("size")) is not int or descriptor["size"] < 0:
            raise ValueError("OCI descriptor digest and size must be canonical")
        content = self.required(f"blobs/sha256/{match.group(1)}")
        if len(content) != descriptor["size"] or hashlib.sha256(content).hexdigest() != match.group(1):
            raise ValueError("OCI descriptor blob digest or size mismatch")
        self.referenced_blobs.add(f"blobs/sha256/{match.group(1)}")
        return content

    def reject_unreferenced_blobs(self):
        blobs = {name for name in self.entries if name.startswith("blobs/sha256/")}
        expected = {"oci-layout", INDEX_FILE} | self.referenced_blobs
        if blobs != self.referenced_blobs or set(self.entries) != expected:
            raise ValueError("OCI layout contains missing, unreferenced, or unlisted blobs")


def validated_layout(
    path: Path,
    expected_platforms: frozenset[tuple[str, str]],
    expected_manifests: dict[tuple[str, str], str] | None = None,
):
    layout = Layout(path)
    manifests, root_digest = root_manifests(layout)
    reject_unexpected_platforms(layout, manifests, expected_platforms, expected_manifests)
    walk_manifest_blobs(layout, manifests)
    layout.reject_unreferenced_blobs()
    return root_digest, layout, manifests


def root_manifests(layout):
    marker = load_json(layout.required("oci-layout"), "oci-layout")
    if marker != {"imageLayoutVersion": "1.0.0"}:
        raise ValueError("OCI layout version must be exactly 1.0.0")
    index_content = layout.required(INDEX_FILE)
    index = load_json(index_content, "OCI index")
    if not isinstance(index, dict) or index.get("schemaVersion") != 2 or not isinstance(index.get("manifests"), list):
        raise ValueError("OCI index structure is invalid")

    manifests = index["manifests"]
    root_digest = f"sha256:{hashlib.sha256(index_content).hexdigest()}"
    if len(manifests) != 1 or manifests[0].get("mediaType") not in INDEX_MEDIA_TYPES or "platform" in manifests[0]:
        return manifests, root_digest
    descriptor = manifests[0]
    nested = load_json(layout.blob(descriptor), "OCI nested index")
    if not isinstance(nested, dict) or nested.get("schemaVersion") != 2 or not isinstance(nested.get("manifests"), list):
        raise ValueError("OCI nested index structure is invalid")
    return nested["manifests"], descriptor["digest"]


def reject_unexpected_platforms(layout, manifests, expected_platforms, expected_manifests):
    actual_platforms = []
    for descriptor in manifests:
        if not isinstance(descriptor, dict) or not isinstance(descriptor.get("platform"), dict):
            raise ValueError("OCI manifest platform is required")
        platform = descriptor["platform"]
        if set(platform) != {"os", "architecture"}:
            raise ValueError("OCI manifest platform must contain only os and architecture")
        actual_platforms.append((platform["os"], platform["architecture"]))
        layout.blob(descriptor)
    if len(actual_platforms) != len(set(actual_platforms)) or frozenset(actual_platforms) != expected_platforms:
        rendered = ", ".join(f"{os_name}/{architecture}" for os_name, architecture in sorted(actual_platforms))
        raise ValueError(f"OCI platforms differ from the exact expected set: {rendered}")
    actual_manifests = {
        identity: descriptor["digest"]
        for identity, descriptor in zip(actual_platforms, manifests, strict=True)
    }
    if expected_manifests is not None and actual_manifests != expected_manifests:
        raise ValueError("OCI manifest digest differs from the exact verified platform subjects")


def walk_manifest_blobs(layout, manifests):
    for descriptor in manifests:
        manifest = load_json(layout.blob(descriptor), "OCI image manifest")
        if not isinstance(manifest, dict) or manifest.get("schemaVersion") != 2:
            raise ValueError("OCI image manifest structure is invalid")
        config = manifest.get("config")
        layers = manifest.get("layers")
        if not isinstance(config, dict) or not isinstance(layers, list):
            raise ValueError("OCI image manifest config and layers are required")
        layout.blob(config)
        for layer in layers:
            layout.blob(layer)


def validated_digest(
    path: Path,
    expected_platforms: frozenset[tuple[str, str]],
    expected_manifests: dict[tuple[str, str], str] | None = None,
) -> str:
    return validated_layout(path, expected_platforms, expected_manifests)[0]


def descriptor_path(descriptor: dict) -> str:
    match = SHA256.fullmatch(descriptor["digest"])
    if match is None:
        raise ValueError("OCI descriptor digest must be canonical")
    return f"blobs/sha256/{match.group(1)}"


def image_blob_paths(layout: Layout, descriptor: dict) -> set[str]:
    manifest = load_json(layout.blob(descriptor), "OCI image manifest")
    config = manifest.get("config") if isinstance(manifest, dict) else None
    layers = manifest.get("layers") if isinstance(manifest, dict) else None
    if not isinstance(config, dict) or not isinstance(layers, list):
        raise ValueError("OCI image manifest config and layers are required")
    result = {descriptor_path(descriptor), descriptor_path(config)}
    layout.blob(config)
    for layer in layers:
        layout.blob(layer)
        result.add(descriptor_path(layer))
    return result


def compose_layout(
    output: Path,
    sources: dict[tuple[str, str], Path],
    source_date_epoch: int,
) -> str:
    if set(sources) != set(ALL_PLATFORMS):
        raise ValueError("OCI composition requires exactly linux/amd64 and linux/arm64 sources")
    if output.exists() or output.is_symlink():
        raise ValueError("OCI composition output must not already exist")
    output.parent.mkdir(parents=True, exist_ok=True)
    descriptors = []
    blobs = {}
    expected_manifests = {}
    for identity, source in sorted(sources.items()):
        _, layout, manifests = validated_layout(source, frozenset((identity,)))
        descriptor = manifests[0]
        descriptors.append(descriptor)
        expected_manifests[identity] = descriptor["digest"]
        for name in image_blob_paths(layout, descriptor):
            content = layout.entries[name]
            if name in blobs and blobs[name] != content:
                raise ValueError("OCI sources contain conflicting content for one digest")
            blobs[name] = content

    entries = {
        "oci-layout": b'{"imageLayoutVersion":"1.0.0"}',
        INDEX_FILE: json.dumps(
            {
                "schemaVersion": 2,
                "mediaType": "application/vnd.oci.image.index.v1+json",
                "manifests": descriptors,
            },
            sort_keys=True,
            separators=(",", ":"),
        ).encode("ascii"),
        **blobs,
    }
    temporary_name = None
    try:
        with tempfile.NamedTemporaryFile(dir=output.parent, prefix=f".{output.name}.", delete=False) as stream:
            temporary_name = stream.name
            with tarfile.open(fileobj=stream, mode="w", format=tarfile.USTAR_FORMAT) as archive:
                for name, content in sorted(entries.items()):
                    info = tarfile.TarInfo(name)
                    info.size = len(content)
                    info.mode = 0o644
                    info.uid = info.gid = 0
                    info.mtime = source_date_epoch
                    archive.addfile(info, io.BytesIO(content))
        os.replace(temporary_name, output)
        temporary_name = None
    finally:
        if temporary_name is not None:
            Path(temporary_name).unlink(missing_ok=True)
    return validated_digest(output, ALL_PLATFORMS, expected_manifests)


def parse_platforms(value: str) -> frozenset[tuple[str, str]]:
    result = set()
    for item in value.split(","):
        match = PLATFORM.fullmatch(item)
        if match is None or (match.group(1), match.group(2)) in result:
            raise argparse.ArgumentTypeError("platforms must be a unique comma-separated subset of linux/amd64,linux/arm64")
        result.add((match.group(1), match.group(2)))
    if not result:
        raise argparse.ArgumentTypeError("at least one platform is required")
    return frozenset(result)


def parse_source(value: str) -> tuple[tuple[str, str], Path]:
    platform, separator, path = value.partition("=")
    parsed = parse_platforms(platform)
    if not separator or len(parsed) != 1 or not path:
        raise argparse.ArgumentTypeError("source must be linux/ARCH=OCI_LAYOUT")
    return next(iter(parsed)), Path(path)


def parse_expected_manifest(value: str) -> tuple[tuple[str, str], str]:
    platform, separator, digest = value.partition("=")
    parsed = parse_platforms(platform)
    if not separator or len(parsed) != 1 or SHA256.fullmatch(digest) is None:
        raise argparse.ArgumentTypeError("expected manifest must be linux/ARCH=sha256:DIGEST")
    return next(iter(parsed)), digest


def unique_bindings(bindings, description: str):
    result = {}
    for key, value in bindings:
        if key in result:
            raise ValueError(f"duplicate {description} platform")
        result[key] = value
    return result


def requested_digest(arguments) -> str:
    if arguments.source_date_epoch < 0:
        raise ValueError("source date epoch must be nonnegative")
    if arguments.layout is None:
        if arguments.expected_manifest:
            raise ValueError("expected-manifest is valid only with layout")
        return compose_layout(
            arguments.compose_output,
            unique_bindings(arguments.source, "source"),
            arguments.source_date_epoch,
        )
    if arguments.source:
        raise ValueError("source is valid only with compose-output")
    expected = unique_bindings(arguments.expected_manifest, "expected manifest")
    if expected and set(expected) != set(arguments.platforms):
        raise ValueError("expected manifests must cover every requested platform exactly")
    return validated_digest(arguments.layout, arguments.platforms, expected or None)


def emit_digest(arguments, digest: str) -> None:
    if arguments.write_digest is None:
        print(digest)
        return
    if arguments.write_digest.exists() and (arguments.write_digest.is_symlink() or not arguments.write_digest.is_file()):
        raise ValueError("digest output must be a regular file")
    arguments.write_digest.write_text(f"{digest}\n", encoding="ascii")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--layout", type=Path)
    mode.add_argument("--compose-output", type=Path)
    parser.add_argument("--platforms", type=parse_platforms, default=ALL_PLATFORMS)
    parser.add_argument("--source", type=parse_source, action="append", default=[])
    parser.add_argument("--source-date-epoch", type=int, default=0)
    parser.add_argument("--expected-manifest", type=parse_expected_manifest, action="append", default=[])
    parser.add_argument("--write-digest", type=Path)
    arguments = parser.parse_args(argv)
    try:
        emit_digest(arguments, requested_digest(arguments))
        return 0
    except (OSError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
