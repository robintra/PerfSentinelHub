#!/usr/bin/env python3
"""Fail closed when repository supply-chain declarations drift from the inventory."""

from __future__ import annotations

import argparse
import base64
import binascii
import gzip
import json
import os
import re
import ssl
import sys
import xml.etree.ElementTree as ElementTree
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.error import URLError
from urllib.parse import urlsplit
from urllib.request import Request, urlopen


REQUIRED_FIELDS = {
    "name",
    "kind",
    "version",
    "digest_or_sha",
    "released_at",
    "source",
    "stabilization_exempt",
    "reason",
}
ALLOWED_FIELDS = REQUIRED_FIELDS | {"artifact_url", "expiry"}
KNOWN_KINDS = frozenset({"container", "dotnet-sdk", "download", "github-action", "github-release", "nuget"})
ACTION_SHA = re.compile(r"^[0-9a-f]{40}$", re.IGNORECASE)
SHA256 = re.compile(r"^sha256:[0-9a-f]{64}$")
SEMVER_PRERELEASE = re.compile(r"^v?\d+(?:\.\d+){1,3}-[0-9A-Za-z.-]+(?:\+[0-9A-Za-z.-]+)?$")
FROM_LINE = re.compile(r"^\s*FROM\s+(?:--platform=\S+\s+)?([^\s]+)", re.IGNORECASE)
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
    rf"/usr/bin/curl -q -fsSL (?P<url>{GITHUB_ARTIFACT_URL}) -o (?P<output>{SAFE_RELATIVE_PATH})"
)
CANONICAL_USES = re.compile(
    rf"^\s+- uses: (?P<name>{SAFE_RELATIVE_PATH})@(?P<sha>[0-9A-Fa-f]{{40}})(?: # {SAFE_NAME})?$"
)
YAML_USES_WORD = re.compile(r"(?<![A-Za-z0-9_-])['\"]?uses['\"]?(?![A-Za-z0-9_-])", re.IGNORECASE)
YAML_QUOTED_KEY = re.compile(r"(?:^|[{,])\s*['\"][^'\"]+['\"]\s*:")
NETWORK_MARKERS = (
    "curl",
    "wget",
    "ghreleasedownload",
    "ghrundownload",
    "ghrepoclone",
    "ghapi",
    "gitclone",
    "gitfetch",
    "gitpull",
    "pipdownload",
    "pipinstall",
    "urllib",
    "urlopen",
    "requestsget",
    "requestspost",
    "httpx",
    "aiohttp",
    "subprocess",
    "invokewebrequest",
    "invokerestmethod",
    "startbitstransfer",
    "downloadfile",
    "downloadstring",
    "httpclient",
    "webclient",
    "openread",
    "devtcp",
    "https",
    "releasesdownload",
)
DOTNET_SHA512 = re.compile(r"^sha512:[0-9a-f]{128}$")
NUGET_SHA512 = re.compile(r"^sha512-base64:[A-Za-z0-9+/]+={0,2}$")
DOTNET_RELEASES = "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json"


def parse_timestamp(value: str) -> datetime:
    if not isinstance(value, str):
        raise ValueError("timestamp is not a string")
    parsed = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if parsed.tzinfo is None or parsed.utcoffset() != timedelta(0):
        raise ValueError("timestamp is not UTC")
    return parsed.astimezone(timezone.utc)


