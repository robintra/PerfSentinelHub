#!/usr/bin/env python3
"""Create or verify the closed PerfSentinel Hub release manifest."""

from __future__ import annotations

import argparse
import base64
import hashlib
import importlib.util
import json
import os
import re
import shutil
import sys
import tarfile
import tempfile
from pathlib import Path, PurePosixPath


PROVENANCE_FILE = "release.provenance.sigstore.json"
RELEASE_MANIFEST_FILE = "release-manifest.json"
RIDS = ("linux-x64", "linux-arm64", "osx-arm64", "win-x64")
VERSION = re.compile(r"^0\.[0-9]+\.[0-9]+$")
STABLE_TAG = re.compile(r"^v0[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)$")
COMMIT = re.compile(r"^[0-9a-f]{40}$")
DIGEST = re.compile(r"^sha256:([0-9a-f]{64})$")
REPOSITORY = re.compile(r"^https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")
PUBLIC_REPOSITORY = "robintra/PerfSentinelHub"
PUBLIC_RELEASE_URL = re.compile(
    rf"^https://github[.]com/{re.escape(PUBLIC_REPOSITORY)}/releases/tag/(v0[.][0-9]+[.][0-9]+)$"
)
IMAGE_REPOSITORY = re.compile(
    r"^(?:[a-z0-9]+(?:[.-][a-z0-9]+)*(?::[0-9]+)?/)?"
    r"[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*$"
)
IMAGE_HELPER = '''{{- define "perf-sentinel-hub.image" -}}
{{- $repositoryPattern := "^(?:[a-z0-9]+(?:[.-][a-z0-9]+)*(?::[0-9]+)?/)?[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*$" -}}
{{- if not (regexMatch $repositoryPattern .Values.image.repository) -}}
{{- fail "image.repository must contain neither a tag nor a digest" -}}
{{- end -}}
{{- if not (regexMatch "^sha256:[0-9a-f]{64}$" .Values.image.digest) -}}
{{- fail "image.digest must be an immutable sha256 digest" -}}
{{- end -}}
{{- printf "%s@%s" .Values.image.repository .Values.image.digest -}}
{{- end }}'''
PROVENANCE_TYPE = "https://slsa.dev/provenance/v1"
SBOM_TYPE = "https://spdx.dev/Document/v2.3"
SIGSTORE_MEDIA_TYPE = "application/vnd.dev.sigstore.bundle.v0.3+json"
FILE_FIELDS = {"name", "sha256"}
SUBJECT_FIELDS = {
    "name",
    "kind",
    "target",
    "sha256",
    "sbom",
    "signature_bundle",
    "sbom_attestation",
    "provenance",
    "source_commit",
}


