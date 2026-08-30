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
        ("global.json", '"sdk"'),
    ),
    "CI": (
        f"{REPO_URL}/actions/workflows/ci.yml/badge.svg",
        f"{REPO_URL}/actions/workflows/ci.yml",
        (CI_WORKFLOW, None),
    ),
    "Security Audit": (
        f"{REPO_URL}/actions/workflows/security-audit.yml/badge.svg",
        f"{REPO_URL}/actions/workflows/security-audit.yml",
        (".github/workflows/security-audit.yml", None),
    ),
    "CodeQL": (
        f"{REPO_URL}/actions/workflows/codeql.yml/badge.svg",
        f"{REPO_URL}/actions/workflows/codeql.yml",
        (".github/workflows/codeql.yml", None),
    ),
    "Coverage": (
        f"{SONAR_URL}/api/project_badges/measure?project={SONAR_KEY}&metric=coverage",
f"{SONAR_URL}/summary/overall?id={SONAR_KEY}",
        (CI_WORKFLOW, SONAR_KEY),
    ),
    "Quality Gate": (
        f"{SONAR_URL}/api/project_badges/measure?project={SONAR_KEY}&metric=alert_status",
f"{SONAR_URL}/summary/overall?id={SONAR_KEY}",
        (CI_WORKFLOW, SONAR_KEY),
    ),
    "Release": (
        f"{REPO_URL}/actions/workflows/release.yml/badge.svg",
        f"{REPO_URL}/actions/workflows/release.yml",
        (RELEASE_WORKFLOW, None),
    ),
    "Latest release": (
        "https://img.shields.io/github/v/release/robintra/PerfSentinelHub"
        "?display_name=tag&sort=semver&color=512BD4",
        f"{REPO_URL}/releases/latest",
        ("CHANGELOG.md", None),
    ),
    "Container image": (
        "https://img.shields.io/badge/ghcr.io-perf--sentinel--hub-2496ED"
        "?logo=docker&logoColor=white",
        f"{REPO_URL}/pkgs/container/perf-sentinel-hub",
        ("Dockerfile", None),
    ),
    "Helm chart": (
        "https://img.shields.io/badge/chart-perf--sentinel--hub-0F1689"
        "?logo=helm&logoColor=white",
        f"{REPO_URL}/pkgs/container/charts%2Fperf-sentinel-hub",
        ("deploy/helm/perf-sentinel-hub/Chart.yaml", "name: perf-sentinel-hub"),
    ),
}

CANONICAL_PREFIX = '<p align="center">\n' + "".join(
    f'    <a href="{destination}"><img src="{image}" alt="{label}" /></a>\n'
    for label, (image, destination, _) in BADGES.items()
) + "</p>\n\n# PerfSentinelHub\n\n"


# Both READMEs carry the same block, so both are checked. Validating only the
# English one lets the mirror drift silently, which is the whole failure mode
# the canonical block exists to prevent.
READMES = ("README.md", "README-FR.md")


def validate(root: Path):
    errors = []

    for name in READMES:
        if not (root / name).read_bytes().startswith(CANONICAL_PREFIX.encode("utf-8")):
            errors.append(f"{name} must start with the canonical top badge block")

    for _, _, (evidence, claim) in BADGES.values():
        path = root / evidence
        if not path.is_file():
            errors.append(f"missing local evidence: {evidence}")
        elif claim is not None and claim not in path.read_text(encoding="utf-8"):
            errors.append(f"{evidence} no longer states {claim!r}")

    license_digest = hashlib.sha256((root / "LICENSE").read_bytes()).hexdigest()
    if license_digest != LICENSE_SHA256:
        errors.append("LICENSE differs from canonical AGPL-3.0-only")

    # Two badges can name one evidence file, so the same breakage reports once.
    return list(dict.fromkeys(errors))


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