def unique_object(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def load_json(text: str):
    return json.loads(text, object_pairs_hook=unique_object)


def text_files(root: Path):
    ignored = {".git", "bin", "obj", "__pycache__"}
    suffixes = {".yml", ".yaml", ".sh", ".py", ".ps1", ".psm1"}
    for path in root.rglob("*"):
        if not path.is_file() or any(part in ignored for part in path.parts):
            continue
        filename = path.name.casefold()
        if (
            filename == "dockerfile"
            or filename.startswith("dockerfile.")
            or filename.endswith(".dockerfile")
            or path.suffix.casefold() in suffixes
        ):
            yield path


def inventory_by_name(inventory: list[dict]) -> dict[str, dict]:
    return {
        item["name"]: item
        for item in inventory
        if isinstance(item, dict) and isinstance(item.get("name"), str)
    }


def has_network_marker(line: str, suffix: str = "") -> bool:
    compact = re.sub(r"[^0-9A-Za-z]", "", line).lower()
    markers = NETWORK_MARKERS
    if suffix == ".py":
        markers += ("requests", "httpclient", "socket", "ftplib")
    return any(marker in compact for marker in markers)


def is_canonical_nuget_digest(value: object) -> bool:
    if not isinstance(value, str) or not NUGET_SHA512.fullmatch(value):
        return False
    encoded = value.removeprefix("sha512-base64:")
    try:
        decoded = base64.b64decode(encoded, validate=True)
    except (ValueError, binascii.Error):
        return False
    return len(decoded) == 64 and base64.b64encode(decoded).decode("ascii") == encoded


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
    match = re.fullmatch(rf"/v2/(?P<repository>{SAFE_RELATIVE_PATH})/manifests/(?P<tag>{SAFE_VERSION})", source.path)
    return bool(
        source.scheme == "https"
        and source.netloc == "mcr.microsoft.com"
        and match
        and item["name"] == f"mcr.microsoft.com/{match.group('repository')}"
        and item["version"] == match.group("tag")
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
    if kind == "nuget":
        return nuget_source_matches(item)
    if kind == "container":
        return container_source_matches(item)
    return bool(GITHUB_RELEASE.fullmatch(source))


def validate_inventory(inventory: list[dict], now: datetime) -> list[str]:
    errors = []
    name_keys = set()
    artifact_keys = set()
    for item in inventory:
        name = item.get("name", "<unnamed>") if isinstance(item, dict) else "<invalid>"
        if not isinstance(item, dict):
            errors.append("inventory contains a non-object item")
            continue
        missing = REQUIRED_FIELDS - item.keys()
        if missing:
            errors.append(f"{name}: missing required fields: {', '.join(sorted(missing))}")
            continue
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
        if kind != "container" and SEMVER_PRERELEASE.fullmatch(str(item["version"])):
            errors.append(f"{name}: prerelease versions are not permitted")
        try:
            released_at = parse_timestamp(item["released_at"])
            age = now - released_at
        except (TypeError, ValueError):
            errors.append(f"{name}: released_at must be an ISO-8601 UTC timestamp")
            continue
        exempt = item["stabilization_exempt"]
        if type(exempt) is not bool:
            errors.append(f"{name}: stabilization_exempt must be a boolean")
            exempt = False
        if exempt:
            try:
                expiry = parse_timestamp(item.get("expiry"))
                if expiry <= now or expiry > now + timedelta(days=90):
                    raise ValueError("expiry is outside the permitted window")
            except (TypeError, ValueError):
                errors.append(f"{name}: stabilization expiry must be a future UTC timestamp within 90 days")
        elif age < timedelta(hours=72):
            errors.append(f"{name}: ordinary releases must be at least 72 hours old")
        if kind == "github-release" and not ACTION_SHA.fullmatch(str(item["digest_or_sha"])):
            errors.append(f"{name}: GitHub releases require a raw release commit SHA")
        if kind == "github-action" and not ACTION_SHA.fullmatch(str(item["digest_or_sha"])):
            errors.append(f"{name}: GitHub Actions require a full commit SHA")
        if (kind == "container" or kind == "download") and not SHA256.fullmatch(str(item["digest_or_sha"])):
            errors.append(f"{name}: downloaded tools and containers require a sha256 checksum")
        if kind == "dotnet-sdk" and not DOTNET_SHA512.fullmatch(str(item["digest_or_sha"])):
            errors.append(f"{name}: .NET SDK artifacts require a sha512 checksum")
        if kind == "nuget" and not is_canonical_nuget_digest(item["digest_or_sha"]):
            errors.append(f"{name}: NuGet packages require a sha512-base64 checksum")
        if kind == "dotnet-sdk":
            artifact = urlsplit(str(item.get("artifact_url", "")))
            if not (
                artifact.scheme == "https"
                and artifact.netloc == "builds.dotnet.microsoft.com"
                and artifact.path.startswith(f"/dotnet/Sdk/{item['version']}/")
                and artifact.query == ""
                and artifact.fragment == ""
            ):
                errors.append(f"{name}: .NET SDK artifact_url must match the inventory version")
        if kind == "download":
            artifact_url = str(item.get("artifact_url", ""))
            artifact_match = GITHUB_ARTIFACT.fullmatch(artifact_url)
            if artifact_match is None:
                errors.append(f"{name}: downloaded tools require an official artifact_url")
            else:
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
                    artifact_key[:2]
                    != (source_match.group(1).casefold(), source_match.group(2).casefold())
                    or artifact_key[2] != source_match.group(3)
                ):
                    errors.append(f"{name}: artifact_url must belong to its source release")
        match = GITHUB_RELEASE.fullmatch(str(item["source"]))
        if match and match.group(3).lstrip("v") != str(item["version"]).lstrip("v"):
            errors.append(f"{name}: source release tag must match the inventory version")
        if kind == "github-action" and match and str(item["name"]).lower() != f"{match.group(1)}/{match.group(2)}".lower():
            errors.append(f"{name}: action name must match its owner/repository source")
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


def validate_declarations(root: Path, inventory: list[dict]) -> list[str]:
    errors = []
    pins = inventory_by_name(inventory)
    trusted_network_files = {
        Path(__file__).resolve(),
        (Path(__file__).parent / "tests" / "test_check_supply_chain.py").resolve(),
    }
    for path in text_files(root):
        with path.open(encoding="utf-8", errors="replace", newline="") as source_file:
            text = source_file.read()
        lines = text.split("\n")
        suffix = path.suffix.casefold()
        filename = path.name.casefold()
        is_workflow = suffix in {".yml", ".yaml"}
        is_dockerfile = filename == "dockerfile" or filename.startswith("dockerfile.") or filename.endswith(".dockerfile")
        active_lines = []
        for number, line in enumerate(lines, start=1):
            content = line.lstrip(" \t")
            if not content.startswith("#"):
                active_lines.append((number, content))
            if is_workflow and not content.startswith("#") and (
                content.startswith("?") or "\\" in content or YAML_QUOTED_KEY.search(content)
            ):
                errors.append(f"{path.relative_to(root)}:{number}: YAML mapping key is not canonical")
            if is_workflow and not content.startswith("#") and YAML_USES_WORD.search(content):
                action = CANONICAL_USES.fullmatch(line)
                if action is None:
                    errors.append(
                        f"{path.relative_to(root)}:{number}: uses must use the canonical form and a full commit SHA"
                    )
                else:
                    name = action.group("name")
                    sha = action.group("sha")
                    expected = pins.get(name)
                    if expected is None or expected.get("kind") != "github-action":
                        errors.append(f"{path.relative_to(root)}:{number}: action {name} is absent from the inventory")
                    elif expected["digest_or_sha"].lower() != sha.lower():
                        errors.append(f"{path.relative_to(root)}:{number}: action {name} differs from the inventory")

            image = FROM_LINE.match(line) if is_dockerfile else None
            if image:
                reference = image.group(1)
                if "@sha256:" not in reference:
                    errors.append(f"{path.relative_to(root)}:{number}: FROM must contain @sha256:")
                else:
                    image_reference, digest = reference.split("@", 1)
                    if ":" not in image_reference or SHA256.fullmatch(digest) is None:
                        errors.append(f"{path.relative_to(root)}:{number}: FROM is not canonical")
                    else:
                        image_name, tag = image_reference.rsplit(":", 1)
                        expected = pins.get(image_name)
                        if expected is None or expected.get("kind") != "container":
                            errors.append(f"{path.relative_to(root)}:{number}: container {image_name} is absent from the inventory")
                        elif expected["version"] != tag:
                            errors.append(f"{path.relative_to(root)}:{number}: container {image_name} tag differs from the inventory")
                        elif expected["digest_or_sha"].lower() != digest.lower():
                            errors.append(f"{path.relative_to(root)}:{number}: container {image_name} differs from the inventory")

        if path.resolve() not in trusted_network_files:
            if suffix == ".sh":
                errors.extend(validate_download_script(path, root, text, inventory))
            else:
                active_network_lines = [
                    number
                    for number, content in active_lines
                    if has_network_marker(content, suffix)
                ]
                combined = "\n".join(content for _, content in active_lines)
                if not active_network_lines and has_network_marker(combined, suffix):
                    active_network_lines = [active_lines[0][0] if active_lines else 1]
                for number in active_network_lines:
                    errors.append(
                        f"{path.relative_to(root)}:{number}: download declarations are only permitted in canonical .sh shell scripts"
                    )

    global_json = root / "global.json"
    if not global_json.is_file():
        errors.append("global.json is required")
    else:
        try:
            payload = load_json(global_json.read_text(encoding="utf-8"))
            sdk_version = payload["sdk"]["version"]
            if not isinstance(sdk_version, str):
                raise TypeError("SDK version is not a string")
            expected = pins.get("dotnet-sdk")
            if expected is None or expected.get("kind") != "dotnet-sdk" or expected["version"] != sdk_version:
                errors.append("global.json: SDK version differs from the inventory")
        except (OSError, ValueError, KeyError, TypeError):
            errors.append("global.json: unable to read the pinned SDK version")

    packages = root / "Directory.Packages.props"
    if not packages.is_file():
        errors.append("Directory.Packages.props is required")
    else:
        declared_packages = set()
        nuget_pins = {
            name.casefold(): item
            for name, item in pins.items()
            if item.get("kind") == "nuget"
        }
        try:
            package_root = ElementTree.parse(packages).getroot()
            parents = {child: parent for parent in package_root.iter() for child in parent}
            for element in package_root.iter():
                local_tag = element.tag.rsplit("}", 1)[-1]
                if any(attribute.rsplit("}", 1)[-1] == "Condition" for attribute in element.attrib):
                    errors.append("Directory.Packages.props: conditional declarations are not permitted")
                if local_tag in {"Choose", "When", "Otherwise", "Import"}:
                    errors.append(f"Directory.Packages.props: {local_tag} declarations are not permitted")
                if local_tag != "PackageVersion":
                    continue
                parent = parents.get(element)
                if element.tag != "PackageVersion" or parent is None or parent.tag != "ItemGroup":
                    errors.append("Directory.Packages.props: PackageVersion must be an unnamespaced ItemGroup child")
                    continue
                name = element.attrib.get("Include")
                version = element.attrib.get("Version")
                if not isinstance(name, str) or not isinstance(version, str):
                    errors.append("Directory.Packages.props: PackageVersion requires Include and Version")
                    continue
                key = name.casefold()
                if key in declared_packages:
                    errors.append(f"Directory.Packages.props: {name} is declared more than once")
                declared_packages.add(key)
                expected = nuget_pins.get(key)
                if expected is None or expected["version"] != version:
                    errors.append(f"Directory.Packages.props: {name} differs from the inventory")
            for key, expected in nuget_pins.items():
                if key not in declared_packages:
                    errors.append(f"Directory.Packages.props: {expected['name']} is missing")
        except (OSError, ElementTree.ParseError):
            errors.append("Directory.Packages.props: unable to parse XML")
    return errors


def trusted_context() -> ssl.SSLContext:
    certificate_bundle = next(
        (path for path in (Path("/etc/ssl/cert.pem"),) if path.exists()),
        None,
    )
    return ssl.create_default_context(cafile=str(certificate_bundle) if certificate_bundle else None)


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


def validate_online(inventory: list[dict], now: datetime) -> list[str]:
    errors = []
    json_cache = {}

    def cached_json(url: str) -> tuple[dict, dict]:
        if url not in json_cache:
            json_cache[url] = fetch_json(url)
        return json_cache[url]

    def same_timestamp(left: object, right: object) -> bool:
        if not isinstance(left, str) or not isinstance(right, str):
            return False
        try:
            return parse_timestamp(left).replace(microsecond=0) == parse_timestamp(right).replace(microsecond=0)
        except ValueError:
            return False

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

    for item in inventory:
        source = item["source"]
        try:
            match = GITHUB_RELEASE.fullmatch(source)
            if match:
                owner, repository, tag = match.groups()
                release, _ = cached_json(f"https://api.github.com/repos/{owner}/{repository}/releases/tags/{tag}")
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
                else:
                    published = parse_timestamp(published_at)
                    if published.replace(microsecond=0) != parse_timestamp(item["released_at"]).replace(microsecond=0):
                        errors.append(f"{item['name']}: official release timestamp differs from inventory")
                    if published > now - timedelta(hours=72) and item["stabilization_exempt"] is False:
                        errors.append(f"{item['name']}: official release is newer than 72 hours")
                if item["kind"] in {"github-action", "github-release"}:
                    commit, _ = cached_json(f"https://api.github.com/repos/{owner}/{repository}/commits/{tag}")
                    if commit.get("sha", "").lower() != item["digest_or_sha"].lower():
                        errors.append(f"{item['name']}: official release commit moved")
                if item["kind"] == "download":
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
                        errors.append(f"{item['name']}: publisher did not provide the recorded download artifact checksum")
                    elif artifact["digest"].lower() != item["digest_or_sha"].lower():
                        errors.append(f"{item['name']}: publisher checksum differs from inventory")
            elif item["kind"] == "container":
                digest = fetch_manifest_digest(source)
                if digest is None:
                    errors.append(f"{item['name']}: registry did not provide Docker-Content-Digest")
                elif digest.lower() != item["digest_or_sha"].lower():
                    errors.append(f"{item['name']}: container manifest digest moved")
                payload, _ = cached_json(DOTNET_RELEASES)
                release = dotnet_container_release(item, payload)
                if release is None:
                    errors.append(f"{item['name']}: .NET metadata does not contain the container version")
                else:
                    release_date = release.get("release-date")
                    if not isinstance(release_date, str):
                        errors.append(f"{item['name']}: official container release date is required")
                    else:
                        official_date = datetime.fromisoformat(release_date).replace(tzinfo=timezone.utc)
                        if official_date.date() != parse_timestamp(item["released_at"]).date():
                            errors.append(f"{item['name']}: official container release date differs from inventory")
                        if official_date > now - timedelta(hours=72) and item["stabilization_exempt"] is False:
                            errors.append(f"{item['name']}: official container release is newer than 72 hours")
            elif item["kind"] == "nuget":
                payload, _ = cached_json(source)
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
            elif item["kind"] == "dotnet-sdk":
                payload, _ = cached_json(source)
                release = next((candidate for candidate in payload.get("releases", []) if candidate.get("sdk", {}).get("version") == item["version"]), None)
                if release is None:
                    errors.append(f"{item['name']}: .NET metadata does not contain the pinned SDK")
                    continue
                if release.get("release-date") != parse_timestamp(item["released_at"]).date().isoformat():
                    errors.append(f"{item['name']}: .NET metadata release timestamp differs from inventory")
                artifact = next((file for file in release.get("sdk", {}).get("files", []) if file.get("url") == item.get("artifact_url")), None)
                if artifact is None or f"sha512:{artifact.get('hash', '')}" != item["digest_or_sha"]:
                    errors.append(f"{item['name']}: .NET metadata digest differs from inventory")
            else:
                errors.append(f"{item['name']}: unsupported official source")
        except (AttributeError, UnicodeDecodeError, URLError, ValueError, TypeError, TimeoutError) as error:
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

    now = datetime.now(timezone.utc)
    errors = validate_inventory(inventory, now) + validate_declarations(root, inventory)
    if arguments.online and not errors:
        errors.extend(validate_online(inventory, now))
    for error in errors:
        print(error, file=sys.stderr)
    return int(bool(errors))


if __name__ == "__main__":
    raise SystemExit(main())