def image_checker_module():
    path = Path(__file__).with_name("check-image-manifest.py")
    spec = importlib.util.spec_from_file_location("check_image_manifest", path)
    if spec is None or spec.loader is None:
        raise ValueError("image manifest checker cannot be loaded")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def load_json_bytes(content: bytes, description: str):
    def no_duplicates(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError(f"{description} contains duplicate JSON key {key!r}")
            result[key] = value
        return result

    try:
        return json.loads(content.decode("utf-8"), object_pairs_hook=no_duplicates)
    except (UnicodeError, json.JSONDecodeError) as error:
        raise ValueError(f"{description} is not valid JSON: {error}") from error


def load_json(path: Path, description: str):
    try:
        return load_json_bytes(path.read_bytes(), description)
    except OSError as error:
        raise ValueError(f"{description} cannot be read: {error}") from error


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    try:
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
    except OSError as error:
        raise ValueError(f"{path.name} cannot be hashed: {error}") from error
    return digest.hexdigest()


def regular_files(root: Path) -> dict[str, Path]:
    if not root.is_dir() or root.is_symlink():
        raise ValueError("release root must be a regular directory")
    files = {}
    for entry in root.iterdir():
        if entry.is_symlink() or not entry.is_file():
            raise ValueError("release root may contain only regular files")
        if entry.name in files or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", entry.name):
            raise ValueError("release filenames must be unique and canonical")
        files[entry.name] = entry
    return files


def subject_names(version: str):
    subjects = []
    for rid in RIDS:
        extension = "zip" if rid == "win-x64" else "tar.gz"
        subjects.append((f"perf-sentinel-hub-{version}-{rid}.{extension}", "native", rid))
        subjects.append((f"perf-sentinel-hub-{version}-{rid}-symbols.{extension}", "symbols", rid))
    subjects.extend(
        (
            (f"perf-sentinel-hub-{version}.oci.tar", "oci", "linux/amd64,linux/arm64"),
            (f"perf-sentinel-hub-{version}.tgz", "helm", "kubernetes"),
        )
    )
    return subjects


def expected_files(version: str):
    names = {PROVENANCE_FILE}
    for name, _, _ in subject_names(version):
        names.update((name, f"{name}.spdx.json", f"{name}.sigstore.json", f"{name}.sbom.sigstore.json"))
    return names


def decode_base64(value, description: str) -> bytes:
    if not isinstance(value, str):
        raise ValueError(f"{description} must be base64 text")
    try:
        return base64.b64decode(value, validate=True)
    except ValueError as error:
        raise ValueError(f"{description} is not canonical base64") from error


def statement_from_bundle(path: Path, description: str):
    bundle = load_json(path, description)
    if not isinstance(bundle, dict) or bundle.get("mediaType") != SIGSTORE_MEDIA_TYPE:
        raise ValueError(f"{description} media type is invalid")
    envelope = bundle.get("dsseEnvelope")
    if not isinstance(envelope, dict) or envelope.get("payloadType") != "application/vnd.in-toto+json":
        raise ValueError(f"{description} DSSE envelope is invalid")
    signatures = envelope.get("signatures")
    if not isinstance(signatures, list) or not signatures:
        raise ValueError(f"{description} has no DSSE signature")
    statement = load_json_bytes(decode_base64(envelope.get("payload"), f"{description} payload"), f"{description} statement")
    if not isinstance(statement, dict) or statement.get("_type") not in {
        "https://in-toto.io/Statement/v0.1",
        "https://in-toto.io/Statement/v1",
    }:
        raise ValueError(f"{description} statement type is invalid")
    return statement


def statement_subjects(statement, description: str):
    subjects = statement.get("subject")
    if not isinstance(subjects, list) or not subjects:
        raise ValueError(f"{description} subjects are required")
    result = []
    for subject in subjects:
        if not isinstance(subject, dict) or set(subject) != {"name", "digest"} or not isinstance(subject["name"], str):
            raise ValueError(f"{description} subject is invalid")
        digest = subject["digest"]
        if not isinstance(digest, dict) or set(digest) != {"sha256"} or not re.fullmatch(r"[0-9a-f]{64}", str(digest["sha256"])):
            raise ValueError(f"{description} subject digest is invalid")
        result.append((subject["name"], digest["sha256"]))
    if len(result) != len(set(result)):
        raise ValueError(f"{description} contains duplicate subjects")
    return result


def validate_signature_bundle(path: Path, expected_digest: str):
    bundle = load_json(path, f"{path.name} signature bundle")
    if not isinstance(bundle, dict) or bundle.get("mediaType") != SIGSTORE_MEDIA_TYPE:
        raise ValueError(f"{path.name}: signature bundle media type is invalid")
    signature = bundle.get("messageSignature")
    message_digest = signature.get("messageDigest") if isinstance(signature, dict) else None
    if not isinstance(message_digest, dict) or message_digest.get("algorithm") != "SHA2_256":
        raise ValueError(f"{path.name}: signature bundle digest is invalid")
    actual = decode_base64(message_digest.get("digest"), f"{path.name} signature bundle digest")
    if actual != bytes.fromhex(expected_digest):
        raise ValueError(f"{path.name}: signature bundle digest differs from subject")


def validate_sbom(path: Path, subject_name: str):
    sbom = load_json(path, f"{path.name} SPDX SBOM")
    if not isinstance(sbom, dict) or sbom.get("spdxVersion") != "SPDX-2.3" or sbom.get("SPDXID") != "SPDXRef-DOCUMENT":
        raise ValueError(f"{path.name}: SPDX SBOM structure is invalid")
    if sbom.get("dataLicense") != "CC0-1.0" or sbom.get("name") != subject_name:
        raise ValueError(f"{path.name}: SPDX SBOM identity is invalid")
    if not isinstance(sbom.get("documentNamespace"), str) or not isinstance(sbom.get("creationInfo"), dict):
        raise ValueError(f"{path.name}: SPDX SBOM metadata is incomplete")


def validate_sbom_attestation(path: Path, subject: tuple[str, str]):
    statement = statement_from_bundle(path, f"{path.name} SBOM attestation")
    if statement.get("predicateType") != SBOM_TYPE or statement_subjects(statement, f"{path.name} SBOM attestation") != [subject]:
        raise ValueError(f"{path.name}: SBOM attestation subject or predicate differs")


def safe_chart_members(path: Path):
    members = {}
    try:
        with tarfile.open(path, "r:gz") as archive:
            for member in archive:
                pure = PurePosixPath(member.name)
                if member.name != pure.as_posix() or pure.is_absolute() or ".." in pure.parts:
                    raise ValueError("chart contains an unsafe path")
                if member.isdir():
                    continue
                if not member.isfile() or member.name in members:
                    raise ValueError("chart entries must be unique regular files")
                stream = archive.extractfile(member)
                if stream is None:
                    raise ValueError("chart entry cannot be read")
                members[member.name] = stream.read()
    except (OSError, tarfile.TarError) as error:
        raise ValueError(f"chart archive is invalid: {error}") from error
    return members


def validate_chart(path: Path, version: str, image_digest: str):
    members = safe_chart_members(path)
    prefix = "perf-sentinel-hub/"
    try:
        chart = members[prefix + "Chart.yaml"].decode("utf-8")
        values = members[prefix + "values.yaml"].decode("utf-8")
        helpers = members[prefix + "templates/_helpers.tpl"].decode("utf-8")
        deployment = members[prefix + "templates/deployment.yaml"].decode("utf-8")
    except (KeyError, UnicodeError) as error:
        raise ValueError(f"chart required files are missing or invalid: {error}") from error
    if f"version: {version}\n" not in chart or f'appVersion: "{version}"\n' not in chart:
        raise ValueError("chart version differs from release")
    repositories = re.findall(r"(?m)^ {2}repository: ([^\s#]+)\s*$", values)
    if len(repositories) != 1 or IMAGE_REPOSITORY.fullmatch(repositories[0]) is None:
        raise ValueError("chart image repository must contain neither a tag nor a digest")
    if re.search(r"(?m)^\s+tag\s*:", values) or values.count(f"  digest: {image_digest}\n") != 1:
        raise ValueError("chart must contain the immutable image digest and no tag")
    helper_definitions = sum(
        content.count(b'define "perf-sentinel-hub.image"')
        for name, content in members.items()
        if name.startswith(prefix + "templates/")
    )
    if helper_definitions != 1 or IMAGE_HELPER not in helpers:
        raise ValueError("chart image helper must validate and join the repository and digest")
    rendered_image_sources = re.findall(r"(?m)^[ \t]+(?:- )?image:[ \t]*(\S.*)$", deployment)
    if rendered_image_sources != ['{{ include "perf-sentinel-hub.image" . | quote }}']:
        raise ValueError("chart deployment must reference repository by immutable digest")


def validate_provenance(path: Path, expected_subjects: list[tuple[str, str]]):
    statement = statement_from_bundle(path, "release provenance")
    if statement.get("predicateType") != PROVENANCE_TYPE:
        raise ValueError("release provenance predicate type is invalid")
    if sorted(statement_subjects(statement, "release provenance")) != sorted(expected_subjects):
        raise ValueError("release provenance subjects differ from exact release subjects")


def validate_evidence(files: dict[str, Path], subjects, image_digest: str, version: str):
    subject_digests = [(name, sha256(files[name])) for name, _, _ in subjects]
    for name, digest in subject_digests:
        validate_sbom(files[f"{name}.spdx.json"], name)
        validate_signature_bundle(files[f"{name}.sigstore.json"], digest)
        validate_sbom_attestation(files[f"{name}.sbom.sigstore.json"], (name, digest))
    validate_provenance(files[PROVENANCE_FILE], subject_digests)
    checker = image_checker_module()
    actual_image_digest = checker.validated_digest(
        files[f"perf-sentinel-hub-{version}.oci.tar"],
        frozenset((('linux', 'amd64'), ('linux', 'arm64'))),
    )
    if actual_image_digest != image_digest:
        raise ValueError("immutable image digest differs from the OCI manifest")
    validate_chart(files[f"perf-sentinel-hub-{version}.tgz"], version, image_digest)
    return dict(subject_digests)


def validate_inputs(version: str, source_commit: str, source_repository: str, image_digest: str):
    if VERSION.fullmatch(version) is None:
        raise ValueError("version must be canonical 0.MINOR.PATCH")
    if COMMIT.fullmatch(source_commit) is None:
        raise ValueError("source commit must be a lowercase full SHA")
    if REPOSITORY.fullmatch(source_repository) is None:
        raise ValueError("source repository must be a canonical GitHub HTTPS URL")
    if DIGEST.fullmatch(image_digest) is None:
        raise ValueError("immutable image digest must be canonical sha256")


def ensure_closed(files, version):
    expected = expected_files(version)
    actual = set(files)
    extra = sorted(actual - expected)
    missing = sorted(expected - actual)
    unsupported = next((name for name in extra if re.match(rf"^perf-sentinel-hub-{re.escape(version)}-(?:osx-x64|win-arm64)", name)), None)
    if unsupported is not None:
        raise ValueError(f"unsupported native target in {unsupported}")
    if missing:
        missing_subjects = [name for name in missing if not name.endswith((".json", ".sigstore.json"))]
        if any("-symbols." in name for name in missing_subjects):
            raise ValueError(f"symbols archive missing: {', '.join(missing)}")
        if missing_subjects:
            raise ValueError(f"native target or release subject missing: {', '.join(missing)}")
        raise ValueError(f"release evidence missing: {', '.join(missing)}")
    if extra:
        raise ValueError(f"unlisted files in release root: {', '.join(extra)}")


def manifest_payload(root: Path, version: str, source_commit: str, source_repository: str, image_digest: str):
    validate_inputs(version, source_commit, source_repository, image_digest)
    files = regular_files(root)
    ensure_closed(files, version)
    subjects = subject_names(version)
    digests = validate_evidence(files, subjects, image_digest, version)
    return {
        "schema_version": 1,
        "version": version,
        "source": {"repository": source_repository, "commit": source_commit, "ref": f"refs/tags/v{version}"},
        "image": {"digest": image_digest, "platforms": ["linux/amd64", "linux/arm64"]},
        "files": [{"name": name, "sha256": sha256(path)} for name, path in sorted(files.items())],
        "subjects": [
            {
                "name": name,
                "kind": kind,
                "target": target,
                "sha256": digests[name],
                "sbom": f"{name}.spdx.json",
                "signature_bundle": f"{name}.sigstore.json",
                "sbom_attestation": f"{name}.sbom.sigstore.json",
                "provenance": PROVENANCE_FILE,
                "source_commit": source_commit,
            }
            for name, kind, target in subjects
        ],
        "claims": {"slsa_level": None, "reason": "No SLSA level is claimed; the exact subjects use GitHub build provenance attestations."},
    }


def write_manifest(path: Path, payload):
    if path.exists() and (path.is_symlink() or not path.is_file()):
        raise ValueError("manifest output must be a regular file")
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor, temporary_name = tempfile.mkstemp(prefix=f".{path.name}.", dir=path.parent)
    temporary = Path(temporary_name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(payload, stream, indent=2, sort_keys=True)
            stream.write("\n")
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    except Exception:
        temporary.unlink(missing_ok=True)
        raise


def validated_manifest_shape(manifest: object):
    if not isinstance(manifest, dict) or set(manifest) != {"schema_version", "version", "source", "image", "files", "subjects", "claims"}:
        raise ValueError("release manifest root is not closed")
    if manifest["schema_version"] != 1 or manifest["claims"] != {
        "slsa_level": None,
        "reason": "No SLSA level is claimed; the exact subjects use GitHub build provenance attestations.",
    }:
        raise ValueError("release manifest schema or claims are invalid")
    source = manifest["source"]
    image = manifest["image"]
    if not isinstance(source, dict) or set(source) != {"repository", "commit", "ref"}:
        raise ValueError("release manifest source is invalid")
    if source.get("ref") != f"refs/tags/v{manifest['version']}":
        raise ValueError("release manifest source ref differs from version")
    if not isinstance(image, dict) or image.get("platforms") != ["linux/amd64", "linux/arm64"] or set(image) != {"digest", "platforms"}:
        raise ValueError("release manifest image is invalid")
    validate_inputs(manifest["version"], source["commit"], source["repository"], image["digest"])
    return source, image


def verified_file_hashes(files: dict, listed: object) -> dict:
    if not isinstance(listed, list):
        raise ValueError("release manifest files must be an array")
    expected_hashes = {}
    for entry in listed:
        if not isinstance(entry, dict) or set(entry) != FILE_FIELDS or not isinstance(entry["name"], str) or not re.fullmatch(r"[0-9a-f]{64}", str(entry["sha256"])):
            raise ValueError("release manifest file entry is invalid")
        if entry["name"] in expected_hashes:
            raise ValueError("release manifest contains duplicate files")
        expected_hashes[entry["name"]] = entry["sha256"]
    extras = sorted(set(files) - set(expected_hashes))
    missing = sorted(set(expected_hashes) - set(files))
    if extras:
        raise ValueError(f"unlisted files in release root: {', '.join(extras)}")
    if missing:
        raise ValueError(f"listed release files are missing: {', '.join(missing)}")
    for name, expected in expected_hashes.items():
        if sha256(files[name]) != expected:
            raise ValueError(f"{name}: sha256 mismatch")
    return expected_hashes


def validate_manifest_subjects(manifest: dict, source: dict, expected_hashes: dict, expected_subjects) -> None:
    subjects = manifest["subjects"]
    if not isinstance(subjects, list) or len(subjects) != len(expected_subjects):
        raise ValueError("release manifest subject set is incomplete")
    expected_entries = [
        {
            "name": name,
            "kind": kind,
            "target": target,
            "sha256": expected_hashes[name],
            "sbom": f"{name}.spdx.json",
            "signature_bundle": f"{name}.sigstore.json",
            "sbom_attestation": f"{name}.sbom.sigstore.json",
            "provenance": PROVENANCE_FILE,
            "source_commit": source["commit"],
        }
        for name, kind, target in expected_subjects
    ]
    if subjects != expected_entries or any(not isinstance(entry, dict) or set(entry) != SUBJECT_FIELDS for entry in subjects):
        raise ValueError("release manifest subjects do not match the closed release contract")


def verify_manifest(root: Path, path: Path):
    manifest = load_json(path, "release manifest")
    source, image = validated_manifest_shape(manifest)
    files = regular_files(root)
    ensure_closed(files, manifest["version"])
    expected_hashes = verified_file_hashes(files, manifest["files"])
    expected_subjects = subject_names(manifest["version"])
    validate_manifest_subjects(manifest, source, expected_hashes, expected_subjects)
    validate_evidence(files, expected_subjects, image["digest"], manifest["version"])


def public_release_input(value: str):
    tag = value if STABLE_TAG.fullmatch(value) else None
    if tag is None:
        match = PUBLIC_RELEASE_URL.fullmatch(value)
        tag = match.group(1) if match and STABLE_TAG.fullmatch(match.group(1)) else None
    if tag is None:
        raise ValueError("public release input must be a stable tag or the exact public GitHub Release URL")
    return {
        "repository": PUBLIC_REPOSITORY,
        "tag": tag,
        "url": f"https://github.com/{PUBLIC_REPOSITORY}/releases/tag/{tag}",
    }


def latest_stable(content: bytes) -> str:
    pages = load_json_bytes(content, "GitHub releases response")
    if not isinstance(pages, list) or not all(isinstance(page, list) for page in pages):
        raise ValueError("GitHub releases response must contain every paginated page")
    versions = {}
    for release in (item for page in pages for item in page):
        if not isinstance(release, dict):
            raise ValueError("GitHub release entry must be an object")
        tag = release.get("tag_name")
        draft = release.get("draft")
        prerelease = release.get("prerelease")
        if not isinstance(tag, str) or type(draft) is not bool or type(prerelease) is not bool:
            raise ValueError("GitHub release entry has invalid stable-selection fields")
        match = STABLE_TAG.fullmatch(tag)
        if match is None or draft or prerelease:
            continue
        if tag in versions:
            raise ValueError(f"ambiguous stable release tag {tag}")
        versions[tag] = (int(match.group(1)), int(match.group(2)))
    if not versions:
        raise ValueError("no canonical stable v0 release exists")
    return max(versions, key=versions.get)


def verify_published(root: Path):
    files = regular_files(root)
    if "SHA256SUMS" not in files or RELEASE_MANIFEST_FILE not in files:
        raise ValueError("published release requires SHA256SUMS and release-manifest.json")
    checksums = {}
    pattern = re.compile(r"^([0-9a-f]{64}) {2}([A-Za-z0-9][A-Za-z0-9._-]*)$")
    for line in files["SHA256SUMS"].read_text(encoding="ascii").splitlines():
        match = pattern.fullmatch(line)
        if match is None or match.group(2) in checksums or match.group(2) == "SHA256SUMS":
            raise ValueError("SHA256SUMS is not canonical")
        checksums[match.group(2)] = match.group(1)
    if set(checksums) != set(files) - {"SHA256SUMS"}:
        raise ValueError("SHA256SUMS differs from the exact published asset set")
    for name, digest in checksums.items():
        if sha256(files[name]) != digest:
            raise ValueError(f"{name}: sha256 mismatch")
    with tempfile.TemporaryDirectory(prefix="perf-sentinel-public-release-") as directory:
        verification_root = Path(directory)
        for name, path in files.items():
            if name not in {"SHA256SUMS", RELEASE_MANIFEST_FILE}:
                shutil.copyfile(path, verification_root / name)
        verify_manifest(verification_root, files[RELEASE_MANIFEST_FILE])


def parser():
    result = argparse.ArgumentParser(description=__doc__)
    commands = result.add_subparsers(dest="command", required=True)
    create = commands.add_parser("create")
    create.add_argument("--root", type=Path, required=True)
    create.add_argument("--manifest", type=Path, required=True)
    create.add_argument("--version", required=True)
    create.add_argument("--source-commit", required=True)
    create.add_argument("--source-repository", required=True)
    create.add_argument("--image-digest", required=True)
    verify = commands.add_parser("verify")
    verify.add_argument("--root", type=Path, required=True)
    verify.add_argument("--manifest", type=Path, required=True)
    public_input = commands.add_parser("public-input")
    public_input.add_argument("value")
    commands.add_parser("latest-stable")
    published = commands.add_parser("verify-published")
    published.add_argument("--root", type=Path, required=True)
    return result


def main(argv=None):
    arguments = parser().parse_args(argv)
    try:
        if arguments.command == "create":
            payload = manifest_payload(
                arguments.root,
                arguments.version,
                arguments.source_commit,
                arguments.source_repository,
                arguments.image_digest,
            )
            write_manifest(arguments.manifest, payload)
        elif arguments.command == "verify":
            verify_manifest(arguments.root, arguments.manifest)
        elif arguments.command == "public-input":
            json.dump(public_release_input(arguments.value), sys.stdout, sort_keys=True)
            sys.stdout.write("\n")
        elif arguments.command == "latest-stable":
            print(latest_stable(sys.stdin.buffer.read()))
        else:
            verify_published(arguments.root)
        return 0
    except (OSError, ValueError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
