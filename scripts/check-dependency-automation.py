#!/usr/bin/env python3
"""Fail closed when dependency automation drifts from repository policy."""

from __future__ import annotations

import json
import posixpath
import re
import sys
from pathlib import Path


ECOSYSTEMS = {
    "nuget": "/",
    "docker": "/",
    "helm": "/deploy/helm/perf-sentinel-hub",
    "github-actions": "/",
}
UPDATE_KEYS = {
    "package-ecosystem",
    "directory",
    "schedule",
    "cooldown",
    "open-pull-requests-limit",
    "labels",
    "groups",
}
SCHEDULE = {
    "interval": "weekly",
    "day": "monday",
    "time": "06:00",
    "timezone": "Europe/Paris",
}
SEMVER_PRERELEASE = re.compile(
    r"^v?\d+(?:\.\d+){1,3}-[0-9A-Za-z.-]+(?:\+[0-9A-Za-z.-]+)?$"
)
UNSTABLE_CONTAINER_TAG = re.compile(
    r"(?:^|[._-])(?:alpha|beta|rc|preview|pre|eap|nightly|snapshot|canary|unstable|dev)[0-9]*(?:[._-]|$)",
    re.IGNORECASE,
)
RENOVATE_FILES = (
    "renovate.json",
    "renovate.json5",
    ".renovaterc",
    ".renovaterc.json",
    ".github/renovate.json",
    ".github/renovate.json5",
)
AUTO_MERGE_MARKER = re.compile(
    r"auto.?merge|merge\s+dependabot|enablePullRequestAutoMerge", re.IGNORECASE
)
DEPENDABOT_IDENTITY = re.compile(
    r"(?<![a-z0-9_-])dependabot(?:\[bot\])?(?![a-z0-9_-])", re.IGNORECASE
)
GITHUB_IDENTITY = re.compile(
    r"(?<![a-z0-9_])github\s*\.\s*(?:actor|event\s*\.\s*pull_request\s*\.\s*user\s*\.\s*login)(?![a-z0-9_])",
    re.IGNORECASE,
)
WORKFLOW_TOKEN = re.compile(r"--[a-z0-9-]+|[a-z0-9_./\[\]-]+", re.IGNORECASE)
YAML_KEY = re.compile(r"^(?P<key>[a-z][a-z0-9-]*):(?: (?P<value>.+))?$")


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def load_json(path):
    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)


def parse_yaml_scalar(value):
    parsed = json.loads(value, object_pairs_hook=unique_object)
    if isinstance(parsed, (dict, list)) and value not in {"{}", "[]"}:
        raise ValueError("flow collections are not permitted")
    return parsed


def load_dependabot_yaml(path):
    raw_lines = path.read_text(encoding="utf-8").splitlines()
    lines = []
    for number, raw in enumerate(raw_lines, start=1):
        if not raw or raw.isspace():
            continue
        if "\t" in raw or raw.rstrip(" ") != raw:
            raise ValueError(f"line {number}: tabs and trailing spaces are not permitted")
        indent = len(raw) - len(raw.lstrip(" "))
        if indent % 2:
            raise ValueError(f"line {number}: indentation must use two spaces")
        lines.append((number, indent, raw[indent:]))
    if not lines:
        raise ValueError("configuration is empty")

    def parse_block(index, indent):
        if index >= len(lines) or lines[index][1] != indent:
            raise ValueError("invalid indentation")
        sequence = lines[index][2] == "-" or lines[index][2].startswith("- ")
        result = [] if sequence else {}
        while index < len(lines) and lines[index][1] == indent:
            number, _, content = lines[index]
            if sequence:
                if content == "-":
                    index += 1
                    if index >= len(lines) or lines[index][1] != indent + 2:
                        raise ValueError(f"line {number}: sequence item requires a nested value")
                    item, index = parse_block(index, indent + 2)
                elif content.startswith("- "):
                    item = parse_yaml_scalar(content[2:])
                    index += 1
                else:
                    raise ValueError(f"line {number}: mixed mapping and sequence")
                result.append(item)
                continue

            match = YAML_KEY.fullmatch(content)
            if match is None:
                raise ValueError(f"line {number}: mapping key is not canonical")
            key = match.group("key")
            if key in result:
                raise ValueError(f"line {number}: duplicate YAML key: {key}")
            value = match.group("value")
            index += 1
            if value is None:
                if index >= len(lines) or lines[index][1] != indent + 2:
                    raise ValueError(f"line {number}: mapping key requires a nested value")
                result[key], index = parse_block(index, indent + 2)
            else:
                result[key] = parse_yaml_scalar(value)
        return result, index

    parsed, index = parse_block(0, lines[0][1])
    if lines[0][1] != 0 or index != len(lines):
        raise ValueError("unexpected indentation or trailing content")
    return parsed


def has_dependabot_gh_merge(text):
    if GITHUB_IDENTITY.search(text) is None or DEPENDABOT_IDENTITY.search(text) is None:
        return False
    normalized = re.sub(r"\\\s*\r?\n\s*", " ", text.casefold())
    tokens = WORKFLOW_TOKEN.findall(normalized)
    return any(
        (
            tokens[index] == "gh"
            or (
                posixpath.isabs(tokens[index])
                and posixpath.basename(tokens[index]) == "gh"
            )
        )
        and tokens[index + 1 : index + 3] == ["pr", "merge"]
        for index in range(len(tokens) - 2)
    )


