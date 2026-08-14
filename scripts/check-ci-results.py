#!/usr/bin/env python3
"""Validate the aggregate CI result matrix and fail closed."""

from __future__ import annotations

import argparse
import json
import re
import sys


ALWAYS_REQUIRED = {
    "action-pins",
    "changes",
    "markdown",
    "secret-scan",
}
EXPENSIVE = {
    "dependency-review",
    "helm",
    "native-aot",
    "oci",
    "quality-tests-coverage",
    "sonar",
}
VALIDATION_JOBS = ALWAYS_REQUIRED | EXPENSIVE
RESULTS = {"success", "failure", "cancelled", "skipped"}
HEAD_SHA = re.compile(r"^[0-9a-f]{40}$")
REPOSITORY = re.compile(r"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")


def unique_object(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def expected_results(mode: str, decision: str) -> dict[str, set[str]]:
    expected = {job: {"success"} for job in VALIDATION_JOBS}
    expected["validate-dispatch"] = {"success"} if mode == "dispatch" else {"skipped"}
    if decision == "docs":
        for job in EXPENSIVE:
            expected[job] = {"success", "skipped"}
    if mode in {"fork", "dispatch"}:
        expected["sonar"] = {"skipped"}
    return expected


def expected_outputs(job: str, mode: str) -> set[str]:
    if job == "changes":
        return {"decision"}
    if job == "validate-dispatch" and mode == "dispatch":
        return {"head_repository", "head_sha"}
    return set()


def job_entry_errors(job: str, value: object, allowed: set[str], mode: str, decision: str) -> list[str]:
    if not isinstance(value, dict) or set(value) != {"outputs", "result"}:
        return [f"{job}: exactly outputs and result are required"]
    result = value["result"]
    outputs = value["outputs"]
    errors = []
    if not isinstance(result, str) or result not in RESULTS:
        errors.append(f"{job}: invalid result")
    elif result not in allowed:
        errors.append(f"{job}: result {result} is not allowed for {mode}/{decision}")
    if not isinstance(outputs, dict):
        errors.append(f"{job}: outputs must be an object")
    elif set(outputs) != expected_outputs(job, mode):
        errors.append(f"{job}: missing or unexpected outputs")
    elif any(not isinstance(key, str) or not isinstance(item, str) for key, item in outputs.items()):
        errors.append(f"{job}: output names and values must be strings")
    return errors


def validate_needs(payload: object, mode: str, decision: str) -> list[str]:
    expected = expected_results(mode, decision)
    if not isinstance(payload, dict):
        return ["needs JSON must be an object"]

    errors = []
    missing = set(expected) - set(payload)
    unexpected = set(payload) - set(expected)
    errors.extend(f"missing required job: {job}" for job in sorted(missing))
    errors.extend(f"unexpected job: {job}" for job in sorted(unexpected))

    for job in sorted(set(expected) & set(payload)):
        errors.extend(job_entry_errors(job, payload[job], expected[job], mode, decision))

    changes = payload.get("changes")
    if (
        isinstance(changes, dict)
        and isinstance(changes.get("outputs"), dict)
        and changes["outputs"].get("decision") != decision
    ):
        errors.append("changes: decision output does not match the checked decision")

    preflight = payload.get("validate-dispatch")
    if mode == "dispatch" and isinstance(preflight, dict):
        outputs = preflight.get("outputs")
        if isinstance(outputs, dict):
            head_sha = outputs.get("head_sha")
            repository = outputs.get("head_repository")
            if not isinstance(head_sha, str) or HEAD_SHA.fullmatch(head_sha) is None:
                errors.append("validate-dispatch: head_sha is not canonical")
            if not isinstance(repository, str) or REPOSITORY.fullmatch(repository) is None:
                errors.append("validate-dispatch: head_repository is not canonical")
    return errors


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--mode", required=True, choices=("internal", "fork", "dispatch"))
    parser.add_argument("--decision", required=True, choices=("code", "docs"))
    parser.add_argument("--needs-json", required=True)
    arguments = parser.parse_args(argv)
    try:
        payload = json.loads(arguments.needs_json, object_pairs_hook=unique_object)
    except (TypeError, ValueError) as error:
        print(f"invalid needs JSON: {error}", file=sys.stderr)
        return 1
    errors = validate_needs(payload, arguments.mode, arguments.decision)
    for error in errors:
        print(error, file=sys.stderr)
    if errors:
        return 1
    print(f"gate passed for {arguments.mode}/{arguments.decision}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
