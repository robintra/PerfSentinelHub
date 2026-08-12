#!/usr/bin/env python3
"""Require a signed stable release tag at an exact checked-out commit."""

from __future__ import annotations

import base64
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path


TAG = re.compile(r"^v0[.][0-9]+[.][0-9]+$")
COMMIT = re.compile(r"^[0-9a-f]{40}$")
PRINCIPAL = re.compile(r"^[A-Za-z0-9.!#$%&'*+/=?^_`{|}~-]+@[A-Za-z0-9.-]+$")
REPOSITORY = "robintra/PerfSentinelHub"
CONFIG_FIELDS = {"schema_version", "release_tag", "github"}
TAG_IDENTITY_FIELDS = {"principal", "key_type", "public_key", "fingerprint"}
GITHUB_FIELDS = {
    "oidc_issuer",
    "repository",
    "repository_url",
    "release_workflow",
    "tag_ref_template",
    "workflow_identity_template",
}


def git(*arguments: str) -> subprocess.CompletedProcess[str]:
    environment = os.environ.copy()
    environment.update({"GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1", "LC_ALL": "C"})
    return subprocess.run(("git", *arguments), text=True, capture_output=True, check=False, env=environment)


def load_json(path: Path):
    def no_duplicates(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"duplicate key {key!r}")
            result[key] = value
        return result

    try:
        return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=no_duplicates)
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"cannot read strict JSON: {error}") from error


def ssh_string(blob: bytes, offset: int) -> tuple[bytes, int]:
    if len(blob) - offset < 4:
        raise ValueError("truncated SSH public key")
    length = int.from_bytes(blob[offset : offset + 4])
    start = offset + 4
    end = start + length
    if end > len(blob):
        raise ValueError("truncated SSH public key")
    return blob[start:end], end


def signing_identity(path: Path) -> dict[str, str]:
    payload = load_json(path)
    if (
        not isinstance(payload, dict)
        or set(payload) != CONFIG_FIELDS
        or type(payload.get("schema_version")) is not int
        or payload["schema_version"] != 1
    ):
        raise ValueError("top-level schema is not closed version 1")
    release_tag = payload.get("release_tag")
    github = payload.get("github")
    if not isinstance(release_tag, dict) or set(release_tag) != TAG_IDENTITY_FIELDS:
        raise ValueError("release_tag schema is not closed")
    if not isinstance(github, dict) or set(github) != GITHUB_FIELDS:
        raise ValueError("github schema is not closed")

    principal = release_tag.get("principal")
    key_type = release_tag.get("key_type")
    public_key = release_tag.get("public_key")
    fingerprint = release_tag.get("fingerprint")
    if not isinstance(principal, str) or PRINCIPAL.fullmatch(principal) is None:
        raise ValueError("release tag principal must be one email identity")
    if key_type != "ssh-ed25519" or not isinstance(public_key, str):
        raise ValueError("release tag key must be SSH Ed25519")
    parts = public_key.split(" ")
    if len(parts) != 2 or parts[0] != key_type:
        raise ValueError("public key must contain exactly its type and base64 payload")
    try:
        blob = base64.b64decode(parts[1], validate=True)
    except ValueError as error:
        raise ValueError("public key payload is not canonical base64") from error
    if base64.b64encode(blob).decode("ascii") != parts[1]:
        raise ValueError("public key payload is not canonical base64")
    embedded_type, offset = ssh_string(blob, 0)
    key_bytes, offset = ssh_string(blob, offset)
    if embedded_type != b"ssh-ed25519" or len(key_bytes) != 32 or offset != len(blob):
        raise ValueError("public key is not one canonical Ed25519 key")
    actual_fingerprint = "SHA256:" + base64.b64encode(hashlib.sha256(blob).digest()).decode("ascii").rstrip("=")
    if fingerprint != actual_fingerprint:
        raise ValueError("public key fingerprint differs from the exact key")

    expected_github = {
        "oidc_issuer": "https://token.actions.githubusercontent.com",
        "repository": REPOSITORY,
        "repository_url": f"https://github.com/{REPOSITORY}",
        "release_workflow": ".github/workflows/release.yml",
        "tag_ref_template": "refs/tags/v{version}",
        "workflow_identity_template": f"https://github.com/{REPOSITORY}/.github/workflows/release.yml@refs/tags/v{{version}}",
    }
    if github != expected_github:
        raise ValueError("GitHub repository, workflow, ref, or OIDC identity differs from the release contract")
    runtime_repository = os.environ.get("GITHUB_REPOSITORY")
    if runtime_repository is not None and runtime_repository != REPOSITORY:
        raise ValueError("runtime GitHub repository differs from the approved repository")
    return release_tag


