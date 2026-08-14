#!/usr/bin/env python3
"""Fail closed when repository supply-chain declarations drift from the inventory."""

from __future__ import annotations

import argparse
import base64
import gzip
import json
import os
import re
import ssl
import sys
import xml.etree.ElementTree as ElementTree
from datetime import datetime, timezone
from decimal import Decimal
from pathlib import Path
from urllib.error import URLError
from urllib.parse import urlsplit
from urllib.request import Request, urlopen


CSPROJ_SUFFIX = ".csproj"
NUGET_LOCK_FILE = "packages.lock.json"
REQUIRED_FIELDS = {
    "name",
    "kind",
    "version",
    "digest_or_sha",
    "released_at",
    "source",
    "reason",
}
ALLOWED_FIELDS = REQUIRED_FIELDS | {"artifact_url", "lock_content_hash", "nuget_role"}
KNOWN_KINDS = frozenset({"container", "dotnet-sdk", "dotnet-tool", "download", "github-action", "github-release", "nuget"})
ACTION_SHA = re.compile(r"^[0-9a-f]{40}$", re.IGNORECASE)
SHA256 = re.compile(r"^sha256:[0-9a-f]{64}$")
SEMVER_PRERELEASE = re.compile(r"^v?\d+(?:\.\d+){1,3}-[0-9A-Za-z.-]+(?:\+[0-9A-Za-z.-]+)?$")
UNSTABLE_CONTAINER_TAG = re.compile(
    r"(?:^|[._-])(?:alpha|beta|rc|preview|pre|eap|nightly|snapshot|canary|unstable|dev)[0-9]*(?:[._-]|$)",
    re.IGNORECASE,
)
FROM_LINE = re.compile(r"^\s*FROM\s+(?:--platform=\S+\s+)?(\S+)", re.IGNORECASE)
SAFE_NAME = r"[A-Za-z0-9][A-Za-z0-9._-]*"
SAFE_RELATIVE_PATH = rf"{SAFE_NAME}(?:/{SAFE_NAME})*"
SAFE_VERSION = r"[A-Za-z0-9][A-Za-z0-9._+-]*"
GITHUB_RELEASE_URL = rf"https://github\.com/({SAFE_NAME})/({SAFE_NAME})/releases/tag/({SAFE_NAME})"
GITHUB_ARTIFACT_URL = rf"https://github\.com/{SAFE_NAME}/{SAFE_NAME}/releases/download/{SAFE_NAME}/{SAFE_NAME}"
GITHUB_RELEASE = re.compile(rf"^{GITHUB_RELEASE_URL}$")
GITHUB_ARTIFACT = re.compile(
    rf"^https://github\.com/({SAFE_NAME})/({SAFE_NAME})/releases/download/({SAFE_NAME})/({SAFE_NAME})$"
)
CANONICAL_DOWNLOAD = re.compile(
    rf"/usr/bin/curl -q -fsSL(?: --retry 5 --retry-all-errors --retry-delay 2)? "
    rf"(?P<url>{GITHUB_ARTIFACT_URL}) -o (?P<output>{SAFE_RELATIVE_PATH})"
)
CANONICAL_USES = re.compile(
    rf"^\s+- uses: (?P<name>{SAFE_RELATIVE_PATH})@(?P<sha>[0-9A-Fa-f]{{40}})(?: # {SAFE_NAME})?$"
)
YAML_USES_WORD = re.compile(r"(?<![a-z0-9_-])['\"]?uses['\"]?(?![a-z0-9_-])", re.IGNORECASE)
YAML_QUOTED_KEY = re.compile(r"(?:^|[{,])\s*['\"][^'\"]+['\"]\s*:")
DOWNLOAD_COMMAND = re.compile(r"(?<![A-Za-z0-9_-])(?:curl|wget)(?![A-Za-z0-9_-])")
DOTNET_SHA512 = re.compile(r"^sha512:[0-9a-f]{128}$")
NUGET_SHA512 = re.compile(r"^sha512-base64:[A-Za-z0-9+/]+={0,2}$")
DOTNET_RELEASES = "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json"
NUGET_INDEX = "https://api.nuget.org/v3/index.json"
DATE_ONLY = re.compile(
    r"^(?P<year>[0-9]{4})-(?P<month>0[1-9]|1[0-2])-(?P<day>0[1-9]|[12][0-9]|3[01])$"
)
RFC3339_TIMESTAMP = re.compile(
    r"^(?P<year>[0-9]{4})-(?P<month>0[1-9]|1[0-2])-(?P<day>0[1-9]|[12][0-9]|3[01])"
    r"T(?P<hour>[01][0-9]|2[0-3]):(?P<minute>[0-5][0-9]):(?P<second>[0-5][0-9])"
    r"(?:\.(?P<fraction>[0-9]{1,9}))?"
    r"(?P<zone>Z|(?P<offset_sign>[+-])(?P<offset_hour>[01][0-9]|2[0-3]):(?P<offset_minute>[0-5][0-9]))$"
)
NUGET_ROLES = frozenset({"sdk-aot-base", "sdk-aot-base-rid"})
FORBIDDEN_MSBUILD_PROPERTIES = {
    "centralpackageversionoverrideenabled",
    "managepackageversionscentrally",
    "restoreconfigfile",
    "restoresources",
    "restoreadditionalprojectsources",
    "restorefallbackfolders",
    "restoreadditionalprojectfallbackfolders",
}
# The checksum shape each inventory kind must carry, with the message when it does not.
DIGEST_SHAPES = {
    "github-release": (ACTION_SHA, "GitHub releases require a raw release commit SHA"),
    "github-action": (ACTION_SHA, "GitHub Actions require a full commit SHA"),
    "container": (SHA256, "downloaded tools and containers require a sha256 checksum"),
    "download": (SHA256, "downloaded tools and containers require a sha256 checksum"),
    "dotnet-sdk": (DOTNET_SHA512, ".NET SDK artifacts require a sha512 checksum"),
}
UTC_EPOCH = datetime(1970, 1, 1, tzinfo=timezone.utc)


def timestamp_from_datetime(value: datetime) -> Decimal:
    if not isinstance(value, datetime) or value.tzinfo is None or value.utcoffset() is None:
        raise ValueError("timestamp is not timezone-aware")
    normalized = value.astimezone(timezone.utc)
    delta = normalized.replace(microsecond=0) - UTC_EPOCH
    return Decimal(delta.days * 86400 + delta.seconds) + Decimal(normalized.microsecond).scaleb(-6)


def parse_timestamp(value: object) -> Decimal:
    match = RFC3339_TIMESTAMP.fullmatch(value) if isinstance(value, str) else None
    if match is None:
        raise ValueError("timestamp is not canonical RFC3339")
    try:
        seconds = timestamp_from_datetime(
            datetime(
                *(int(match.group(name)) for name in ("year", "month", "day", "hour", "minute", "second")),
                tzinfo=timezone.utc,
            )
        )
    except ValueError as error:
        raise ValueError("timestamp is not canonical RFC3339") from error
    if match.group("offset_sign"):
        offset = int(match.group("offset_hour")) * 3600 + int(match.group("offset_minute")) * 60
        seconds += offset if match.group("offset_sign") == "-" else -offset
    fraction = match.group("fraction")
    return seconds + (Decimal(f"0.{fraction}") if fraction else 0)


