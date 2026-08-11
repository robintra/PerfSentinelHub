#!/usr/bin/env python3
"""Validate the repository's canonical Qodana, Sonar, and secret metadata."""

from __future__ import annotations

import argparse
import json
import re
import sys
import xml.etree.ElementTree as ElementTree
from pathlib import Path


QODANA_IMAGE = re.compile(
    r"^jetbrains/qodana-dotnet:(?P<version>[0-9]{4}\.[0-9]+)@"
    r"(?P<digest>sha256:[0-9a-f]{64})$"
)
QODANA_PATHS = (
    "PerfSentinelHub/bin",
    "PerfSentinelHub/obj",
    "PerfSentinelHub.Tests/bin",
    "PerfSentinelHub.Tests/obj",
    "TestResults",
    "artifacts/coverage",
    "artifacts/sonar",
    "graphify-out",
)
SONAR_PROPERTIES = {
    "sonar.projectKey": "robintra_PerfSentinelHub",
    "sonar.organization": "robintra",
    "sonar.host.url": "https://sonarcloud.io",
    "sonar.sources": "PerfSentinelHub",
    "sonar.tests": "PerfSentinelHub.Tests",
    "sonar.exclusions": (
        "**/bin/**,**/obj/**,TestResults/**,artifacts/coverage/**,"
        "artifacts/sonar/**,graphify-out/**"
    ),
    "sonar.coverageReportPaths": "artifacts/sonar/SonarQube.xml",
    "sonar.cs.vstest.reportsPaths": "artifacts/coverage/tests.trx",
    "sonar.sourceEncoding": "UTF-8",
}
SECRET_FIELDS = {"name", "scope", "purpose", "owner", "rotation_procedure"}
REQUIRED_SECRETS = {"QODANA_TOKEN", "SONAR_TOKEN"}
SECRET_NAME = re.compile(r"^[A-Z][A-Z0-9_]*$")
WORKFLOW_EXPRESSION = re.compile(r"\$\{\{(?P<body>.*?)\}\}")
CANONICAL_SECRET_REFERENCE = re.compile(r"\s*secrets\.([A-Z][A-Z0-9_]*)\s*")
SECRET_TOKEN = re.compile(r"(?<![A-Za-z0-9_])secrets(?![A-Za-z0-9_])", re.IGNORECASE)
SECRET_VALUE = re.compile(
    r"(?i)(?:\b(?:bearer|basic)\s+[A-Za-z0-9._~+/=-]{16,}"
    r"|(?:gh[pousr]_|github_pat_|sqa_|qdt_)[A-Za-z0-9_=-]{16,}"
    r"|-----BEGIN [A-Z ]+ PRIVATE KEY-----|base64:[A-Za-z0-9+/=]{16,}"
    r"|eyJ[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{8,})"
)
INPUTS = {
    "artifacts/coverage/coverage.cobertura.xml": "coverage",
    "artifacts/coverage/tests.trx": "TestRun",
    "artifacts/sonar/SonarQube.xml": "coverage",
}


def unique_object(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def load_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)


def canonical_qodana_lines(linter: str) -> tuple[str, ...]:
    return (
        'version: "1.0"',
        f"linter: {linter}",
        "profile:",
        "  name: qodana.recommended",
        "dotnet:",
        "  solution: PerfSentinelHub.sln",
        "exclude:",
        "  - name: All",
        "    paths:",
        *(f"      - {path}" for path in QODANA_PATHS),
        "failureConditions:",
        "  severityThresholds:",
        "    critical: 0",
        "    high: 0",
    )


