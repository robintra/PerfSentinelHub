#!/usr/bin/env python3
"""Verify that every release declaration matches one stable v0 tag."""

from __future__ import annotations

import re
import shlex
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path


STABLE_TAG = re.compile(r"^v0\.[0-9]+\.[0-9]+$")
STABLE_VERSION = re.compile(r"^0\.[0-9]+\.[0-9]+$")
CHART_ENTRY = re.compile(r"^(?P<key>[A-Za-z][A-Za-z0-9_-]*)[ ]*:[ ]*(?P<value>.*?)[ ]*$")
IMAGE_VERSION_LABEL = "org.opencontainers.image.version"
VERSION_PROPERTIES = frozenset(("version", "versionprefix", "versionsuffix"))


def fail(message: str) -> None:
    raise ValueError(message)


def read(path: Path, description: str) -> str:
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as error:
        fail(f"{description} cannot be read: {error}")


def parse_project(path: Path, description: str):
    try:
        project = ElementTree.fromstring(read(path, description))
    except ElementTree.ParseError as error:
        fail(f"{description} XML is malformed: {error}")
    if project.tag != "Project" or any(not isinstance(element.tag, str) or "}" in element.tag for element in project.iter()):
        fail(f"{description} XML structure is not canonical")
    return project


def project_version(root: Path) -> str:
    project_path = root / "PerfSentinelHub/PerfSentinelHub.csproj"
    project = parse_project(project_path, "project version")
    elements = list(project.iter())
    if any(element.tag.casefold() == "import" for element in elements):
        fail("project version cannot depend on an explicit import")
    if any(element.tag.casefold() in VERSION_PROPERTIES - {"version"} for element in elements):
        fail("project version cannot use VersionPrefix or VersionSuffix")

    versions = [element for element in elements if element.tag.casefold() == "version"]
    if len(versions) != 1:
        fail("project version must have exactly one declaration")
    version = versions[0]
    parent_groups = [
        group
        for group in list(project)
        if group.tag == "PropertyGroup" and version in list(group)
    ]
    if (
        len(parent_groups) != 1
        or version.tag != "Version"
        or parent_groups[0].attrib
        or version.attrib
        or list(version)
        or version.text is None
    ):
        fail("project version must be one unconditional canonical property")

    for directory in (project_path.parent, root):
        for name in ("Directory.Build.props", "Directory.Build.targets"):
            path = directory / name
            if not path.exists():
                continue
            imported = parse_project(path, "project version import")
            if any(
                element.tag.casefold() == "import" or element.tag.casefold() in VERSION_PROPERTIES
                for element in imported.iter()
            ):
                fail(f"project version can be overridden by {path.relative_to(root)}")

    value = version.text.strip()
    if STABLE_VERSION.fullmatch(value) is None:
        fail("project version must be canonical 0.MINOR.PATCH")
    return value


def chart_scalar(value: str) -> str:
    if value.startswith("'"):
        match = re.fullmatch(r"'((?:[^']|'')*)'[ ]*(?:#.*)?", value)
        if match is None:
            fail("chart structure contains a noncanonical single-quoted scalar")
        return match.group(1).replace("''", "'")
    if value.startswith('"'):
        match = re.fullmatch(r'"([^"\\]*)"[ ]*(?:#.*)?', value)
        if match is None:
            fail("chart structure contains a noncanonical double-quoted scalar")
        return match.group(1)
    scalar = re.split(r"[ ]+#", value, maxsplit=1)[0].rstrip()
    if not scalar or scalar[0] in "[{&*!|>@`" or "\t" in scalar:
        fail("chart structure contains a noncanonical scalar")
    return scalar


def chart_values(root: Path) -> dict[str, str]:
    chart = read(root / "deploy/helm/perf-sentinel-hub/Chart.yaml", "chart structure")
    values = {}
    for number, line in enumerate(chart.splitlines(), start=1):
        if not line or line.startswith("#"):
            continue
        if line[0].isspace():
            fail(f"chart structure line {number} is not a top-level mapping entry")
        match = CHART_ENTRY.fullmatch(line)
        if match is None:
            fail(f"chart structure line {number} is not canonical")
        key = match.group("key")
        if key in values:
            description = f"chart {key}" if key in {"version", "appVersion"} else "chart structure"
            fail(f"{description} must have exactly one declaration")
        values[key] = chart_scalar(match.group("value"))
    if values.get("apiVersion") != "v2" or values.get("name") != "perf-sentinel-hub" or values.get("type") != "application":
        fail("chart structure must identify the v2 perf-sentinel-hub application")
    return values


