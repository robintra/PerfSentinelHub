#!/usr/bin/env python3
"""Fail closed when repository supply-chain declarations drift from the inventory."""

from __future__ import annotations

import argparse
import gzip
import json
import os
import re
import ssl
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.error import URLError
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
KNOWN_KINDS = frozenset({"container", "dotnet-sdk", "download", "github-action", "github-release", "nuget"})
ACTION_SHA = re.compile(r"^[0-9a-f]{40}$", re.IGNORECASE)
SHA256 = re.compile(r"^sha256:[0-9a-f]{64}$")
SEMVER_PRERELEASE = re.compile(r"^v?\d+(?:\.\d+){1,3}-[0-9A-Za-z.-]+(?:\+[0-9A-Za-z.-]+)?$")
USES_LINE = re.compile(r"^\s*(?:-\s*)?uses:\s*([^\s#]+)")
FROM_LINE = re.compile(r"^\s*FROM\s+(?:--platform=\S+\s+)?([^\s]+)", re.IGNORECASE)
PACKAGE_VERSION = re.compile(r'<PackageVersion\s+Include="([^"]+)"\s+Version="([^"]+)"')
SAFE_NAME = r"[A-Za-z0-9][A-Za-z0-9._-]*"
SAFE_RELATIVE_PATH = rf"{SAFE_NAME}(?:/{SAFE_NAME})*"
GITHUB_RELEASE_URL = rf"https://github\.com/({SAFE_NAME})/({SAFE_NAME})/releases/tag/({SAFE_NAME})"
GITHUB_ARTIFACT_URL = rf"https://github\.com/{SAFE_NAME}/{SAFE_NAME}/releases/download/{SAFE_NAME}/{SAFE_NAME}"
GITHUB_RELEASE = re.compile(rf"^{GITHUB_RELEASE_URL}$")
GITHUB_ARTIFACT = re.compile(
    rf"^https://github\.com/({SAFE_NAME})/({SAFE_NAME})/releases/download/({SAFE_NAME})/({SAFE_NAME})$"
)
CANONICAL_DOWNLOAD = re.compile(
    rf"curl -fsSL (?P<url>{GITHUB_ARTIFACT_URL}) -o (?P<output>{SAFE_RELATIVE_PATH})"
)
NETWORK_MARKERS = ("curl", "wget", "https", "githubcom", "releasesdownload")
NUGET_REGISTRATION = re.compile(r"^https://api\.nuget\.org/v3/registration5-gz-semver2/[^/]+/[^/]+\.json$", re.IGNORECASE)
DOTNET_RELEASES = "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json"


def parse_timestamp(value: str) -> datetime:
    return datetime.fromisoformat(value.replace("Z", "+00:00")).astimezone(timezone.utc)


def text_files(root: Path):
    ignored = {".git", "bin", "obj", "__pycache__"}
    suffixes = {".yml", ".yaml", ".sh", ".py"}
    for path in root.rglob("*"):
        if not path.is_file() or any(part in ignored for part in path.parts):
            continue
        if path.name == "Dockerfile" or path.suffix in suffixes:
            yield path


def inventory_by_name(inventory: list[dict]) -> dict[str, dict]:
    return {item["name"]: item for item in inventory if isinstance(item, dict) and "name" in item}


def has_network_marker(line: str) -> bool:
    compact = re.sub(r"[^0-9A-Za-z]", "", line).lower()
    return any(marker in compact for marker in NETWORK_MARKERS)


