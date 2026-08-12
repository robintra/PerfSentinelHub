#!/usr/bin/env python3
"""Verify that every release declaration matches one stable v0 tag."""

from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path


STABLE_TAG = re.compile(r"^v0\.[0-9]+\.[0-9]+$")


def fail(message: str) -> None:
    raise ValueError(message)


def read(path: Path, description: str) -> str:
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as error:
        fail(f"{description} cannot be read: {error}")


def exactly_one(values: list[str], description: str) -> str:
    if len(values) != 1:
        fail(f"{description} must have exactly one declaration")
    return values[0]


def project_version(root: Path) -> str:
    path = root / "PerfSentinelHub/PerfSentinelHub.csproj"
    try:
        project = ElementTree.fromstring(read(path, "project version"))
    except ElementTree.ParseError as error:
        fail(f"project version XML is malformed: {error}")
    values = [element.text.strip() for element in project.iter() if element.tag.rsplit("}", 1)[-1] == "Version" and element.text]
    return exactly_one(values, "project version")


def chart_value(root: Path, key: str, description: str) -> str:
    chart = read(root / "deploy/helm/perf-sentinel-hub/Chart.yaml", description)
    pattern = re.compile(rf"^{re.escape(key)}:[ \t]*['\"]?([^'\"# \t]+)['\"]?[ \t]*(?:#.*)?$", re.MULTILINE)
    return exactly_one(pattern.findall(chart), description)


def changelog_versions(root: Path) -> list[str]:
    changelog = read(root / "CHANGELOG.md", "changelog heading")
    return re.findall(r"^## \[([^]]+)](?: - [0-9]{4}-[0-9]{2}-[0-9]{2})?[ \t]*$", changelog, re.MULTILINE)


def image_version(root: Path) -> str:
    dockerfile = read(root / "Dockerfile", "image version label")
    values = re.findall(
        r'^LABEL[ \t]+org\.opencontainers\.image\.version=["\']([^"\']+)["\'][ \t]*$',
        dockerfile,
        re.MULTILINE,
    )
    return exactly_one(values, "image version label")


def check(tag: str, root: Path) -> None:
    if STABLE_TAG.fullmatch(tag) is None:
        fail("tag must be a stable tag matching v0.MINOR.PATCH without a suffix")
    version = tag[1:]
    declarations = (
        ("project version", project_version(root)),
        ("chart version", chart_value(root, "version", "chart version")),
        ("chart appVersion", chart_value(root, "appVersion", "chart appVersion")),
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
