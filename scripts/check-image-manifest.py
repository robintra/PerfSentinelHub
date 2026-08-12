#!/usr/bin/env python3
"""Validate an OCI layout and emit its immutable index digest."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
import tarfile
from pathlib import Path, PurePosixPath


SHA256 = re.compile(r"^sha256:([0-9a-f]{64})$")
PLATFORM = re.compile(r"^(linux)/(amd64|arm64)$")
INDEX_MEDIA_TYPES = {
    "application/vnd.oci.image.index.v1+json",
    "application/vnd.docker.distribution.manifest.list.v2+json",
}


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


class Layout:
    def __init__(self, path: Path):
        self.path = path
        self.entries = self._read()
        self.referenced_blobs = set()

    def _read(self):
        if self.path.is_dir() and not self.path.is_symlink():
            entries = {}
            for candidate in self.path.rglob("*"):
                if candidate.is_symlink() or (candidate.exists() and not candidate.is_file() and not candidate.is_dir()):
                    raise ValueError("OCI layout contains a non-regular entry")
                if candidate.is_file():
                    entries[candidate.relative_to(self.path).as_posix()] = candidate.read_bytes()
            return entries
        if not self.path.is_file() or self.path.is_symlink():
            raise ValueError("OCI layout must be a regular directory or tar archive")
        raw = self.path.read_bytes()
        if len(raw) < 1024 or raw[-1024:] != bytes(1024):
            raise ValueError("OCI tar archive has noncanonical trailing data")
        entries = {}
        try:
            with tarfile.open(self.path, "r:*") as archive:
                for member in archive:
                    pure = PurePosixPath(member.name)
                    if member.name != pure.as_posix() or pure.is_absolute() or ".." in pure.parts:
                        raise ValueError("OCI tar archive contains an unsafe path")
                    if member.isdir():
                        continue
                    if not member.isfile() or member.name in entries:
                        raise ValueError("OCI tar archive entries must be unique regular files")
                    stream = archive.extractfile(member)
                    if stream is None:
                        raise ValueError("OCI tar archive entry cannot be read")
                    content = stream.read()
                    if len(content) != member.size:
                        raise ValueError("OCI tar archive entry is truncated")
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
        expected = {"oci-layout", "index.json"} | self.referenced_blobs
        if blobs != self.referenced_blobs or set(self.entries) != expected:
            raise ValueError("OCI layout contains missing, unreferenced, or unlisted blobs")


def validated_digest(path: Path, expected_platforms: frozenset[tuple[str, str]]) -> str:
    layout = Layout(path)
    marker = load_json(layout.required("oci-layout"), "oci-layout")
    if marker != {"imageLayoutVersion": "1.0.0"}:
        raise ValueError("OCI layout version must be exactly 1.0.0")
    index_content = layout.required("index.json")
    index = load_json(index_content, "OCI index")
    if not isinstance(index, dict) or index.get("schemaVersion") != 2 or not isinstance(index.get("manifests"), list):
        raise ValueError("OCI index structure is invalid")

    manifests = index["manifests"]
    root_digest = f"sha256:{hashlib.sha256(index_content).hexdigest()}"
    if len(manifests) == 1 and manifests[0].get("mediaType") in INDEX_MEDIA_TYPES and "platform" not in manifests[0]:
        descriptor = manifests[0]
        nested_content = layout.blob(descriptor)
        nested = load_json(nested_content, "OCI nested index")
        if not isinstance(nested, dict) or nested.get("schemaVersion") != 2 or not isinstance(nested.get("manifests"), list):
            raise ValueError("OCI nested index structure is invalid")
        manifests = nested["manifests"]
        root_digest = descriptor["digest"]

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
    layout.reject_unreferenced_blobs()
    return root_digest


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


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--layout", type=Path, required=True)
    parser.add_argument("--platforms", type=parse_platforms, default=parse_platforms("linux/amd64,linux/arm64"))
    parser.add_argument("--write-digest", type=Path)
    arguments = parser.parse_args(argv)
    try:
        digest = validated_digest(arguments.layout, arguments.platforms)
        if arguments.write_digest is not None:
            if arguments.write_digest.exists() and (arguments.write_digest.is_symlink() or not arguments.write_digest.is_file()):
                raise ValueError("digest output must be a regular file")
            arguments.write_digest.write_text(f"{digest}\n", encoding="ascii")
        else:
            print(digest)
        return 0
    except (OSError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