def is_supported_source(item: dict) -> bool:
    source = item["source"]
    kind = item["kind"]
    if kind == "dotnet-sdk":
        return source == DOTNET_RELEASES
    if kind == "nuget":
        return bool(NUGET_REGISTRATION.fullmatch(source))
    if kind == "container":
        return source.startswith("https://mcr.microsoft.com/v2/") and "/manifests/" in source
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
        name_key = str(name).casefold()
        if name_key in name_keys:
            errors.append(f"{name}: ambiguous duplicate inventory name")
        name_keys.add(name_key)
        if item["kind"] not in KNOWN_KINDS:
            errors.append(f"{name}: unknown inventory kind {item['kind']}")
        if not isinstance(item["source"], str) or not is_supported_source(item):
            errors.append(f"{name}: unsupported official source")
        if not isinstance(item["reason"], str) or not item["reason"].strip():
            errors.append(f"{name}: reason must explain the pin")
        if item["kind"] != "container" and SEMVER_PRERELEASE.fullmatch(str(item["version"])):
            errors.append(f"{name}: prerelease versions are not permitted")
        try:
            age = now - parse_timestamp(item["released_at"])
        except (TypeError, ValueError):
            errors.append(f"{name}: released_at must be an ISO-8601 UTC timestamp")
            continue
        if not item["stabilization_exempt"] and age < timedelta(hours=72):
            errors.append(f"{name}: ordinary releases must be at least 72 hours old")
        if item["kind"] in {"github-action", "github-release"} and not ACTION_SHA.fullmatch(str(item["digest_or_sha"])):
            errors.append(f"{name}: GitHub releases require a raw release commit SHA")
        if item["kind"] == "github-action" and not ACTION_SHA.fullmatch(str(item["digest_or_sha"])):
            errors.append(f"{name}: GitHub Actions require a full commit SHA")
        if item["kind"] in {"container", "download"} and not SHA256.fullmatch(str(item["digest_or_sha"])):
            errors.append(f"{name}: downloaded tools and containers require a sha256 checksum")
        if item["kind"] == "download":
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
        if item["kind"] == "github-action" and match and item["name"].lower() != f"{match.group(1)}/{match.group(2)}".lower():
            errors.append(f"{name}: action name must match its owner/repository source")
    return errors


def validate_declarations(root: Path, inventory: list[dict]) -> list[str]:
    errors = []
    pins = inventory_by_name(inventory)
    for path in text_files(root):
        with path.open(encoding="utf-8", errors="replace", newline="") as source_file:
            lines = source_file.read().split("\n")
        is_workflow = path.suffix in {".yml", ".yaml"}
        is_dockerfile = path.name == "Dockerfile"
        checks_downloads = is_workflow or is_dockerfile or path.suffix == ".sh"
        for number, line in enumerate(lines, start=1):
            action = USES_LINE.match(line) if is_workflow else None
            if action:
                reference = action.group(1)
                if "@" not in reference or not ACTION_SHA.fullmatch(reference.rsplit("@", 1)[-1]):
                    errors.append(f"{path.relative_to(root)}:{number}: uses must be pinned to a full commit SHA")
                else:
                    name, sha = reference.rsplit("@", 1)
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
                    image_name = image_reference.rsplit(":", 1)[0]
                    expected = pins.get(image_name)
                    if expected is None or expected.get("kind") != "container":
                        errors.append(f"{path.relative_to(root)}:{number}: container {image_name} is absent from the inventory")
                    elif expected["digest_or_sha"].lower() != digest.lower():
                        errors.append(f"{path.relative_to(root)}:{number}: container {image_name} differs from the inventory")
            content = line.lstrip(" ")
            if checks_downloads and not content.startswith("#") and has_network_marker(content):
                if path.suffix != ".sh":
                    errors.append(
                        f"{path.relative_to(root)}:{number}: download declarations are only permitted in .sh shell scripts"
                    )
                    continue
                declaration = CANONICAL_DOWNLOAD.fullmatch(content)
                if declaration is None:
                    errors.append(f"{path.relative_to(root)}:{number}: download command is not canonical")
                    continue
                artifact_url = declaration.group("url")
                output = declaration.group("output")
                matches = [
                    item
                    for item in inventory
                    if item.get("kind") == "download" and item.get("artifact_url") == artifact_url
                ]
                if not matches:
                    errors.append(f"{path.relative_to(root)}:{number}: download URL is absent from the inventory")
                    continue
                if len(matches) != 1:
                    errors.append(f"{path.relative_to(root)}:{number}: download URL is ambiguous in the inventory")
                    continue
                expected = matches[0]
                checksum = expected["digest_or_sha"].removeprefix("sha256:")
                verification = f"echo '{checksum}  {output}' | sha256sum -c -"
                next_line = lines[number].lstrip(" ") if number < len(lines) else ""
                if next_line != verification:
                    errors.append(f"{path.relative_to(root)}:{number}: download does not bind {output} to its inventory checksum")

    global_json = root / "global.json"
    if global_json.exists():
        try:
            sdk_version = json.loads(global_json.read_text(encoding="utf-8"))["sdk"]["version"]
            expected = pins.get("dotnet-sdk")
            if expected is None or expected["version"] != sdk_version:
                errors.append("global.json: SDK version differs from the inventory")
        except (json.JSONDecodeError, KeyError, TypeError):
            errors.append("global.json: unable to read the pinned SDK version")

    packages = root / "Directory.Packages.props"
    if packages.exists():
        for name, version in PACKAGE_VERSION.findall(packages.read_text(encoding="utf-8")):
            expected = pins.get(name)
            if expected is None or expected.get("kind") != "nuget" or expected["version"] != version:
                errors.append(f"Directory.Packages.props: {name} differs from the inventory")
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
        return json.loads(content), dict(response.headers.items())


