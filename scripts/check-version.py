#!/usr/bin/env python3
"""Verify that every release declaration matches one stable v0 tag."""

from __future__ import annotations

import re
import shlex
import sys
import xml.etree.ElementTree as ElementTree
from datetime import date
from pathlib import Path


STABLE_TAG = re.compile(r"^v0\.[0-9]+\.[0-9]+$")
STABLE_VERSION = re.compile(r"^0\.[0-9]+\.[0-9]+$")
CHART_ENTRY = re.compile(r"(?P<key>[A-Za-z][A-Za-z0-9_-]*+) *+: *+(?P<value>.*)")
CHANGELOG_HEADING = re.compile(r"^## \[(?P<version>[^]]+)] - (?P<date>[0-9]{4}-[0-9]{2}-[0-9]{2})$")
FENCE_OPENING = re.compile(r"^ {0,3}(?P<marker>`{3,}+|~{3,}+)(?P<info>.*)$")
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


def canonical_version_element(project):
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
        for group in project
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
    return version


def reject_version_overrides(root: Path, project_path: Path) -> None:
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


def project_version(root: Path) -> str:
    project_path = root / "PerfSentinelHub/PerfSentinelHub.csproj"
    version = canonical_version_element(parse_project(project_path, "project version"))
    reject_version_overrides(root, project_path)
    value = version.text.strip()
    if STABLE_VERSION.fullmatch(value) is None:
        fail("project version must be canonical 0.MINOR.PATCH")
    return value


def chart_scalar(value: str) -> str:
    if value.startswith("'"):
        match = re.fullmatch(r"'((?:[^']|'')*)' *(?:#.*)?", value)
        if match is None:
            fail("chart structure contains a noncanonical single-quoted scalar")
        return match.group(1).replace("''", "'")
    if value.startswith('"'):
        match = re.fullmatch(r'"([^"\\]*)" *(?:#.*)?', value)
        if match is None:
            fail("chart structure contains a noncanonical double-quoted scalar")
        return match.group(1)
    # Splitting on a regex here scans every position, which is quadratic on a run of spaces.
    # The run between the cut and the comment marker is stripped either way.
    scalar = value.split(" #", 1)[0].rstrip()
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
        values[key] = chart_scalar(match.group("value").rstrip(" "))
    if values.get("apiVersion") != "v2" or values.get("name") != "perf-sentinel-hub" or values.get("type") != "application":
        fail("chart structure must identify the v2 perf-sentinel-hub application")
    return values


def without_html_comments(line: str, inside_comment: bool) -> tuple[str, bool]:
    visible = []
    position = 0
    while position < len(line):
        if inside_comment:
            end = line.find("-->", position)
            if end < 0:
                return "".join(visible), True
            position = end + 3
            inside_comment = False
            continue
        start = line.find("<!--", position)
        unmatched_end = line.find("-->", position)
        if unmatched_end >= 0 and (start < 0 or unmatched_end < start):
            fail("changelog heading contains an unmatched HTML comment terminator")
        if start < 0:
            visible.append(line[position:])
            break
        visible.append(line[position:start])
        position = start + 4
        inside_comment = True
    return "".join(visible), inside_comment


def reject_ambiguous_comment_marker(line: str) -> None:
    if "<!--" not in line and "-->" not in line:
        return
    indentation = line[: len(line) - len(line.lstrip(" \t"))]
    if (
        "`" in line
        or "\t" in indentation
        or len(indentation) >= 4
        or "\\<!--" in line
        or "\\-->" in line
    ):
        fail("changelog heading contains an ambiguous HTML comment marker")


def heading_version(visible: str) -> str | None:
    heading_candidate = visible.lstrip(" \t")
    if not heading_candidate.startswith("## ["):
        return None
    if heading_candidate != visible:
        fail("changelog heading must not be indented")
    heading = CHANGELOG_HEADING.fullmatch(visible)
    if heading is None:
        fail("changelog heading must use canonical ## [VERSION] - YYYY-MM-DD syntax")
    try:
        date.fromisoformat(heading.group("date"))
    except ValueError:
        fail("changelog heading date must be a valid ISO calendar date")
    return heading.group("version")


def fence_closes(fence, line: str) -> bool:
    marker, length = fence
    return re.fullmatch(rf" {{0,3}}{re.escape(marker)}{{{length},}}[ \t]*", line) is not None


def opened_fence(visible: str):
    opening = FENCE_OPENING.fullmatch(visible)
    if opening is None:
        return None
    marker = opening.group("marker")
    if marker[0] == "`" and "`" in opening.group("info"):
        fail("changelog heading cannot be hidden by invalid backtick fence info")
    return (marker[0], len(marker))


def changelog_versions(root: Path) -> list[str]:
    changelog = read(root / "CHANGELOG.md", "changelog heading")
    inside_comment = False
    fence = None
    versions = []
    for line in changelog.splitlines():
        reject_ambiguous_comment_marker(line)
        if fence is not None:
            if fence_closes(fence, line):
                fence = None
            continue

        visible, inside_comment = without_html_comments(line, inside_comment)
        fence = opened_fence(visible)
        if fence is not None:
            continue
        version = heading_version(visible)
        if version is not None:
            versions.append(version)

    if inside_comment:
        fail("changelog heading is inside an unclosed HTML comment")
    if fence is not None:
        fail("changelog heading is inside an unclosed fenced code block")
    return versions


def reject_noncanonical_escape(stripped: str) -> None:
    directive = re.fullmatch(r"# *+escape *+= *+(\S*+) *+", stripped, re.IGNORECASE)
    if directive and directive.group(1) != "\\":
        fail("image version label requires the canonical Dockerfile escape character")


def docker_instructions(text: str) -> list[str]:
    instructions = []
    parts = []
    for line in text.splitlines():
        stripped = line.strip()
        if not parts and stripped.startswith("#"):
            reject_noncanonical_escape(stripped)
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


def label_version_values(body: str) -> list[str]:
    if body.lstrip().startswith("["):
        if IMAGE_VERSION_LABEL.casefold() in body.casefold():
            fail("image version label cannot use a JSON-like LABEL form")
        return []
    try:
        fields = shlex.split(body, comments=False, posix=True)
    except ValueError:
        if IMAGE_VERSION_LABEL.casefold() in body.casefold():
            fail("image version label is not a canonical LABEL field")
        return []
    if any("=" not in field for field in fields):
        if IMAGE_VERSION_LABEL.casefold() in body.casefold():
            fail("image version label must use key=value LABEL syntax")
        return []
    values = []
    for field in fields:
        key, value = field.split("=", 1)
        if key.casefold() == IMAGE_VERSION_LABEL.casefold():
            if key != IMAGE_VERSION_LABEL:
                fail("image version label key is not canonical")
            values.append(value)
    return values


def image_version(root: Path) -> str:
    dockerfile = read(root / "Dockerfile", "image version label")
    if "<<" in dockerfile:
        fail("image version label cannot be verified in a Dockerfile containing heredoc syntax")
    stage = -1
    values = []
    for instruction in docker_instructions(dockerfile):
        match = re.match(r"^(?P<name>[A-Za-z]++)(?:[ \t]++(?P<body>.*))?$", instruction)
        if match is None:
            continue
        name = match.group("name").casefold()
        if name == "from":
            stage += 1
            continue
        if name != "label":
            continue
        for value in label_version_values(match.group("body") or ""):
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