def validate_qodana(root: Path) -> list[str]:
    path = root / "qodana.yaml"
    try:
        text = path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as error:
        return [f"qodana.yaml: unable to read strict UTF-8: {error}"]
    errors = []
    if "\r" in text or not text.endswith("\n"):
        errors.append("qodana.yaml: canonical LF-terminated YAML is required")
    if "qodana.starter" in text:
        errors.append("qodana.yaml: qodana.starter is not permitted")
    if any(
        line.lstrip().startswith("#") and "failureConditions" in line
        for line in text.splitlines()
    ):
        errors.append("qodana.yaml: failureConditions must not be commented out")
    ambiguous = re.compile(r"\t|(^|\s)(?:[&*!]|<<:)|[{}\[\]\\]|^[ ]*['\"][^'\"]+['\"]:")
    if any(ambiguous.search(line) for line in text.splitlines() if not line.lstrip().startswith("#")):
        errors.append("qodana.yaml: ambiguous YAML forms are not permitted")
    lines = tuple(text.splitlines())
    linter = lines[1].removeprefix("linter: ") if len(lines) > 1 else ""
    image = QODANA_IMAGE.fullmatch(linter)
    if image is None:
        errors.append("qodana.yaml: linter must use a stable version and sha256 digest")
        return errors
    unexpected_paths = [
        line.removeprefix("      - ")
        for line in lines
        if line.startswith("      - ")
        and line.removeprefix("      - ") not in QODANA_PATHS
    ]
    errors.extend(
        f"qodana.yaml: excluded path is not permitted: {excluded}"
        for excluded in unexpected_paths
    )
    if lines != canonical_qodana_lines(linter):
        errors.append("qodana.yaml: configuration differs from the canonical blocking policy")
    try:
        inventory = load_json(root / "config" / "supply-chain.json")["inventory"]
        pin = next(
            item
            for item in inventory
            if isinstance(item, dict) and item.get("name") == "jetbrains/qodana-dotnet"
        )
        if (
            pin.get("kind") != "container"
            or pin.get("version") != image.group("version")
            or pin.get("digest_or_sha") != image.group("digest")
            or pin.get("stabilization_exempt") is not False
        ):
            errors.append("qodana.yaml: linter differs from the non-exempt supply-chain pin")
    except (OSError, UnicodeError, ValueError, KeyError, StopIteration, TypeError):
        errors.append("qodana.yaml: matching supply-chain container pin is required")
    return errors


def validate_sonar(root: Path) -> list[str]:
    path = root / "sonar-project.properties"
    try:
        text = path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as error:
        return [f"sonar-project.properties: unable to read strict UTF-8: {error}"]
    errors = []
    if "\r" in text or not text.endswith("\n"):
        errors.append("sonar-project.properties: canonical LF-terminated properties are required")
    properties = {}
    for number, line in enumerate(text.splitlines(), start=1):
        if not line or line.startswith(("#", "!")) or "\\" in line or "=" not in line:
            errors.append(f"sonar-project.properties:{number}: non-canonical property")
            continue
        key, value = line.split("=", 1)
        if key.strip() != key or value.strip() != value or not key or not value:
            errors.append(f"sonar-project.properties:{number}: non-canonical property")
            continue
        if key in properties:
            errors.append(f"sonar-project.properties:{number}: duplicate property {key}")
            continue
        properties[key] = value
    unknown = set(properties) - set(SONAR_PROPERTIES)
    missing = set(SONAR_PROPERTIES) - set(properties)
    errors.extend(
        f"sonar-project.properties: unknown property {key}" for key in sorted(unknown)
    )
    errors.extend(
        f"sonar-project.properties: missing required property {key}" for key in sorted(missing)
    )
    for key, expected in SONAR_PROPERTIES.items():
        if key in properties and properties[key] != expected:
            errors.append(f"sonar-project.properties: {key} differs from the canonical policy")
    return errors


def resembles_secret(value: str) -> bool:
    compact = value.strip()
    return bool(
        SECRET_VALUE.search(compact)
        or re.fullmatch(r"[A-Za-z0-9+/=_-]{40,}", compact)
        or re.search(r"https://[^/\s]+:[^@\s]+@", compact)
    )