def fetch_manifest_digest(url: str) -> str | None:
    request = Request(
        url,
        headers=request_headers(url, "application/vnd.docker.distribution.manifest.list.v2+json"),
    )
    with urlopen(request, timeout=20, context=trusted_context()) as response:  # nosec B310: sources are committed HTTPS inventory entries
        return response.headers.get("Docker-Content-Digest")


def validate_online(inventory: list[dict], now: datetime) -> list[str]:
    errors = []
    for item in inventory:
        source = item["source"]
        try:
            match = GITHUB_RELEASE.fullmatch(source)
            if match:
                owner, repository, tag = match.groups()
                release, _ = fetch_json(f"https://api.github.com/repos/{owner}/{repository}/releases/tags/{tag}")
                if release.get("prerelease") or release.get("draft"):
                    errors.append(f"{item['name']}: official release is prerelease or draft")
                if release.get("tag_name", "").lstrip("v") != str(item["version"]).lstrip("v"):
                    errors.append(f"{item['name']}: official release tag moved or differs from inventory")
                if release.get("published_at") and parse_timestamp(release["published_at"]) > now - timedelta(hours=72) and not item["stabilization_exempt"]:
                    errors.append(f"{item['name']}: official release is newer than 72 hours")
                if item["kind"] in {"github-action", "github-release"}:
                    commit, _ = fetch_json(f"https://api.github.com/repos/{owner}/{repository}/commits/{tag}")
                    if commit.get("sha", "").lower() != item["digest_or_sha"].lower():
                        errors.append(f"{item['name']}: official release commit moved")
                if item["kind"] == "download":
                    artifact = next((asset for asset in release.get("assets", []) if asset.get("browser_download_url") == item["artifact_url"]), None)
                    if artifact is None or artifact.get("digest") is None:
                        errors.append(f"{item['name']}: publisher did not provide the recorded download artifact checksum")
                    elif artifact["digest"].lower() != item["digest_or_sha"].lower():
                        errors.append(f"{item['name']}: publisher checksum differs from inventory")
            elif item["kind"] == "container":
                digest = fetch_manifest_digest(source)
                if digest is None:
                    errors.append(f"{item['name']}: registry did not provide Docker-Content-Digest")
                elif digest.lower() != item["digest_or_sha"].lower():
                    errors.append(f"{item['name']}: container manifest digest moved")
            elif item["kind"] == "nuget":
                payload, _ = fetch_json(source)
                if payload.get("listed") is False:
                    errors.append(f"{item['name']}: NuGet package is unlisted")
                catalog = payload.get("catalogEntry")
                if isinstance(catalog, str):
                    catalog, _ = fetch_json(catalog)
                if not isinstance(catalog, dict) or catalog.get("version") != item["version"]:
                    errors.append(f"{item['name']}: NuGet version differs from inventory")
                published = catalog.get("published") if isinstance(catalog, dict) else None
                if not isinstance(published, str) or parse_timestamp(published).replace(microsecond=0) != parse_timestamp(item["released_at"]).replace(microsecond=0):
                    errors.append(f"{item['name']}: NuGet release timestamp differs from inventory")
                if not isinstance(catalog, dict) or f"sha512-base64:{catalog.get('packageHash', '')}" != item["digest_or_sha"]:
                    errors.append(f"{item['name']}: NuGet checksum differs from inventory")
            elif item["kind"] == "dotnet-sdk":
                payload, _ = fetch_json(source)
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
        except (URLError, ValueError, json.JSONDecodeError, TimeoutError) as error:
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
        payload = json.loads(inventory_path.read_text(encoding="utf-8"))
        inventory = payload["inventory"]
        if payload.get("schema_version") != 1 or not isinstance(inventory, list):
            raise ValueError("schema_version must be 1 and inventory must be an array")
    except (OSError, ValueError, KeyError, TypeError, json.JSONDecodeError) as error:
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