def release_time(value: object) -> Decimal:
    match = DATE_ONLY.fullmatch(value) if isinstance(value, str) else None
    if match:
        try:
            return timestamp_from_datetime(
                datetime(
                    *(int(match.group(name)) for name in ("year", "month", "day")),
                    tzinfo=timezone.utc,
                )
            )
        except ValueError as error:
            raise ValueError("date is not canonical") from error
    return parse_timestamp(value)


def unique_object(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def load_json(text: str):
    return json.loads(text, object_pairs_hook=unique_object)


def inventory_by_name(inventory: list[dict]) -> dict[str, dict]:
    return {
        item["name"]: item
        for item in inventory
        if isinstance(item, dict) and isinstance(item.get("name"), str)
    }


def is_canonical_sha512(value: object) -> bool:
    if not isinstance(value, str) or re.fullmatch(r"[A-Za-z0-9+/]+={0,2}", value) is None:
        return False
    try:
        decoded = base64.b64decode(value, validate=True)
    except ValueError:
        return False
    return len(decoded) == 64 and base64.b64encode(decoded).decode("ascii") == value


def is_canonical_nuget_digest(value: object) -> bool:
    return bool(
        isinstance(value, str)
        and NUGET_SHA512.fullmatch(value)
        and is_canonical_sha512(value.removeprefix("sha512-base64:"))
    )


def nuget_source_matches(item: dict) -> bool:
    source = urlsplit(item["source"])
    match = re.fullmatch(
        rf"/v3/registration5-gz-semver2/(?P<name>{SAFE_NAME})/(?P<version>{SAFE_VERSION})\.json",
        source.path,
    )
    return bool(
        source.scheme == "https"
        and source.netloc == "api.nuget.org"
        and match
        and match.group("name").casefold() == item["name"].casefold()
        and match.group("version") == item["version"]
        and source.query == ""
        and source.fragment == ""
    )


def container_source_matches(item: dict) -> bool:
    source = urlsplit(item["source"])
    mcr_match = re.fullmatch(rf"/v2/(?P<repository>{SAFE_RELATIVE_PATH})/manifests/(?P<tag>{SAFE_VERSION})", source.path)
    if (
        source.scheme == "https"
        and source.netloc == "mcr.microsoft.com"
        and mcr_match
        and item["name"] == f"mcr.microsoft.com/{mcr_match.group('repository')}"
        and item["version"] == mcr_match.group("tag")
        and source.query == ""
        and source.fragment == ""
    ):
        return True
    docker_hub_match = re.fullmatch(
        rf"/v2/namespaces/(?P<namespace>{SAFE_NAME})/repositories/"
        rf"(?P<repository>{SAFE_NAME})/tags/(?P<tag>{SAFE_VERSION})",
        source.path,
    )
    return bool(
        source.scheme == "https"
        and source.netloc == "hub.docker.com"
        and docker_hub_match
        and docker_hub_match.group("namespace") == "jetbrains"
        and item["name"]
        == f"{docker_hub_match.group('namespace')}/{docker_hub_match.group('repository')}"
        and item["version"] == docker_hub_match.group("tag")
        and source.query == ""
        and source.fragment == ""
    )


def is_supported_source(item: dict) -> bool:
    source = item["source"]
    kind = item["kind"]
    if (
        not isinstance(source, str)
        or not isinstance(item.get("name"), str)
        or not isinstance(item.get("version"), str)
    ):
        return False
    if kind == "dotnet-sdk":
        return source == DOTNET_RELEASES
    if kind in ("dotnet-tool", "nuget"):
        return nuget_source_matches(item)
    if kind == "container":
        return container_source_matches(item)
    return bool(GITHUB_RELEASE.fullmatch(source))


def nuget_field_errors(name, kind, item) -> list[str]:
    if kind != "nuget":
        return [
            f"{name}: {field} is only valid for NuGet packages"
            for field in ("lock_content_hash", "nuget_role")
            if field in item
        ]
    errors = []
    if not is_canonical_sha512(item.get("lock_content_hash")):
        errors.append(f"{name}: NuGet packages require a canonical lock_content_hash")
    role = item.get("nuget_role")
    if "nuget_role" in item and (not isinstance(role, str) or role not in NUGET_ROLES):
        errors.append(f"{name}: unknown NuGet role")
    return errors


def digest_errors(name, kind, item) -> list[str]:
    errors = []
    # A malformed inventory can carry an unhashable kind, which must not raise here.
    expected = DIGEST_SHAPES.get(kind) if isinstance(kind, str) else None
    if expected is not None and not expected[0].fullmatch(str(item["digest_or_sha"])):
        errors.append(f"{name}: {expected[1]}")
    if kind in ("dotnet-tool", "nuget") and not is_canonical_nuget_digest(item["digest_or_sha"]):
        errors.append(f"{name}: NuGet packages require a sha512-base64 checksum")
    errors.extend(nuget_field_errors(name, kind, item))
    return errors


def artifact_url_errors(name, kind, item, artifact_keys) -> list[str]:
    if kind == "dotnet-sdk":
        artifact = urlsplit(str(item.get("artifact_url", "")))
        if not (
            artifact.scheme == "https"
            and artifact.netloc == "builds.dotnet.microsoft.com"
            and artifact.path.startswith(f"/dotnet/Sdk/{item['version']}/")
            and artifact.query == ""
            and artifact.fragment == ""
        ):
            return [f"{name}: .NET SDK artifact_url must match the inventory version"]
        return []
    if kind != "download":
        return []
    artifact_match = GITHUB_ARTIFACT.fullmatch(str(item.get("artifact_url", "")))
    if artifact_match is None:
        return [f"{name}: downloaded tools require an official artifact_url"]
    errors = []
    artifact_key = (
        artifact_match.group(1).casefold(),
        artifact_match.group(2).casefold(),
        artifact_match.group(3),
        artifact_match.group(4),
    )
    if artifact_key in artifact_keys:
        errors.append(f"{name}: ambiguous download artifact_url")
    artifact_keys.add(artifact_key)
    source_match = GITHUB_RELEASE.fullmatch(str(item["source"]))
    if source_match and (
        artifact_key[:2] != (source_match.group(1).casefold(), source_match.group(2).casefold())
        or artifact_key[2] != source_match.group(3)
    ):
        errors.append(f"{name}: artifact_url must belong to its source release")
    return errors


def inventory_field_errors(item, name, name_keys) -> list[str]:
    errors = []
    unknown = item.keys() - ALLOWED_FIELDS
    if unknown:
        errors.append(f"{name}: unknown inventory fields: {', '.join(sorted(unknown))}")
    if not isinstance(name, str) or re.fullmatch(SAFE_RELATIVE_PATH, name) is None:
        errors.append(f"{name}: inventory name is not canonical")
    if not isinstance(item["version"], str) or re.fullmatch(SAFE_VERSION, item["version"]) is None:
        errors.append(f"{name}: inventory version is not canonical")
    name_key = str(name).casefold()
    if name_key in name_keys:
        errors.append(f"{name}: ambiguous duplicate inventory name")
    name_keys.add(name_key)
    kind = item["kind"]
    if not isinstance(kind, str) or kind not in KNOWN_KINDS:
        errors.append(f"{name}: unknown inventory kind {item['kind']}")
    if not isinstance(item["source"], str) or not is_supported_source(item):
        errors.append(f"{name}: unsupported official source")
    if not isinstance(item["reason"], str) or not item["reason"].strip():
        errors.append(f"{name}: reason must explain the pin")
    return errors


def release_timestamp_errors(item, name, kind) -> list[str]:
    try:
        release_value = item["released_at"]
        date_only = bool(isinstance(release_value, str) and DATE_ONLY.fullmatch(release_value))
        if date_only and kind not in ("container", "dotnet-sdk"):
            raise ValueError("this source requires a precise timestamp")
        release_time(release_value)
    except (TypeError, ValueError):
        return [
            f"{name}: released_at must be an ISO-8601 date or UTC timestamp matching source precision"
        ]
    return []


def source_release_errors(item, name, kind) -> list[str]:
    match = GITHUB_RELEASE.fullmatch(str(item["source"]))
    if not match:
        return []
    errors = []
    if match.group(3).lstrip("v") != str(item["version"]).lstrip("v"):
        errors.append(f"{name}: source release tag must match the inventory version")
    if kind == "github-action" and str(item["name"]).lower() != f"{match.group(1)}/{match.group(2)}".lower():
        errors.append(f"{name}: action name must match its owner/repository source")
    return errors


def inventory_item_errors(item, name_keys, artifact_keys) -> list[str]:
    name = item.get("name", "<unnamed>") if isinstance(item, dict) else "<invalid>"
    if not isinstance(item, dict):
        return ["inventory contains a non-object item"]
    missing = REQUIRED_FIELDS - item.keys()
    if missing:
        return [f"{name}: missing required fields: {', '.join(sorted(missing))}"]

    kind = item["kind"]
    errors = inventory_field_errors(item, name, name_keys)
    prerelease = (
        UNSTABLE_CONTAINER_TAG.search(str(item["version"]))
        if kind == "container"
        else SEMVER_PRERELEASE.fullmatch(str(item["version"]))
    )
    if prerelease:
        errors.append(f"{name}: prerelease versions are not permitted")
    timestamp_errors = release_timestamp_errors(item, name, kind)
    if timestamp_errors:
        return errors + timestamp_errors

    errors.extend(digest_errors(name, kind, item))
    errors.extend(artifact_url_errors(name, kind, item, artifact_keys))
    errors.extend(source_release_errors(item, name, kind))
    return errors


def validate_inventory(inventory: list[dict]) -> list[str]:
    errors = []
    name_keys = set()
    artifact_keys = set()
    for item in inventory:
        errors.extend(inventory_item_errors(item, name_keys, artifact_keys))
    return errors


def validate_download_script(path: Path, root: Path, text: str, inventory: list[dict]) -> list[str]:
    relative = path.relative_to(root)
    if "\r" in text or not text.endswith("\n"):
        return [f"{relative}:1: download script is not canonical LF-terminated text"]
    lines = text[:-1].split("\n")
    if lines[:2] != ["#!/bin/dash", "set -eu"]:
        return [f"{relative}:1: download script must use the canonical /bin/dash header"]
    body = lines[2:]
    if not body:
        return [f"{relative}:3: download script contains no canonical download"]

    errors = []
    for offset in range(0, len(body), 2):
        number = offset + 3
        declaration = CANONICAL_DOWNLOAD.fullmatch(body[offset])
        if declaration is None:
            errors.append(f"{relative}:{number}: download command is not canonical")
            break
        artifact_url = declaration.group("url")
        output = declaration.group("output")
        matches = [
            item
            for item in inventory
            if item.get("kind") == "download" and item.get("artifact_url") == artifact_url
        ]
        if not matches:
            errors.append(f"{relative}:{number}: download URL is absent from the inventory")
            break
        if len(matches) != 1:
            errors.append(f"{relative}:{number}: download URL is ambiguous in the inventory")
            break
        checksum = matches[0]["digest_or_sha"].removeprefix("sha256:")
        verification = (
            f"/usr/bin/printf '{checksum}  {output}\\n' | /usr/bin/sha256sum -c -"
        )
        if offset + 1 >= len(body) or body[offset + 1] != verification:
            errors.append(
                f"{relative}:{number}: download does not bind {output} to its inventory checksum"
            )
            break
    return errors


def structured_files(root: Path) -> list[Path]:
    ignored = {".dotnet", ".git", "artifacts", "bin", "obj", "__pycache__"}
    return sorted(
        path
        for path in root.rglob("*")
        if path.is_file() and not any(part in ignored for part in path.parts)
    )


def validate_nuget_config(root: Path, files: list[Path]) -> list[str]:
    configs = [path for path in files if path.name.casefold() == "nuget.config"]
    expected_path = root / "NuGet.Config"
    if configs != [expected_path]:
        return ["NuGet.Config: exactly one canonical repository-root configuration is required"]
    try:
        config = ElementTree.parse(expected_path).getroot()
        children = list(config)
        if config.tag != "configuration" or config.attrib or len(children) != 1:
            raise ValueError("unexpected configuration structure")
        sources = children[0]
        entries = list(sources)
        if sources.tag != "packageSources" or sources.attrib or len(entries) != 2:
            raise ValueError("unexpected packageSources structure")
        clear, source = entries
        if clear.tag != "clear" or clear.attrib or list(clear):
            raise ValueError("package sources must start with clear")
        if (
            source.tag != "add"
            or source.attrib
            != {
                "key": "nuget.org",
                "value": NUGET_INDEX,
                "protocolVersion": "3",
            }
            or list(source)
        ):
            raise ValueError("nuget.org must be the only package source")
    except (OSError, ValueError, ElementTree.ParseError):
        return ["NuGet.Config: only the canonical nuget.org source is permitted"]
    return []


def canonical_tool_declaration(name, declaration, expected) -> bool:
    commands = declaration.get("commands") if isinstance(declaration, dict) else None
    return not (
        not isinstance(name, str)
        or re.fullmatch(SAFE_NAME, name) is None
        or expected is None
        or expected["name"] != name
        or not isinstance(declaration, dict)
        or set(declaration) != {"version", "commands"}
        or declaration.get("version") != expected["version"]
        or not isinstance(commands, list)
        or not commands
        or len(commands) != len(set(commands))
        or any(
            not isinstance(command, str) or re.fullmatch(SAFE_NAME, command) is None
            for command in commands
        )
    )


def validate_dotnet_tools(root: Path, files: list[Path], inventory: list[dict]) -> list[str]:
    errors = []
    pins = {
        item["name"].casefold(): item
        for item in inventory
        if isinstance(item, dict)
        and item.get("kind") == "dotnet-tool"
        and isinstance(item.get("name"), str)
    }
    manifests = [path for path in files if path.name.casefold() == "dotnet-tools.json"]
    expected_path = root / ".config" / "dotnet-tools.json"
    if not pins and not manifests:
        return []
    if manifests != [expected_path]:
        return [".config/dotnet-tools.json: exactly one repository-root dotnet tool manifest is required"]
    try:
        payload = load_json(expected_path.read_text(encoding="utf-8"))
        if (
            not isinstance(payload, dict)
            or set(payload) != {"version", "isRoot", "tools"}
            or type(payload["version"]) is not int
            or payload["version"] != 1
            or payload["isRoot"] is not True
            or not isinstance(payload["tools"], dict)
        ):
            raise ValueError("tool manifest root is not canonical")
        consumed = set()
        for name, declaration in payload["tools"].items():
            key = name.casefold() if isinstance(name, str) else ""
            if not canonical_tool_declaration(name, declaration, pins.get(key)):
                errors.append(f".config/dotnet-tools.json: dotnet tool {name} differs from the inventory")
                continue
            consumed.add(key)
        for key in set(pins) - consumed:
            errors.append(f".config/dotnet-tools.json: dotnet tool {pins[key]['name']} is missing")
    except (OSError, KeyError, TypeError, ValueError):
        errors.append(".config/dotnet-tools.json: unable to parse canonical dotnet tool manifest")
    return errors


def msbuild_name(value: object) -> str:
    return value.rsplit("}", 1)[-1].casefold() if isinstance(value, str) else ""


def msbuild_attributes(element: ElementTree.Element) -> dict[str, str]:
    attributes = {}
    for raw_name, value in element.attrib.items():
        if "}" in raw_name:
            raise ValueError("namespaced MSBuild attribute")
        name = msbuild_name(raw_name)
        if name in attributes:
            raise ValueError("case-insensitive duplicate MSBuild attribute")
        attributes[name] = value
    return attributes


def workflow_uses_errors(location: str, line: str, pins) -> list[str]:
    action = CANONICAL_USES.fullmatch(line)
    if action is None:
        return [f"{location}: uses must use the canonical form and a full commit SHA"]
    name = action.group("name")
    sha = action.group("sha")
    repository = "/".join(name.split("/")[:2])
    expected = pins.get(repository)
    if expected is None or expected.get("kind") != "github-action":
        return [f"{location}: action {name} is absent from the inventory"]
    if expected["digest_or_sha"].lower() != sha.lower():
        return [f"{location}: action {name} differs from the inventory"]
    return []


def dockerfile_from_errors(location: str, image, pins) -> list[str]:
    reference = image.group(1)
    if "@sha256:" not in reference:
        return [f"{location}: FROM must contain @sha256:"]
    image_reference, digest = reference.split("@", 1)
    if ":" not in image_reference or SHA256.fullmatch(digest) is None:
        return [f"{location}: FROM is not canonical"]
    image_name, tag = image_reference.rsplit(":", 1)
    expected = pins.get(image_name)
    if expected is None or expected.get("kind") != "container":
        return [f"{location}: container {image_name} is absent from the inventory"]
    if expected["version"] != tag:
        return [f"{location}: container {image_name} tag differs from the inventory"]
    if expected["digest_or_sha"].lower() != digest.lower():
        return [f"{location}: container {image_name} differs from the inventory"]
    return []


def folded_lock_entries(relative, entries) -> tuple[dict, list[str]]:
    folded_entries = {}
    errors = []
    for name, record in entries.items():
        if not isinstance(name, str):
            raise ValueError("lock package name is not a string")
        key = name.casefold()
        if key in folded_entries:
            errors.append(f"{relative}: {name} has a case-insensitive duplicate")
        folded_entries[key] = (name, record)
        if not isinstance(record, dict) or record.get("type") not in {
            "Direct",
            "Transitive",
            "CentralTransitive",
            "Project",
        }:
            errors.append(f"{relative}: {name} has an unknown lock role")
        elif record["type"] != "Project" and (
            not isinstance(record.get("resolved"), str)
            or not is_canonical_sha512(record.get("contentHash"))
        ):
            errors.append(f"{relative}: {name} is not fully content-locked")
    return folded_entries, errors


def expected_lock_errors(relative, group_name: str, expected: dict, folded_entries: dict) -> list[str]:
    errors = []
    for key, item in expected.items():
        locked = folded_entries.get(key)
        if locked is None:
            errors.append(f"{relative}: {item['name']} is missing from {group_name}")
            continue
        errors.extend(
            validate_lock_entry(
                relative,
                item["name"],
                locked[1],
                (item["version"], item["lock_content_hash"]),
            )
        )
    return errors


def central_element_errors(element, local_tag: str, parents, properties) -> list[str]:
    errors = []
    if any(msbuild_name(attribute) == "condition" for attribute in element.attrib):
        errors.append("Directory.Packages.props: conditional declarations are not permitted")
    if local_tag in {"choose", "when", "otherwise", "import"}:
        errors.append(f"Directory.Packages.props: {local_tag} declarations are not permitted")
    if local_tag not in properties:
        return errors
    parent = parents.get(element)
    if (
        "}" in element.tag
        or parent is None
        or "}" in parent.tag
        or msbuild_name(parent.tag) != "propertygroup"
        or element.attrib
    ):
        errors.append(
            f"Directory.Packages.props: {local_tag} must be an unconditioned PropertyGroup value"
        )
    properties[local_tag].append(element.text)
    return errors


def package_version_errors(element, parents, central, nuget_pins) -> list[str]:
    parent = parents.get(element)
    attributes = msbuild_attributes(element)
    if (
        "}" in element.tag
        or parent is None
        or "}" in parent.tag
        or msbuild_name(parent.tag) != "itemgroup"
        or set(attributes) != {"include", "version"}
        or list(element)
    ):
        return ["Directory.Packages.props: PackageVersion must be a canonical unnamespaced ItemGroup child"]
    name = attributes["include"]
    version = attributes["version"]
    key = name.casefold()
    if key in central:
        return [f"Directory.Packages.props: {name} is declared more than once"]
    central[key] = (name, version)
    expected = nuget_pins.get(key)
    if expected is None or expected["name"] != name or expected["version"] != version:
        return [f"Directory.Packages.props: {name} differs from the inventory"]
    return []


def msbuild_tag_errors(relative, folded_tag: str, forbidden_msbuild_properties) -> list[str]:
    errors = []
    if folded_tag in forbidden_msbuild_properties:
        errors.append(f"{relative}: central package and restore policy overrides are not permitted")
    if folded_tag == "import":
        errors.append(f"{relative}: explicit Import declarations are not permitted")
    if folded_tag == "packageversion":
        errors.append(f"{relative}: PackageVersion is only permitted in Directory.Packages.props")
    if folded_tag in {"packagedownload", "globalpackagereference"}:
        errors.append(f"{relative}: {folded_tag} is not permitted")
    return errors


def has_conditional_ancestor(element, parents) -> bool:
    ancestor = element
    while ancestor is not None:
        if any(msbuild_name(attribute) == "condition" for attribute in ancestor.attrib):
            return True
        ancestor = parents.get(ancestor)
    return False


def canonical_package_reference(element, parent, attributes, child_names, conditional, name) -> bool:
    attribute_names = set(attributes)
    return not (
        "}" in element.tag
        or parent is None
        or "}" in parent.tag
        or msbuild_name(parent.tag) != "itemgroup"
        or conditional
        or not isinstance(name, str)
        or re.fullmatch(SAFE_NAME, name) is None
        or "update" in attribute_names
        or "remove" in attribute_names
        or "version" in attribute_names
        or "versionoverride" in attribute_names
        or "version" in child_names
        or "versionoverride" in child_names
    )


def package_reference_errors(relative, element, parents, references, nuget_pins, central, all_references) -> list[str]:
    attributes = msbuild_attributes(element)
    child_names = {msbuild_name(child.tag) for child in element if isinstance(child.tag, str)}
    name: str = attributes.get("include", "")
    if not canonical_package_reference(
        element, parents.get(element), attributes, child_names, has_conditional_ancestor(element, parents), name
    ):
        return [f"{relative}: PackageReference must be versionless, unconditional, and canonical"]
    key = name.casefold()
    if key in references:
        return [f"{relative}: PackageReference {name} is duplicated"]
    expected = nuget_pins.get(key)
    if expected is None or expected["name"] != name or key not in central:
        return [f"{relative}: PackageReference {name} differs from the central inventory"]
    references[key] = expected
    all_references.add(key)
    return []


def runtime_identifier_values(relative, runtime_identifiers) -> tuple[tuple[str, ...], list[str]]:
    if not runtime_identifiers:
        return (), []
    if len(runtime_identifiers) != 1 or not isinstance(runtime_identifiers[0], str):
        return (), [f"{relative}: RuntimeIdentifiers must be a single canonical value"]
    values = tuple(runtime_identifiers[0].split(";"))
    if not values or len(set(values)) != len(values) or any(
        re.fullmatch(SAFE_NAME, value) is None for value in values
    ):
        return (), [f"{relative}: RuntimeIdentifiers is not canonical"]
    return values, []


def central_property_errors(properties: dict[str, list[str | None]]) -> list[str]:
    errors = []
    if properties["managepackageversionscentrally"] != ["true"]:
        errors.append(
            "Directory.Packages.props: ManagePackageVersionsCentrally must be exactly true"
        )
    if properties["centralpackageversionoverrideenabled"] != ["false"]:
        errors.append(
            "Directory.Packages.props: CentralPackageVersionOverrideEnabled must be exactly false"
        )
    return errors


def central_element_scan(
    package_root, parents: dict, central: dict, nuget_pins: dict, properties: dict
) -> list[str]:
    errors = []
    for element in package_root.iter():
        if not isinstance(element.tag, str):
            continue
        local_tag = msbuild_name(element.tag)
        errors.extend(central_element_errors(element, local_tag, parents, properties))
        if local_tag == "packageversion":
            errors.extend(package_version_errors(element, parents, central, nuget_pins))
    return errors


def central_package_errors(central_path: Path, nuget_pins: dict, central: dict) -> list[str]:
    if not central_path.is_file():
        return ["Directory.Packages.props is required"]
    errors = []
    try:
        package_root = ElementTree.parse(central_path).getroot()
        if "}" in package_root.tag or msbuild_name(package_root.tag) != "project":
            raise ValueError("Project root is not canonical")
        parents = {child: parent for parent in package_root.iter() for child in parent}
        properties: dict[str, list[str | None]] = {
            "managepackageversionscentrally": [],
            "centralpackageversionoverrideenabled": [],
        }
        errors.extend(
            central_element_scan(package_root, parents, central, nuget_pins, properties)
        )
        errors.extend(central_property_errors(properties))
        errors.extend(
            f"Directory.Packages.props: {expected['name']} is missing"
            for key, expected in nuget_pins.items()
            if key not in central
        )
    except (OSError, ValueError, ElementTree.ParseError):
        errors.append("Directory.Packages.props: unable to parse canonical XML")
    return errors


def scan_project_elements(
    errors: list[str],
    relative: Path,
    project_root,
    parents: dict,
    nuget_pins: dict,
    central: dict,
    all_references: set,
    is_project_file: bool,
) -> tuple[dict, list, list]:
    """Collect the declarations of one MSBuild file, appending its errors as they are found."""
    references: dict[str, dict] = {}
    publish_aot: list = []
    runtime_identifiers: list = []
    for element in project_root.iter():
        if not isinstance(element.tag, str):
            continue
        folded_tag = msbuild_name(element.tag)
        errors.extend(msbuild_tag_errors(relative, folded_tag, FORBIDDEN_MSBUILD_PROPERTIES))
        if folded_tag == "publishaot":
            publish_aot.append(element.text)
        if folded_tag == "runtimeidentifiers":
            runtime_identifiers.append(element.text)
        if folded_tag != "packagereference":
            continue
        if not is_project_file:
            errors.append(f"{relative}: PackageReference is only permitted in project files")
            continue
        errors.extend(
            package_reference_errors(
                relative, element, parents, references, nuget_pins, central, all_references
            )
        )
    return references, publish_aot, runtime_identifiers


def project_declaration_errors(
    root: Path, path: Path, nuget_pins: dict, central: dict, all_references: set
) -> tuple[list[str], tuple | None]:
    relative = path.relative_to(root)
    is_project_file = path.suffix.casefold() == CSPROJ_SUFFIX
    errors: list[str] = []
    try:
        project_root = ElementTree.parse(path).getroot()
        if "}" in project_root.tag or msbuild_name(project_root.tag) != "project":
            raise ValueError("Project root is not canonical")
        parents = {child: parent for parent in project_root.iter() for child in parent}
        references, publish_aot, runtime_identifiers = scan_project_elements(
            errors, relative, project_root, parents, nuget_pins, central, all_references, is_project_file
        )
        aot = publish_aot == ["true"]
        if publish_aot and publish_aot not in (["true"], ["false"]):
            errors.append(f"{relative}: PublishAot must be a single canonical boolean")
        rids, rid_errors = runtime_identifier_values(relative, runtime_identifiers)
        errors.extend(rid_errors)
        return errors, ((references, aot, rids) if is_project_file else None)
    except (OSError, ValueError, ElementTree.ParseError):
        errors.append(f"{relative}: unable to parse canonical project XML")
        return errors, None


def validate_package_declarations(
    root: Path, files: list[Path], inventory: list[dict]
) -> tuple[list[str], dict[Path, tuple[dict[str, dict], bool, tuple[str, ...]]]]:
    nuget_pins = {
        item["name"].casefold(): item
        for item in inventory
        if isinstance(item, dict)
        and item.get("kind") == "nuget"
        and "nuget_role" not in item
        and isinstance(item.get("name"), str)
    }
    central_path = root / "Directory.Packages.props"
    central: dict = {}
    errors = central_package_errors(central_path, nuget_pins, central)

    projects = {}
    all_references = set()
    xml_files = [
        path
        for path in files
        if path.suffix.casefold() in {CSPROJ_SUFFIX, ".props", ".targets"}
        and path != central_path
    ]
    for path in xml_files:
        file_errors, project = project_declaration_errors(
            root, path, nuget_pins, central, all_references
        )
        errors.extend(file_errors)
        if project is not None:
            projects[path] = project
    if all_references != set(nuget_pins):
        errors.append(
            "PackageReference declarations must use every NuGet inventory package exactly through central management"
        )
    return errors, projects


def validate_lock_entry(
    relative: Path, name: str, record: object, expected: tuple[str, str]
) -> list[str]:
    version, content_hash = expected
    if not isinstance(record, dict) or (
        record.get("type") != "Direct"
        or record.get("requested") != f"[{version}, )"
        or record.get("resolved") != version
        or record.get("contentHash") != content_hash
    ):
        return [f"{relative}: {name} direct lock entry differs from the inventory"]
    return []


def implicit_pins_for_group(
    group_name: str, is_base: bool, aot: bool, expected_rid_groups: set, sdk_pins: dict, rid_sdk_pins: dict
) -> dict:
    if not aot:
        return {}
    if is_base:
        return sdk_pins
    return rid_sdk_pins if group_name in expected_rid_groups else {}


def lock_group_errors(
    relative: Path,
    group_name: str,
    entries: dict,
    references: dict,
    implicit: dict,
) -> list[str]:
    folded_entries, errors = folded_lock_entries(relative, entries)
    expected_direct = references if "/" not in group_name else {}
    errors.extend(expected_lock_errors(relative, group_name, expected_direct, folded_entries))
    errors.extend(expected_lock_errors(relative, group_name, implicit, folded_entries))
    permitted_direct = set(expected_direct) | set(implicit)
    errors.extend(
        f"{relative}: unexpected direct dependency {name}"
        for key, (name, record) in folded_entries.items()
        if isinstance(record, dict) and record.get("type") == "Direct" and key not in permitted_direct
    )
    return errors


def canonical_lock_groups(lock_path: Path) -> dict:
    payload = load_json(lock_path.read_text(encoding="utf-8"))
    if (
        not isinstance(payload, dict)
        or set(payload) != {"version", "dependencies"}
        or type(payload["version"]) is not int
        or payload["version"] != 2
        or not isinstance(payload["dependencies"], dict)
    ):
        raise ValueError("lock root is not canonical")
    return payload["dependencies"]


def project_lock_errors(
    relative: Path,
    lock_path: Path,
    references: dict,
    aot: bool,
    rids: tuple[str, ...],
    sdk_pins: dict,
    rid_sdk_pins: dict,
    consumed_sdk_pins: set,
) -> list[str]:
    errors: list[str] = []
    try:
        groups = canonical_lock_groups(lock_path)
        base_groups = [name for name in groups if isinstance(name, str) and "/" not in name]
        if not base_groups:
            raise ValueError("lock has no base target framework")
        expected_rid_groups = {
            f"{base}/{rid}" for base in base_groups for rid in rids
        } if aot else set()
        actual_rid_groups = {name for name in groups if isinstance(name, str) and "/" in name}
        if aot and actual_rid_groups != expected_rid_groups:
            errors.append(f"{relative}: runtime-specific lock groups differ from RuntimeIdentifiers")
        for group_name, entries in groups.items():
            if not isinstance(group_name, str) or not isinstance(entries, dict):
                raise ValueError("lock dependency group is not canonical")
            implicit = implicit_pins_for_group(
                group_name, "/" not in group_name, aot, expected_rid_groups, sdk_pins, rid_sdk_pins
            )
            consumed_sdk_pins.update(implicit)
            errors.extend(lock_group_errors(relative, group_name, entries, references, implicit))
    except (OSError, KeyError, TypeError, ValueError, ElementTree.ParseError):
        errors.append(f"{relative}: unable to parse canonical packages.lock.json")
    return errors


def validate_package_locks(
    root: Path,
    files: list[Path],
    projects: dict[Path, tuple[dict[str, dict], bool, tuple[str, ...]]],
    inventory: list[dict],
) -> list[str]:
    errors = []
    sdk_pins = {
        item["name"].casefold(): item
        for item in inventory
        if isinstance(item, dict)
        and item.get("kind") == "nuget"
        and isinstance(item.get("nuget_role"), str)
        and item.get("nuget_role") in NUGET_ROLES
        and isinstance(item.get("name"), str)
    }
    rid_sdk_pins = {
        key: item
        for key, item in sdk_pins.items()
        if item["nuget_role"] == "sdk-aot-base-rid"
    }
    consumed_sdk_pins = set()
    expected_locks = {path.parent / NUGET_LOCK_FILE for path in projects}
    actual_locks = {path for path in files if path.name == NUGET_LOCK_FILE}
    for path in sorted(expected_locks - actual_locks):
        errors.append(f"{path.relative_to(root)} is required for its project")
    for path in sorted(actual_locks - expected_locks):
        errors.append(f"{path.relative_to(root)} is orphaned from any project")
    for project, (references, aot, rids) in projects.items():
        lock_path = project.parent / NUGET_LOCK_FILE
        if lock_path not in actual_locks:
            continue
        errors.extend(
            project_lock_errors(
                lock_path.relative_to(root), lock_path, references, aot, rids,
                sdk_pins, rid_sdk_pins, consumed_sdk_pins,
            )
        )
    for key in set(sdk_pins) - consumed_sdk_pins:
        errors.append(f"{sdk_pins[key]['name']}: SDK NuGet role is unused by any AOT lock")
    return errors


def download_declaration_lines(lines: list[str], artifact_urls: set) -> list[tuple[int, str]]:
    return [
        (number, content)
        for number, line in enumerate(lines, start=1)
        if not (content := line.lstrip(" \t")).startswith("#")
        and (
            DOWNLOAD_COMMAND.search(content)
            or any(artifact_url in content for artifact_url in artifact_urls)
        )
    ]


def source_line_errors(
    relative: Path, lines: list[str], pins: dict, is_workflow: bool, is_dockerfile: bool
) -> list[str]:
    errors = []
    for number, line in enumerate(lines, start=1):
        content = line.lstrip(" \t")
        location = f"{relative}:{number}"
        if is_workflow and not content.startswith("#"):
            if content.startswith("?") or "\\" in content or YAML_QUOTED_KEY.search(content):
                errors.append(f"{location}: YAML mapping key is not canonical")
            if YAML_USES_WORD.search(content):
                errors.extend(workflow_uses_errors(location, line, pins))
        from_line = FROM_LINE.match(line) if is_dockerfile else None
        if from_line is not None:
            errors.extend(dockerfile_from_errors(location, from_line, pins))
    return errors


def source_file_errors(
    root: Path, path: Path, pins: dict, artifact_urls: set, inventory: list[dict]
) -> list[str]:
    suffix = path.suffix.casefold()
    filename = path.name.casefold()
    is_dockerfile = (
        filename == "dockerfile"
        or filename.startswith("dockerfile.")
        or filename.endswith(".dockerfile")
    )
    if suffix not in {".yml", ".yaml", ".sh"} and not is_dockerfile:
        return []
    with path.open(encoding="utf-8", errors="replace", newline="") as source_file:
        text = source_file.read()
    lines = text.split("\n")
    relative = path.relative_to(root)
    download_declarations = download_declaration_lines(lines, artifact_urls)
    errors = source_line_errors(
        relative, lines, pins, suffix in {".yml", ".yaml"}, is_dockerfile
    )
    if suffix == ".sh" and download_declarations:
        errors.extend(validate_download_script(path, root, text, inventory))
    elif suffix != ".sh":
        errors.extend(
            f"{relative}:{number}: download declarations are only permitted in canonical .sh shell scripts"
            for number, _ in download_declarations
        )
    return errors


def canonical_sdk_policy(payload) -> dict:
    if not isinstance(payload, dict) or set(payload) != {"sdk"}:
        raise ValueError("global root is not canonical")
    sdk = payload["sdk"]
    if (
        not isinstance(sdk, dict)
        or set(sdk) != {"version", "rollForward", "allowPrerelease"}
        or not isinstance(sdk["version"], str)
        or sdk["rollForward"] != "disable"
        or sdk["allowPrerelease"] is not False
    ):
        raise ValueError("SDK policy is not stable")
    return sdk


def global_json_errors(root: Path, pins: dict) -> list[str]:
    global_json = root / "global.json"
    if not global_json.is_file():
        return ["global.json is required"]
    try:
        sdk = canonical_sdk_policy(load_json(global_json.read_text(encoding="utf-8")))
        expected = pins.get("dotnet-sdk")
        if expected is None or expected.get("kind") != "dotnet-sdk":
            return ["global.json: SDK version differs from the inventory"]
        if expected["version"] != sdk["version"]:
            return ["global.json: SDK version differs from the inventory"]
    except (OSError, ValueError, KeyError, TypeError):
        return ["global.json: SDK version, rollForward, and allowPrerelease policy must be pinned"]
    return []


def validate_declarations(root: Path, inventory: list[dict]) -> list[str]:
    errors = []
    pins = inventory_by_name(inventory)
    files = structured_files(root)
    artifact_urls = {
        item["artifact_url"]
        for item in inventory
        if isinstance(item, dict)
        and item.get("kind") == "download"
        and isinstance(item.get("artifact_url"), str)
    }
    for path in files:
        errors.extend(source_file_errors(root, path, pins, artifact_urls, inventory))
    errors.extend(global_json_errors(root, pins))
    errors.extend(validate_nuget_config(root, files))
    errors.extend(validate_dotnet_tools(root, files, inventory))
    package_errors, projects = validate_package_declarations(root, files, inventory)
    errors.extend(package_errors)
    errors.extend(validate_package_locks(root, files, projects, inventory))
    return errors


def trusted_context() -> ssl.SSLContext:
    certificate_bundle = next(
        (path for path in (Path("/etc/ssl/cert.pem"),) if path.exists()),
        None,
    )
    context = ssl.create_default_context(
        cafile=str(certificate_bundle) if certificate_bundle else None
    )
    # create_default_context already negotiates TLS 1.2 or better on supported runtimes. Stating
    # the floor makes it independent of the interpreter's defaults.
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    return context


def request_headers(url: str, accept: str) -> dict[str, str]:
    headers = {"Accept": accept, "User-Agent": "perf-sentinel-hub-supply-chain-check"}
    if url.startswith("https://api.github.com/") and (token := os.environ.get("GITHUB_TOKEN")):
        headers["Authorization"] = f"Bearer {token}"
    return headers


def fetch_json(url: str) -> tuple[dict, dict]:
    request = Request(url, headers=request_headers(url, "application/json"))
    context = trusted_context()
    with urlopen(request, timeout=20, context=context) as response:  # nosec B310: sources are committed HTTPS inventory entries
        content = response.read()
        if response.headers.get("Content-Encoding") == "gzip" or content.startswith(b"\x1f\x8b"):
            content = gzip.decompress(content)
        return load_json(content.decode("utf-8")), dict(response.headers.items())


def fetch_manifest_digest(url: str) -> str | None:
    request = Request(
        url,
        headers=request_headers(url, "application/vnd.docker.distribution.manifest.list.v2+json"),
    )
    with urlopen(request, timeout=20, context=trusted_context()) as response:  # nosec B310: sources are committed HTTPS inventory entries
        return response.headers.get("Docker-Content-Digest")


def same_timestamp(left: object, right: object) -> bool:
    if not isinstance(left, str) or not isinstance(right, str):
        return False
    try:
        return parse_timestamp(left) == parse_timestamp(right)
    except ValueError:
        return False


def same_release_value(left: object, right: object) -> bool:
    left_is_date = bool(isinstance(left, str) and DATE_ONLY.fullmatch(left))
    right_is_date = bool(isinstance(right, str) and DATE_ONLY.fullmatch(right))
    if left_is_date or right_is_date:
        return left_is_date and right_is_date and left == right
    return same_timestamp(left, right)


def dotnet_container_release(item: dict, payload: dict) -> dict | None:
    base_version = item["version"].split("-", 1)[0]
    if item["name"] == "mcr.microsoft.com/dotnet/sdk":
        return next(
            (
                release
                for release in payload.get("releases", [])
                if release.get("sdk", {}).get("version") == base_version
            ),
            None,
        )
    if item["name"] == "mcr.microsoft.com/dotnet/runtime-deps":
        return next(
            (
                release
                for release in payload.get("releases", [])
                if release.get("release-version") == base_version
                and release.get("runtime", {}).get("version") == base_version
            ),
            None,
        )
    return None


def release_metadata_errors(item: dict, release: dict) -> list[str]:
    errors = []
    prerelease = release.get("prerelease")
    draft = release.get("draft")
    if type(prerelease) is not bool:
        errors.append(f"{item['name']}: official release prerelease must be a required boolean")
    if type(draft) is not bool:
        errors.append(f"{item['name']}: official release draft must be a required boolean")
    if prerelease is True or draft is True:
        errors.append(f"{item['name']}: official release is prerelease or draft")
    tag_name = release.get("tag_name")
    if not isinstance(tag_name, str):
        errors.append(f"{item['name']}: official release tag_name is required")
    elif tag_name.lstrip("v") != str(item["version"]).lstrip("v"):
        errors.append(f"{item['name']}: official release tag moved or differs from inventory")
    published_at = release.get("published_at")
    if not isinstance(published_at, str):
        errors.append(f"{item['name']}: official release published_at is required")
    elif not same_timestamp(published_at, item["released_at"]):
        errors.append(f"{item['name']}: official release timestamp differs from inventory")
    return errors


def release_asset_errors(item: dict, release: dict) -> list[str]:
    assets = release.get("assets")
    artifact = next(
        (
            asset
            for asset in assets
            if isinstance(asset, dict)
            and asset.get("browser_download_url") == item["artifact_url"]
        ),
        None,
    ) if isinstance(assets, list) else None
    if artifact is None or not isinstance(artifact.get("digest"), str):
        return [f"{item['name']}: publisher did not provide the recorded download artifact checksum"]
    if artifact["digest"].lower() != item["digest_or_sha"].lower():
        return [f"{item['name']}: publisher checksum differs from inventory"]
    return []


def github_release_errors(item: dict, match, cached_json) -> list[str]:
    owner, repository, tag = match.groups()
    release, _ = cached_json(f"https://api.github.com/repos/{owner}/{repository}/releases/tags/{tag}")
    errors = release_metadata_errors(item, release)
    if item["kind"] in {"github-action", "github-release"}:
        commit, _ = cached_json(f"https://api.github.com/repos/{owner}/{repository}/commits/{tag}")
        if commit.get("sha", "").lower() != item["digest_or_sha"].lower():
            errors.append(f"{item['name']}: official release commit moved")
    if item["kind"] == "download":
        errors.extend(release_asset_errors(item, release))
    return errors


def container_errors(item: dict, source: str, cached_json) -> list[str]:
    if urlsplit(source).netloc == "hub.docker.com":
        tag, _ = cached_json(source)
        errors = []
        if tag.get("name") != item["version"]:
            errors.append(f"{item['name']}: Docker Hub tag differs from inventory")
        if tag.get("tag_status") != "active":
            errors.append(f"{item['name']}: Docker Hub tag is not active")
        if str(tag.get("digest", "")).lower() != item["digest_or_sha"].lower():
            errors.append(f"{item['name']}: Docker Hub manifest digest moved")
        if not same_timestamp(tag.get("last_updated"), item["released_at"]):
            errors.append(f"{item['name']}: Docker Hub release timestamp differs from inventory")
        return errors
    errors = []
    digest = fetch_manifest_digest(source)
    if digest is None:
        errors.append(f"{item['name']}: registry did not provide Docker-Content-Digest")
    elif digest.lower() != item["digest_or_sha"].lower():
        errors.append(f"{item['name']}: container manifest digest moved")
    payload, _ = cached_json(DOTNET_RELEASES)
    release = dotnet_container_release(item, payload)
    if release is None:
        errors.append(f"{item['name']}: .NET metadata does not contain the container version")
        return errors
    release_date = release.get("release-date")
    if not isinstance(release_date, str):
        errors.append(f"{item['name']}: official container release date is required")
    elif not same_release_value(release_date, item["released_at"]):
        errors.append(f"{item['name']}: official container release date differs from inventory")
    return errors


def nuget_errors(item: dict, source: str, cached_json) -> list[str]:
    payload, _ = cached_json(source)
    errors = []
    if payload.get("listed") is not True:
        errors.append(f"{item['name']}: NuGet registration listed status must be true")
    if not same_timestamp(payload.get("published"), item["released_at"]):
        errors.append(f"{item['name']}: NuGet release timestamp differs from inventory")
    catalog = payload.get("catalogEntry")
    if isinstance(catalog, str):
        parsed_catalog = urlsplit(catalog)
        if not (
            parsed_catalog.scheme == "https"
            and parsed_catalog.netloc == "api.nuget.org"
            and parsed_catalog.path.startswith("/v3/catalog0/data/")
            and parsed_catalog.query == ""
            and parsed_catalog.fragment == ""
        ):
            errors.append(f"{item['name']}: NuGet catalog source is not official")
            catalog = None
        else:
            catalog, _ = cached_json(catalog)
    if not isinstance(catalog, dict):
        errors.append(f"{item['name']}: NuGet catalog entry is required")
        catalog = {}
    catalog_id = catalog.get("id")
    if not isinstance(catalog_id, str) or catalog_id.casefold() != item["name"].casefold():
        errors.append(f"{item['name']}: NuGet catalog identity differs from inventory")
    if catalog.get("version") != item["version"]:
        errors.append(f"{item['name']}: NuGet version differs from inventory")
    if catalog.get("listed") is not True:
        errors.append(f"{item['name']}: NuGet catalog listed status must be true")
    if not same_timestamp(catalog.get("published"), item["released_at"]):
        errors.append(f"{item['name']}: NuGet release timestamp differs from inventory")
    if (
        catalog.get("packageHashAlgorithm") != "SHA512"
        or not isinstance(catalog.get("packageHash"), str)
        or f"sha512-base64:{catalog['packageHash']}" != item["digest_or_sha"]
    ):
        errors.append(f"{item['name']}: NuGet checksum differs from inventory")
    return errors


def dotnet_sdk_errors(item: dict, source: str, cached_json) -> list[str]:
    payload, _ = cached_json(source)
    release = next(
        (candidate for candidate in payload.get("releases", []) if candidate.get("sdk", {}).get("version") == item["version"]),
        None,
    )
    if release is None:
        return [f"{item['name']}: .NET metadata does not contain the pinned SDK"]
    errors = []
    if not same_release_value(release.get("release-date"), item["released_at"]):
        errors.append(f"{item['name']}: .NET metadata release timestamp differs from inventory")
    artifact = next(
        (file for file in release.get("sdk", {}).get("files", []) if file.get("url") == item.get("artifact_url")),
        None,
    )
    if artifact is None or f"sha512:{artifact.get('hash', '')}" != item["digest_or_sha"]:
        errors.append(f"{item['name']}: .NET metadata digest differs from inventory")
    return errors


def online_item_errors(item: dict, cached_json) -> list[str]:
    source = item["source"]
    match = GITHUB_RELEASE.fullmatch(source)
    if match:
        return github_release_errors(item, match, cached_json)
    if item["kind"] == "container":
        return container_errors(item, source, cached_json)
    if item["kind"] in ("dotnet-tool", "nuget"):
        return nuget_errors(item, source, cached_json)
    if item["kind"] == "dotnet-sdk":
        return dotnet_sdk_errors(item, source, cached_json)
    return [f"{item['name']}: unsupported official source"]


def validate_online(inventory: list[dict]) -> list[str]:
    errors = []
    json_cache = {}

    def cached_json(url: str) -> tuple[dict, dict]:
        if url not in json_cache:
            json_cache[url] = fetch_json(url)
        return json_cache[url]

    for item in inventory:
        try:
            errors.extend(online_item_errors(item, cached_json))
        except (AttributeError, URLError, ValueError, TypeError, TimeoutError) as error:
            errors.append(f"{item['name']}: unable to verify official source: {error}")
    return errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--online", action="store_true", help="verify committed pins against official release endpoints")
    arguments = parser.parse_args(argv)
    root = Path.cwd()
    inventory_path = root / "config" / "supply-chain.json"
    if not inventory_path.exists():
        print("config/supply-chain.json is required", file=sys.stderr)
        return 1
    try:
        payload = load_json(inventory_path.read_text(encoding="utf-8"))
        if set(payload) != {"schema_version", "inventory"}:
            raise ValueError("only schema_version and inventory are permitted")
        inventory = payload["inventory"]
        if type(payload.get("schema_version")) is not int or payload["schema_version"] != 1 or not isinstance(inventory, list):
            raise ValueError("schema_version must be 1 and inventory must be an array")
    except (OSError, ValueError, KeyError, TypeError) as error:
        print(f"invalid config/supply-chain.json: {error}", file=sys.stderr)
        return 1

    errors = validate_inventory(inventory) + validate_declarations(root, inventory)
    if arguments.online and not errors:
        errors.extend(validate_online(inventory))
    for error in errors:
        print(error, file=sys.stderr)
    return int(bool(errors))


if __name__ == "__main__":
    raise SystemExit(main())