def changelog_versions(root: Path) -> list[str]:
    changelog = read(root / "CHANGELOG.md", "changelog heading")
    return re.findall(r"^## \[([^]]+)](?: - [0-9]{4}-[0-9]{2}-[0-9]{2})?[ \t]*$", changelog, re.MULTILINE)


def docker_instructions(text: str) -> list[str]:
    instructions = []
    parts = []
    for line in text.splitlines():
        stripped = line.strip()
        if not parts and stripped.startswith("#"):
            directive = re.fullmatch(r"#[ ]*escape[ ]*=[ ]*(.)[ ]*", stripped, re.IGNORECASE)
            if directive and directive.group(1) != "\\":
                fail("image version label requires the canonical Dockerfile escape character")
        if not parts and (not stripped or stripped.startswith("#")):
            continue
        end = line.rstrip()
        backslashes = len(end) - len(end.rstrip("\\"))
        continued = backslashes % 2 == 1
        if continued:
            end = end[:-1]
        parts.append(end.strip())
        if not continued:
            instructions.append(" ".join(parts))
            parts = []
    if parts:
        fail("image version label is inside an unterminated Dockerfile instruction")
    return instructions


def image_version(root: Path) -> str:
    dockerfile = read(root / "Dockerfile", "image version label")
    stage = -1
    values = []
    for instruction in docker_instructions(dockerfile):
        match = re.match(r"^(?P<name>[A-Za-z]+)(?:[ \t]+(?P<body>.*))?$", instruction)
        if match is None:
            continue
        name = match.group("name").casefold()
        if name == "from":
            stage += 1
            continue
        if name != "label":
            continue
        body = match.group("body") or ""
        if body.lstrip().startswith("["):
            if IMAGE_VERSION_LABEL.casefold() in body.casefold():
                fail("image version label cannot use a JSON-like LABEL form")
            continue
        try:
            fields = shlex.split(body, comments=False, posix=True)
        except ValueError:
            if IMAGE_VERSION_LABEL.casefold() in body.casefold():
                fail("image version label is not a canonical LABEL field")
            continue
        if any("=" not in field for field in fields):
            if IMAGE_VERSION_LABEL.casefold() in body.casefold():
                fail("image version label must use key=value LABEL syntax")
            continue
        for field in fields:
            key, value = field.split("=", 1)
            if key.casefold() == IMAGE_VERSION_LABEL.casefold():
                if key != IMAGE_VERSION_LABEL:
                    fail("image version label key is not canonical")
                values.append((stage, value))
    if stage < 0 or len(values) != 1 or values[0][0] != stage:
        fail("image version label must have exactly one declaration in the final Docker stage")
    return values[0][1]


def check(tag: str, root: Path) -> None:
    if STABLE_TAG.fullmatch(tag) is None:
        fail("tag must be a stable tag matching v0.MINOR.PATCH without a suffix")
    version = tag[1:]
    chart = chart_values(root)
    declarations = (
        ("project version", project_version(root)),
        ("chart version", chart.get("version", "")),
        ("chart appVersion", chart.get("appVersion", "")),
        ("image version label", image_version(root)),
    )
    for description, declared in declarations:
        if declared != version:
            fail(f"{description} is {declared!r}, expected {version!r}")
    headings = changelog_versions(root)
    if headings.count(version) != 1:
        fail(f"changelog heading must contain exactly one [{version}] release heading")


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: scripts/check-version.py v0.MINOR.PATCH", file=sys.stderr)
        return 2
    try:
        check(sys.argv[1], Path.cwd())
    except ValueError as error:
        print(f"version contract: {error}", file=sys.stderr)
        return 1
    print(f"version contract matches {sys.argv[1]}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