def tag_principal(tag: str) -> str:
    tag_object = git("cat-file", "tag", tag)
    if tag_object.returncode != 0:
        raise ValueError("annotated release tag cannot be read")
    matches = re.findall(r"(?m)^tagger .* <([^<>\n]+)> [0-9]+ [+-][0-9]{4}$", tag_object.stdout)
    if len(matches) != 1:
        raise ValueError("annotated release tag must contain one canonical tagger")
    return matches[0]


def restore_local_config(key: str, previous: str | None):
    if previous is None:
        git("config", "--local", "--unset-all", key)
    else:
        git("config", "--local", "--replace-all", key, previous)


def verify_signature(tag: str, identity: dict[str, str]) -> bool:
    previous_format = git("config", "--local", "--get", "gpg.format")
    previous_signers = git("config", "--local", "--get", "gpg.ssh.allowedSignersFile")
    old_format = previous_format.stdout.strip() if previous_format.returncode == 0 else None
    old_signers = previous_signers.stdout.strip() if previous_signers.returncode == 0 else None
    with tempfile.TemporaryDirectory(prefix="perf-sentinel-release-signers-") as directory:
        allowed_signers = Path(directory) / "allowed_signers"
        allowed_signers.write_text(f'{identity["principal"]} {identity["public_key"]}\n', encoding="ascii")
        allowed_signers.chmod(0o600)
        try:
            if git("config", "--local", "gpg.format", "ssh").returncode != 0 or git(
                "config", "--local", "gpg.ssh.allowedSignersFile", str(allowed_signers)
            ).returncode != 0:
                return False
            verified = git("verify-tag", "--format=%GS%x00%GF", tag)
        finally:
            restore_local_config("gpg.format", old_format)
            restore_local_config("gpg.ssh.allowedSignersFile", old_signers)
    if verified.returncode != 0:
        return False
    reported = verified.stdout.rstrip("\n").split("\0")
    if len(reported) == 2 and all(reported):
        return reported == [identity["principal"], identity["fingerprint"]]
    return True


def main(argv=None) -> int:
    arguments = sys.argv[1:] if argv is None else argv
    if len(arguments) != 2 or TAG.fullmatch(arguments[0]) is None or COMMIT.fullmatch(arguments[1]) is None:
        print("usage: check-release-tag.py v0.MINOR.PATCH COMMIT", file=sys.stderr)
        return 2
    tag, expected_commit = arguments
    try:
        identity = signing_identity(Path("config/signing-identities.json"))
    except ValueError as error:
        print(f"error: signing identity configuration is invalid: {error}", file=sys.stderr)
        return 1
    if git("cat-file", "-t", tag).stdout.strip() != "tag":
        print("error: release tag must be an annotated tag object", file=sys.stderr)
        return 1
    try:
        principal = tag_principal(tag)
    except ValueError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    if principal != identity["principal"]:
        print("error: release tag principal differs from the approved principal", file=sys.stderr)
        return 1
    if not verify_signature(tag, identity):
        print("error: release tag signature is not valid for the approved SSH identity", file=sys.stderr)
        return 1
    target = git("rev-list", "-n", "1", tag)
    head = git("rev-parse", "HEAD")
    if target.returncode != 0 or head.returncode != 0 or target.stdout.strip() != expected_commit or head.stdout.strip() != expected_commit:
        print("error: release tag target and checked-out commit must equal the expected commit", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