def validate_ownership(root):
    errors = []
    for relative in RENOVATE_FILES:
        if (root / relative).exists():
            errors.append(f"{relative}: Renovate duplicates Dependabot ownership")
    for workflow in sorted((root / ".github" / "workflows").glob("*.y*ml")):
        try:
            text = workflow.read_text(encoding="utf-8")
        except OSError as error:
            errors.append(f"{workflow.relative_to(root)}: unable to inspect workflow: {error}")
            continue
        if DEPENDABOT_IDENTITY.search(text) and (
            AUTO_MERGE_MARKER.search(text) or has_dependabot_gh_merge(text)
        ):
            errors.append(f"{workflow.relative_to(root)}: Dependabot auto-merge is forbidden")
        if "renovatebot/" in text.casefold():
            errors.append(f"{workflow.relative_to(root)}: Renovate duplicates Dependabot ownership")
    return errors


def validate_stable_only(root):
    errors = []
    try:
        sdk = load_json(root / "global.json")["sdk"]
        if sdk.get("allowPrerelease") is not False or SEMVER_PRERELEASE.fullmatch(
            str(sdk.get("version", ""))
        ):
            errors.append("global.json: stable-only dependency policy is required")
    except (OSError, ValueError, KeyError, TypeError) as error:
        errors.append(f"global.json: stable-only policy cannot be verified: {error}")

    try:
        inventory = load_json(root / "config" / "supply-chain.json")["inventory"]
        if not isinstance(inventory, list):
            raise ValueError("inventory must be an array")
        for item in inventory:
            if not isinstance(item, dict):
                raise ValueError("inventory entries must be objects")
            version = item.get("version")
            prerelease = (
                UNSTABLE_CONTAINER_TAG.search(str(version))
                if item.get("kind") == "container"
                else SEMVER_PRERELEASE.fullmatch(str(version))
            )
            if prerelease:
                errors.append(f"{item.get('name', '<unnamed>')}: stable-only versions are required")
    except (OSError, ValueError, KeyError, TypeError) as error:
        errors.append(f"config/supply-chain.json: stable-only policy cannot be verified: {error}")
    return errors


def validate_update(entry):
    ecosystem = entry.get("package-ecosystem", "<unknown>")
    errors = []
    if set(entry) != UPDATE_KEYS:
        errors.append(f"{ecosystem}: unsupported or missing Dependabot options")
    if entry.get("schedule") != SCHEDULE:
        errors.append(f"{ecosystem}: ordinary updates must run Monday at 06:00 Europe/Paris")
    cooldown = entry.get("cooldown")
    if (
        not isinstance(cooldown, dict)
        or set(cooldown) != {"default-days"}
        or type(cooldown.get("default-days")) is not int
        or cooldown["default-days"] != 3
    ):
        errors.append(f"{ecosystem}: ordinary updates require an unbypassed three-day cooldown")
    limit = entry.get("open-pull-requests-limit")
    labels = entry.get("labels")
    if (
        type(limit) is not int
        or not 1 <= limit <= 5
        or not isinstance(labels, list)
        or "dependencies" not in labels
        or f"ecosystem:{ecosystem}" not in labels
        or any(not isinstance(label, str) or not label for label in labels)
    ):
        errors.append(f"{ecosystem}: pull requests must be bounded and labeled")

    groups = entry.get("groups")
    expected_name = f"ordinary-{ecosystem}"
    if not isinstance(groups, dict):
        errors.append(f"{ecosystem}: ordinary patch/minor grouping is required")
        return errors
    security_groups = [
        name
        for name, group in groups.items()
        if name != expected_name
        and isinstance(group, dict)
        and group.get("applies-to") == "security-updates"
    ]
    if security_groups:
        errors.append(f"{ecosystem}: security updates must remain isolated")
    expected_group = {
        "applies-to": "version-updates",
        "patterns": ["*"],
        "update-types": ["minor", "patch"],
    }
    if set(groups) != {expected_name} or groups.get(expected_name) != expected_group:
        errors.append(f"{ecosystem}: ordinary patch/minor grouping is required")
    return errors


def validate_config(root):
    path = root / ".github" / "dependabot.yml"
    try:
        config = load_dependabot_yaml(path)
    except (OSError, ValueError, TypeError) as error:
        return [f".github/dependabot.yml: invalid strict YAML/JSON: {error}"]
    if not isinstance(config, dict) or set(config) != {"version", "updates"}:
        return [".github/dependabot.yml: only version and updates are permitted"]
    version = config.get("version")
    if type(version) is not int or version != 2 or not isinstance(config.get("updates"), list):
        return [".github/dependabot.yml: version 2 and an updates array are required"]

    errors = []
    ownership = []
    for entry in config["updates"]:
        if not isinstance(entry, dict):
            errors.append("Dependabot update entries must be objects")
            continue
        ownership.append((entry.get("package-ecosystem"), entry.get("directory")))
        errors.extend(validate_update(entry))
    expected = list(ECOSYSTEMS.items())
    if sorted(ownership, key=str) != sorted(expected, key=str):
        errors.append("NuGet, Docker, Helm, and GitHub Actions must each be owned exactly once")
    return errors


def main():
    root = Path.cwd()
    errors = validate_config(root) + validate_ownership(root) + validate_stable_only(root)
    for error in errors:
        print(error, file=sys.stderr)
    return int(bool(errors))


if __name__ == "__main__":
    raise SystemExit(main())
