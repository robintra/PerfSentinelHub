#!/usr/bin/env python3
"""Require a signed stable release tag at an exact checked-out commit."""

from __future__ import annotations

import re
import subprocess
import sys


TAG = re.compile(r"^v0[.][0-9]+[.][0-9]+$")
COMMIT = re.compile(r"^[0-9a-f]{40}$")


def git(*arguments: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(("git", *arguments), text=True, capture_output=True, check=False)


def main(argv=None) -> int:
    arguments = sys.argv[1:] if argv is None else argv
    if len(arguments) != 2 or TAG.fullmatch(arguments[0]) is None or COMMIT.fullmatch(arguments[1]) is None:
        print("usage: check-release-tag.py v0.MINOR.PATCH COMMIT", file=sys.stderr)
        return 2
    tag, expected_commit = arguments
    if git("cat-file", "-t", tag).stdout.strip() != "tag":
        print("error: release tag must be an annotated tag object", file=sys.stderr)
        return 1
    if git("verify-tag", tag).returncode != 0:
        print("error: release tag signature is not valid", file=sys.stderr)
        return 1
    target = git("rev-list", "-n", "1", tag)
    head = git("rev-parse", "HEAD")
    if target.returncode != 0 or head.returncode != 0 or target.stdout.strip() != expected_commit or head.stdout.strip() != expected_commit:
        print("error: release tag target and checked-out commit must equal the expected commit", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
