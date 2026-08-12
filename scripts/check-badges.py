#!/usr/bin/env python3
"""Require README badges to point at their canonical evidence."""

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path


SDK_VERSION = "10.0.302"
LICENSE_SHA256 = "8486a10c4393cee1c25392769ddd3b2d6c242d6ec7928e1414efff7dfb2f07ef"
BADGES = {
    "CI": (
        "https://github.com/robintra/PerfSentinelHub/actions/workflows/ci.yml/badge.svg",
        "https://github.com/robintra/PerfSentinelHub/actions/workflows/ci.yml",
        ".github/workflows/ci.yml",
    ),
    "Sonar quality": (
        "https://sonarcloud.io/api/project_badges/measure?project=robintra_PerfSentinelHub&metric=alert_status",
        "https://sonarcloud.io/summary/new_code?id=robintra_PerfSentinelHub",
        "sonar-project.properties",
    ),
    "Sonar coverage": (
        "https://sonarcloud.io/api/project_badges/measure?project=robintra_PerfSentinelHub&metric=coverage",
        "https://sonarcloud.io/component_measures?id=robintra_PerfSentinelHub&metric=coverage&view=list",
        "sonar-project.properties",
    ),
    "Qodana": (
        "https://img.shields.io/badge/Qodana-configured-lightgrey",
        "https://github.com/robintra/PerfSentinelHub/actions/workflows/ci.yml",
        ".github/workflows/ci.yml",
    ),
    "CodeQL": (
        "https://github.com/robintra/PerfSentinelHub/actions/workflows/codeql.yml/badge.svg",
        "https://github.com/robintra/PerfSentinelHub/actions/workflows/codeql.yml",
        ".github/workflows/codeql.yml",
    ),
    "Daily audit": (
        "https://github.com/robintra/PerfSentinelHub/actions/workflows/security-audit.yml/badge.svg",
        "https://github.com/robintra/PerfSentinelHub/actions/workflows/security-audit.yml",
        ".github/workflows/security-audit.yml",
    ),
    "OpenSSF Scorecard": (
        "https://api.securityscorecards.dev/projects/github.com/robintra/PerfSentinelHub/badge",
        "https://securityscorecards.dev/viewer/?uri=github.com/robintra/PerfSentinelHub",
        ".github/workflows/security-audit.yml",
    ),
    "Latest release": (
        "https://img.shields.io/github/v/release/robintra/PerfSentinelHub?display_name=tag&sort=semver",
        "https://github.com/robintra/PerfSentinelHub/releases/latest",
        ".github/workflows/release.yml",
    ),
    "GHCR": (
        "https://img.shields.io/badge/GHCR-configured-lightgrey",
        "https://github.com/robintra/PerfSentinelHub/pkgs/container/perf-sentinel-hub",
        ".github/workflows/release.yml",
    ),
    "Helm": (
        "https://img.shields.io/badge/Helm-configured-lightgrey",
        "https://github.com/robintra/PerfSentinelHub/pkgs/container/charts%2Fperf-sentinel-hub",
        ".github/workflows/release.yml",
    ),
    ".NET": (
        f"https://img.shields.io/badge/.NET-{SDK_VERSION}-512BD4",
        "https://github.com/robintra/PerfSentinelHub/blob/main/global.json",
        "global.json",
    ),
    "License": (
        "https://img.shields.io/github/license/robintra/PerfSentinelHub",
        "https://github.com/robintra/PerfSentinelHub/blob/main/LICENSE",
        "LICENSE",
    ),
}

LINKED_BADGE = re.compile(
    r"\[!\[(?P<label>[^\]]+)\]\((?P<image>[^\s)]+)\)\]"
    r"\((?P<destination>[^\s)]+)\)"
)
IMAGE = re.compile(r"!\[(?P<label>[^\]]*)\]\((?P<image>[^\s)]+)\)")
NONCANONICAL_IMAGE = re.compile(
    r"!\[[^\]\r\n]*\]\[[^\]\r\n]+\]|<\s*/?\s*(?:img|picture|svg)\b",
    re.IGNORECASE,
)


def validate(root: Path):
    readme = (root / "README.md").read_text(encoding="utf-8")
    heading = re.match(r"\A# [^\n]+\n\n", readme)
    badge_block = readme[heading.end():].split("\n\n", 1)[0] if heading else ""
    linked = list(LINKED_BADGE.finditer(badge_block))
    errors = []

    if NONCANONICAL_IMAGE.search(badge_block):
        errors.append("unsupported image syntax in top badge block")

    for label, (image, destination, evidence) in BADGES.items():
        candidates = [match for match in linked if match.group("label") == label]
        if not candidates:
            errors.append(f"missing badge: {label}")
        elif len(candidates) != 1 or (
            candidates[0].group("image"), candidates[0].group("destination")
        ) != (image, destination):
            errors.append(f"{label} badge must link to its evidence")
        if not (root / evidence).is_file():
            errors.append(f"missing local evidence: {evidence}")

    for match in linked:
        expected = BADGES.get(match.group("label"))
        if expected is None:
            errors.append(f"unsupported badge: {match.group('label')}")

    linked_spans = [match.span() for match in linked]
    for image in IMAGE.finditer(badge_block):
        linked_image = any(
            start <= image.start() and image.end() <= end
            for start, end in linked_spans
        )
        if not linked_image:
            errors.append(f"badge image without an evidence link: {image.group('label')}")

    global_json = json.loads((root / "global.json").read_text(encoding="utf-8"))
    sdk = global_json.get("sdk") if isinstance(global_json, dict) else None
    if not isinstance(sdk, dict) or sdk.get("version") != SDK_VERSION:
        errors.append(".NET badge differs from global.json")
    license_digest = hashlib.sha256((root / "LICENSE").read_bytes()).hexdigest()
    if license_digest != LICENSE_SHA256:
        errors.append("License badge differs from canonical AGPL-3.0-only")

    return errors


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    arguments = parser.parse_args(argv)
    try:
        errors = validate(arguments.root)
    except (OSError, UnicodeError, ValueError) as error:
        errors = [f"badge check failed closed: {error}"]
    for error in errors:
        print(error, file=sys.stderr)
    return int(bool(errors))


if __name__ == "__main__":
    raise SystemExit(main())
