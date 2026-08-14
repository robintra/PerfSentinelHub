#!/usr/bin/env python3
"""Validate temporary security exceptions and fail closed."""

from __future__ import annotations

import json
import re
import sys
from datetime import date, datetime, timedelta
from pathlib import Path, PurePosixPath
from zoneinfo import ZoneInfo


FIELDS = {"advisory", "exposure", "owner", "expires", "paths"}
GHSA_SEGMENT = "[23456789cfghjmpqrvwxy]{4}"
GHSA_ADVISORY = re.compile(rf"^GHSA-{GHSA_SEGMENT}-{GHSA_SEGMENT}-{GHSA_SEGMENT}$")
CVE_ADVISORY = re.compile(r"^CVE-(?:1999|2[0-9]{3})-(?:0[0-9]{3}|[1-9][0-9]{3,})$")
EXPIRY = re.compile(r"^[0-9]{4}-[0-9]{2}-[0-9]{2}$")
PATH = re.compile(r"^[A-Za-z0-9._/-]+$")


def unique_object(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def valid_path(value: object) -> bool:
    if not isinstance(value, str) or PATH.fullmatch(value) is None:
        return False
    path = PurePosixPath(value)
    return (
        not path.is_absolute()
        and value == path.as_posix()
        and all(part not in {"", ".", ".."} for part in path.parts)
    )


def expiry_errors(expires: object, label: str, today: date) -> list[str]:
    try:
        if not isinstance(expires, str) or EXPIRY.fullmatch(expires) is None:
            raise ValueError
        expiry = date.fromisoformat(expires)
    except ValueError:
        return [f"{label}: expires must be a valid YYYY-MM-DD date"]
    if expiry < today:
        return [f"{label}: security exception expired on {expires}"]
    if expiry > today + timedelta(days=90):
        return [f"{label}: expires must be no more than 90 days away"]
    return []


def exception_entry_errors(entry: object, label: str, advisories: set[str], today: date) -> list[str]:
    if not isinstance(entry, dict) or set(entry) != FIELDS:
        return [f"{label}: advisory, exposure, owner, expires, and paths are required"]

    errors = []
    advisory = entry["advisory"]
    canonical = isinstance(advisory, str) and (
        GHSA_ADVISORY.fullmatch(advisory) is not None or CVE_ADVISORY.fullmatch(advisory) is not None
    )
    if not canonical:
        errors.append(f"{label}: advisory must be a canonical GHSA or CVE identifier")
    elif advisory in advisories:
        errors.append(f"{label}: duplicate advisory {advisory}")
    else:
        advisories.add(advisory)

    for field in ("exposure", "owner"):
        value = entry[field]
        if (
            not isinstance(value, str)
            or not value.strip()
            or value != value.strip()
            or any(ord(character) < 32 or ord(character) == 127 for character in value)
        ):
            errors.append(f"{label}: {field} must be a non-empty canonical string")

    errors.extend(expiry_errors(entry["expires"], label, today))

    paths = entry["paths"]
    if (
        not isinstance(paths, list)
        or not paths
        or any(not valid_path(path) for path in paths)
        or len(paths) != len(set(paths))
    ):
        errors.append(f"{label}: paths must be unique canonical repository-relative paths")
    return errors


def validate(payload: object, today: date) -> list[str]:
    if not isinstance(payload, dict) or set(payload) != {"schema_version", "exceptions"}:
        return ["only schema_version and exceptions are permitted"]
    if type(payload["schema_version"]) is not int or payload["schema_version"] != 1:
        return ["schema_version must be integer 1"]
    exceptions = payload["exceptions"]
    if not isinstance(exceptions, list):
        return ["exceptions must be an array"]

    errors = []
    advisories = set()
    for index, entry in enumerate(exceptions, start=1):
        errors.extend(exception_entry_errors(entry, f"exception {index}", advisories, today))
    return errors


def main() -> int:
    path = Path("config/security-exceptions.json")
    try:
        payload = json.loads(
            path.read_text(encoding="utf-8"), object_pairs_hook=unique_object
        )
    except (OSError, ValueError, TypeError) as error:
        print(f"invalid config/security-exceptions.json: {error}", file=sys.stderr)
        return 1

    errors = validate(payload, datetime.now(ZoneInfo("Europe/Paris")).date())
    for error in errors:
        print(error, file=sys.stderr)
    return int(bool(errors))


if __name__ == "__main__":
    raise SystemExit(main())