def validate_secret_inventory(root: Path) -> tuple[list[str], set[str]]:
    path = root / "config" / "secret-inventory.json"
    try:
        payload = load_json(path)
    except (OSError, UnicodeError, ValueError, TypeError) as error:
        return [f"config/secret-inventory.json: invalid canonical JSON: {error}"], set()
    errors = []
    names = set()
    if not isinstance(payload, dict) or set(payload) != {"schema_version", "secrets"}:
        return ["config/secret-inventory.json: only schema_version and secrets are permitted"], names
    entries = payload.get("secrets")
    if payload.get("schema_version") != 1 or not isinstance(entries, list):
        return ["config/secret-inventory.json: schema_version 1 and a secrets array are required"], names
    for index, entry in enumerate(entries):
        label = f"config/secret-inventory.json: entry {index + 1}"
        if not isinstance(entry, dict):
            errors.append(f"{label} must be an object")
            continue
        if set(entry) != SECRET_FIELDS:
            errors.append(f"{label} must contain metadata fields only")
            continue
        name = entry.get("name")
        if not isinstance(name, str) or SECRET_NAME.fullmatch(name) is None:
            errors.append(f"{label} name is not canonical")
            continue
        if name in names:
            errors.append(f"{label} duplicates {name}")
        names.add(name)
        for field in SECRET_FIELDS - {"name"}:
            value = entry.get(field)
            if not isinstance(value, str) or not value.strip():
                errors.append(f"{label} requires non-empty {field}")
            elif resembles_secret(value):
                errors.append(f"{label} {field} resembles a secret value")
    missing = REQUIRED_SECRETS - names
    unexpected = names - REQUIRED_SECRETS
    errors.extend(
        f"config/secret-inventory.json: missing required secret metadata {name}"
        for name in sorted(missing)
    )
    errors.extend(
        f"config/secret-inventory.json: unexpected secret metadata {name}"
        for name in sorted(unexpected)
    )
    return errors, names


def validate_workflow_secrets(root: Path, inventory_names: set[str]) -> list[str]:
    errors = []
    workflows = root / ".github" / "workflows"
    if not workflows.exists():
        return errors
    for path in sorted((*workflows.rglob("*.yml"), *workflows.rglob("*.yaml"))):
        try:
            text = path.read_text(encoding="utf-8")
        except (OSError, UnicodeError) as error:
            errors.append(f"{path.relative_to(root)}: unable to read strict UTF-8: {error}")
            continue
        for number, line in enumerate(text.splitlines(), start=1):
            if line.lstrip().startswith("#"):
                continue
            remaining = line
            for expression in WORKFLOW_EXPRESSION.finditer(line):
                body = expression.group("body")
                if not SECRET_TOKEN.search(body):
                    continue
                match = CANONICAL_SECRET_REFERENCE.fullmatch(body)
                if match is None:
                    errors.append(
                        f"{path.relative_to(root)}:{number}: non-canonical secret reference"
                    )
                    continue
                name = match.group(1)
                if name not in inventory_names:
                    errors.append(
                        f"{path.relative_to(root)}:{number}: workflow secret {name} is absent from the inventory"
                    )
                remaining = remaining.replace(expression.group(0), "", 1)
            if SECRET_TOKEN.search(remaining):
                errors.append(
                    f"{path.relative_to(root)}:{number}: non-canonical secret reference"
                )
    return errors


def validate_inputs(root: Path) -> list[str]:
    errors = []
    for relative, expected_root in INPUTS.items():
        path = root / relative
        try:
            document = ElementTree.parse(path)
        except (OSError, ElementTree.ParseError) as error:
            errors.append(f"{relative}: missing or invalid analysis input: {error}")
            continue
        if document.getroot().tag.rsplit("}", 1)[-1] != expected_root:
            errors.append(f"{relative}: unexpected XML root element")
    return errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--require-analysis-inputs",
        action="store_true",
        help="also require locally generated Cobertura, SonarQube, and TRX inputs",
    )
    arguments = parser.parse_args(argv)
    root = Path.cwd()
    secret_errors, inventory_names = validate_secret_inventory(root)
    errors = [
        *validate_qodana(root),
        *validate_sonar(root),
        *secret_errors,
        *validate_workflow_secrets(root, inventory_names),
    ]
    if arguments.require_analysis_inputs:
        errors.extend(validate_inputs(root))
    for error in errors:
        print(error, file=sys.stderr)
    return int(bool(errors))


if __name__ == "__main__":
    raise SystemExit(main())
