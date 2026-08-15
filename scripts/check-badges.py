#!/usr/bin/env python3
"""Require README badges to point at their canonical evidence."""

import argparse
import hashlib
import sys
from pathlib import Path


REPO_URL = "https://github.com/robintra/PerfSentinelHub"
SONAR_URL = "https://sonarcloud.io"
SONAR_KEY = "robintrassard_PerfSentinelHub"
CI_WORKFLOW = ".github/workflows/ci.yml"
RELEASE_WORKFLOW = ".github/workflows/release.yml"
LICENSE_SHA256 = "8486a10c4393cee1c25392769ddd3b2d6c242d6ec7928e1414efff7dfb2f07ef"
BADGES = {
    ".NET": (
        "https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fraw.githubusercontent.com"
        "%2Frobintra%2FPerfSentinelHub%2Fmain%2Fglobal.json&query=%24.sdk.version"
        "&label=.NET&color=512BD4&logo=dotnet&logoColor=white",
        "https://dotnet.microsoft.com/",
        "global.json",
    ),
    "CI": (
        f"{REPO_URL}/actions/workflows/ci.yml/badge.svg",
        f"{REPO_URL}/actions/workflows/ci.yml",
        CI_WORKFLOW,
    ),
    "Security Audit": (
        f"{REPO_URL}/actions/workflows/security-audit.yml/badge.svg",
        f"{REPO_URL}/actions/workflows/security-audit.yml",
        ".github/workflows/security-audit.yml",
    ),
    "CodeQL": (
        f"{REPO_URL}/actions/workflows/codeql.yml/badge.svg",
        f"{REPO_URL}/actions/workflows/codeql.yml",
        ".github/workflows/codeql.yml",
    ),
    "Coverage": (
        f"{SONAR_URL}/api/project_badges/measure?project={SONAR_KEY}&metric=coverage",
        f"{SONAR_URL}/summary/overall?id={SONAR_KEY}",
        CI_WORKFLOW,
    ),
    "Quality Gate": (
        f"{SONAR_URL}/api/project_badges/measure?project={SONAR_KEY}&metric=alert_status",
        f"{SONAR_URL}/summary/overall?id={SONAR_KEY}",
        CI_WORKFLOW,
    ),
    "Release": (
        f"{REPO_URL}/actions/workflows/release.yml/badge.svg",
        f"{REPO_URL}/actions/workflows/release.yml",
        RELEASE_WORKFLOW,
    ),
    "Container image": (
        "https://img.shields.io/badge/ghcr.io-perf--sentinel--hub-2496ED?logo=docker&logoColor=white",
        f"{REPO_URL}/pkgs/container/perf-sentinel-hub",
        RELEASE_WORKFLOW,
    ),
    "Helm chart": (
        "https://img.shields.io/badge/helm-perf--sentinel--hub-0F1689?logo=helm&logoColor=white",
        f"{REPO_URL}/pkgs/container/charts%2Fperf-sentinel-hub",
        RELEASE_WORKFLOW,
    ),
}

CANONICAL_PREFIX = "# PerfSentinelHub\n\n" + '<p align="center">\n' + "".join(
    f'    <a href="{destination}"><img src="{image}" alt="{label}" /></a>\n'
    for label, (image, destination, _) in BADGES.items()
) + "</p>\n\n"


def validate(root: Path):
    readme = (root / "README.md").read_bytes()
    errors = []

    if not readme.startswith(CANONICAL_PREFIX.encode("utf-8")):
        errors.append("README must start with the canonical top badge block")

    for _, _, evidence in BADGES.values():
        if not (root / evidence).is_file():
            errors.append(f"missing local evidence: {evidence}")

    license_digest = hashlib.sha256((root / "LICENSE").read_bytes()).hexdigest()
    if license_digest != LICENSE_SHA256:
        errors.append("LICENSE differs from canonical AGPL-3.0-only")

    return errors


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    arguments = parser.parse_args(argv)
    try:
        errors = validate(arguments.root)
    except (OSError, ValueError) as error:
        errors = [f"badge check failed closed: {error}"]
    for error in errors:
        print(error, file=sys.stderr)
    return int(bool(errors))


if __name__ == "__main__":
    raise SystemExit(main())
