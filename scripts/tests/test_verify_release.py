import base64
import hashlib
import io
import json
import os
import re
import subprocess
import sys
import tarfile
import tempfile
import unittest
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parents[2]
VERIFIER = REPOSITORY / "scripts" / "verify-release.py"
IMAGE_CHECKER = REPOSITORY / "scripts" / "check-image-manifest.py"
TAG_CHECKER = REPOSITORY / "scripts" / "check-release-tag.py"
VERSION = "0.1.0"
COMMIT = "d61dec54c28bac1a092f97089bb2a426bbef39a6"
RIDS = ("linux-x64", "linux-arm64", "osx-arm64", "win-x64")
HELM_IMAGE_HELPER = b'''{{- define "perf-sentinel-hub.image" -}}
{{- $repositoryPattern := "^(?:[a-z0-9]+(?:[.-][a-z0-9]+)*(?::[0-9]+)?/)?[a-z0-9]+(?:[._-][a-z0-9]+)*(?:/[a-z0-9]+(?:[._-][a-z0-9]+)*)*$" -}}
{{- if not (regexMatch $repositoryPattern .Values.image.repository) -}}
{{- fail "image.repository must contain neither a tag nor a digest" -}}
{{- end -}}
{{- if not (regexMatch "^sha256:[0-9a-f]{64}$" .Values.image.digest) -}}
{{- fail "image.digest must be an immutable sha256 digest" -}}
{{- end -}}
{{- printf "%s@%s" .Values.image.repository .Values.image.digest -}}
{{- end }}
'''


def compact_json(payload):
    return json.dumps(payload, sort_keys=True, separators=(",", ":")).encode()


def dsse_bundle(subjects, predicate_type):
    statement = {
        "_type": "https://in-toto.io/Statement/v1",
        "subject": [
            {"name": name, "digest": {"sha256": digest}}
            for name, digest in subjects
        ],
        "predicateType": predicate_type,
        "predicate": {},
    }
    return compact_json(
        {
            "mediaType": "application/vnd.dev.sigstore.bundle.v0.3+json",
            "dsseEnvelope": {
                "payloadType": "application/vnd.in-toto+json",
                "payload": base64.b64encode(compact_json(statement)).decode(),
                "signatures": [{"sig": base64.b64encode(b"signature").decode()}],
            },
            "verificationMaterial": {"certificate": {"rawBytes": "Y2VydA=="}},
        }
    )


def signature_bundle(digest):
    return compact_json(
        {
            "mediaType": "application/vnd.dev.sigstore.bundle.v0.3+json",
            "messageSignature": {
                "messageDigest": {
                    "algorithm": "SHA2_256",
                    "digest": base64.b64encode(bytes.fromhex(digest)).decode(),
                },
                "signature": base64.b64encode(b"signature").decode(),
            },
            "verificationMaterial": {"certificate": {"rawBytes": "Y2VydA=="}},
        }
    )


class ReleaseFixture:
    def __init__(self, root):
        self.root = root
        self.subjects = []
        for rid in RIDS:
            extension = "zip" if rid == "win-x64" else "tar.gz"
            self.add_subject(f"perf-sentinel-hub-{VERSION}-{rid}.{extension}")
            self.add_subject(f"perf-sentinel-hub-{VERSION}-{rid}-symbols.{extension}")
        self.image = f"perf-sentinel-hub-{VERSION}.oci.tar"
        self.chart = f"perf-sentinel-hub-{VERSION}.tgz"
        image_content = self.oci_archive()
        self.add_subject(self.image, image_content)
        self.add_subject(self.chart, self.chart_archive())
        self.write_evidence()

    def add_subject(self, name, content=None):
        content = content if content is not None else f"subject:{name}\n".encode()
        (self.root / name).write_bytes(content)
        self.subjects.append((name, hashlib.sha256(content).hexdigest()))

    def write_evidence(self):
        for name, digest in self.subjects:
            (self.root / f"{name}.spdx.json").write_bytes(
                compact_json(
                    {
                        "spdxVersion": "SPDX-2.3",
                        "dataLicense": "CC0-1.0",
                        "SPDXID": "SPDXRef-DOCUMENT",
                        "name": name,
                        "documentNamespace": f"https://example.invalid/spdx/{digest}",
                        "creationInfo": {
                            "created": "2026-08-11T00:00:00Z",
                            "creators": ["Tool: syft-1.50.0"],
                        },
                        "packages": [],
                    }
                )
            )
            (self.root / f"{name}.sigstore.json").write_bytes(signature_bundle(digest))
            (self.root / f"{name}.sbom.sigstore.json").write_bytes(
                dsse_bundle([(name, digest)], "https://spdx.dev/Document/v2.3")
            )
        (self.root / "release.provenance.sigstore.json").write_bytes(
            dsse_bundle(self.subjects, "https://slsa.dev/provenance/v1")
        )

    def oci_archive(self, architectures=("amd64", "arm64")):
        manifests = []
        blobs = {}
        config = b"{}"
        config_digest = hashlib.sha256(config).hexdigest()
        blobs[config_digest] = config
        for architecture in architectures:
            manifest = compact_json(
                {
                    "schemaVersion": 2,
                    "mediaType": "application/vnd.oci.image.manifest.v1+json",
                    "config": {"mediaType": "application/vnd.oci.image.config.v1+json", "digest": f"sha256:{config_digest}", "size": len(config)},
                    "layers": [],
                }
            )
            digest = hashlib.sha256(manifest).hexdigest()
            blobs[digest] = manifest
            manifests.append(
                {
                    "mediaType": "application/vnd.oci.image.manifest.v1+json",
                    "digest": f"sha256:{digest}",
                    "size": len(manifest),
                    "platform": {"os": "linux", "architecture": architecture},
                }
            )
        index = compact_json({"schemaVersion": 2, "mediaType": "application/vnd.oci.image.index.v1+json", "manifests": manifests})
        self.oci_digest = f"sha256:{hashlib.sha256(index).hexdigest()}"
        output = io.BytesIO()
        with tarfile.open(fileobj=output, mode="w") as archive:
            self.add_tar_bytes(archive, "oci-layout", b'{"imageLayoutVersion":"1.0.0"}\n')
            self.add_tar_bytes(archive, "index.json", index)
            for digest, content in sorted(blobs.items()):
                self.add_tar_bytes(archive, f"blobs/sha256/{digest}", content)
        return output.getvalue()

    def chart_archive(self, *, mutable=False, repository="ghcr.io/robintra/perf-sentinel-hub"):
        digest = "tag: mutable" if mutable else f"digest: {self.oci_digest}"
        image = 'image: "{{ .Values.image.repository }}:{{ .Values.image.tag }}"' if mutable else 'image: {{ include "perf-sentinel-hub.image" . | quote }}'
        output = io.BytesIO()
        with tarfile.open(fileobj=output, mode="w:gz") as archive:
            self.add_tar_bytes(archive, "perf-sentinel-hub/Chart.yaml", b"apiVersion: v2\nname: perf-sentinel-hub\nversion: 0.1.0\nappVersion: \"0.1.0\"\n")
            self.add_tar_bytes(archive, "perf-sentinel-hub/values.yaml", f"image:\n  repository: {repository}\n  {digest}\n".encode())
            self.add_tar_bytes(archive, "perf-sentinel-hub/templates/_helpers.tpl", HELM_IMAGE_HELPER)
            self.add_tar_bytes(archive, "perf-sentinel-hub/templates/deployment.yaml", f"containers:\n  - {image}\n".encode())
        return output.getvalue()

    @staticmethod
    def add_tar_bytes(archive, name, content):
        info = tarfile.TarInfo(name)
        info.size = len(content)
        info.mtime = 0
        info.mode = 0o644
        archive.addfile(info, io.BytesIO(content))


class VerifyReleaseTests(unittest.TestCase):
    def test_creates_and_verifies_closed_manifest(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fixture = ReleaseFixture(root)
            manifest = root.parent / f"{root.name}-manifest.json"
            result = self.create(root, manifest)
            self.assertEqual(0, result.returncode, result.stderr)
            payload = json.loads(manifest.read_text(encoding="utf-8"))
            self.assertEqual(COMMIT, payload["source"]["commit"])
            self.assertEqual(fixture.oci_digest, payload["image"]["digest"])
            self.assertEqual(set(RIDS), {subject["target"] for subject in payload["subjects"] if subject["kind"] == "native"})
            self.assertEqual({name for name, _ in fixture.subjects}, {subject["name"] for subject in payload["subjects"]})
            verified = self.verify(root, manifest)
            self.assertEqual(0, verified.returncode, verified.stderr)

    def test_rejects_missing_or_forbidden_native_targets(self):
        mutations = (
            lambda root: self.remove_subject(root, f"perf-sentinel-hub-{VERSION}-linux-arm64.tar.gz"),
            lambda root: self.rename_subject(root, "osx-arm64", "osx-x64"),
            lambda root: self.rename_subject(root, "win-x64", "win-arm64"),
        )
        for mutate in mutations:
            with self.subTest(mutate=mutate), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                ReleaseFixture(root)
                mutate(root)
                result = self.create(root, root.parent / f"{root.name}.json")
                self.assertNotEqual(0, result.returncode)
                self.assertIn("native target", result.stderr)

    def test_rejects_missing_symbols_mutable_image_and_mutable_chart(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            ReleaseFixture(root)
            self.remove_subject(root, f"perf-sentinel-hub-{VERSION}-linux-x64-symbols.tar.gz")
            result = self.create(root, root.parent / f"{root.name}.json")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("symbols", result.stderr)

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            ReleaseFixture(root)
            result = self.create(root, root.parent / f"{root.name}.json", image_digest="latest")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("immutable image digest", result.stderr)

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fixture = ReleaseFixture(root)
            (root / fixture.chart).write_bytes(fixture.chart_archive(mutable=True))
            self.refresh_subject_evidence(root, fixture.chart)
            result = self.create(root, root.parent / f"{root.name}.json")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("chart", result.stderr)

    def test_rejects_mismatched_hash_and_unlisted_file(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            ReleaseFixture(root)
            manifest = root.parent / f"{root.name}.json"
            self.assertEqual(0, self.create(root, manifest).returncode)
            subject = root / f"perf-sentinel-hub-{VERSION}-linux-x64.tar.gz"
            subject.write_bytes(b"tampered")
            result = self.verify(root, manifest)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("sha256 mismatch", result.stderr)

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            ReleaseFixture(root)
            manifest = root.parent / f"{root.name}.json"
            self.assertEqual(0, self.create(root, manifest).returncode)
            (root / "surprise.txt").write_text("extra", encoding="utf-8")
            result = self.verify(root, manifest)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("unlisted files", result.stderr)

    def test_rejects_attestation_or_signature_for_wrong_subject(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fixture = ReleaseFixture(root)
            name, digest = fixture.subjects[0]
            (root / f"{name}.sigstore.json").write_bytes(signature_bundle("f" * 64))
            result = self.create(root, root.parent / f"{root.name}.json")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("signature bundle digest", result.stderr)

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fixture = ReleaseFixture(root)
            name, digest = fixture.subjects[0]
            (root / f"{name}.sbom.sigstore.json").write_bytes(
                dsse_bundle([("wrong-name", digest)], "https://spdx.dev/Document/v2.3")
            )
            result = self.create(root, root.parent / f"{root.name}.json")
            self.assertNotEqual(0, result.returncode)
            self.assertIn("SBOM attestation subject", result.stderr)

    def test_chart_repository_rejects_tag_or_digest_but_accepts_registry_port(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fixture = ReleaseFixture(root)
            (root / fixture.chart).write_bytes(
                fixture.chart_archive(repository="localhost:5000/team/perf-sentinel-hub")
            )
            self.refresh_subject_evidence(root, fixture.chart)
            result = self.create(root, root.parent / f"{root.name}.json")
            self.assertEqual(0, result.returncode, result.stderr)

        for repository in (
            "ghcr.io/robintra/perf-sentinel-hub:latest",
            "ghcr.io/robintra/perf-sentinel-hub@sha256:" + "a" * 64,
        ):
            with self.subTest(repository=repository), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                fixture = ReleaseFixture(root)
                (root / fixture.chart).write_bytes(fixture.chart_archive(repository=repository))
                self.refresh_subject_evidence(root, fixture.chart)
                result = self.create(root, root.parent / f"{root.name}.json")
                self.assertNotEqual(0, result.returncode)
                self.assertIn("repository", result.stderr)

    def create(self, root, manifest, *, image_digest=None):
        if image_digest is None:
            image = root / f"perf-sentinel-hub-{VERSION}.oci.tar"
            checked = subprocess.run(
                [sys.executable, str(IMAGE_CHECKER), "--layout", str(image)],
                text=True,
                capture_output=True,
            )
            self.assertEqual(0, checked.returncode, checked.stderr)
            image_digest = checked.stdout.strip()
        return subprocess.run(
            [
                sys.executable,
                str(VERIFIER),
                "create",
                "--root",
                str(root),
                "--manifest",
                str(manifest),
                "--version",
                VERSION,
                "--source-commit",
                COMMIT,
                "--source-repository",
                "https://github.com/robintra/PerfSentinelHub",
                "--image-digest",
                image_digest,
            ],
            text=True,
            capture_output=True,
        )

    def verify(self, root, manifest):
        return subprocess.run(
            [sys.executable, str(VERIFIER), "verify", "--root", str(root), "--manifest", str(manifest)],
            text=True,
            capture_output=True,
        )

    @staticmethod
    def remove_subject(root, name):
        for suffix in ("", ".spdx.json", ".sigstore.json", ".sbom.sigstore.json"):
            (root / f"{name}{suffix}").unlink()

    @staticmethod
    def rename_subject(root, old, new):
        # Snapshot the directory first, renaming entries while iterating it can skip files.
        for path in sorted(root.iterdir()):
            if old in path.name:
                path.rename(root / path.name.replace(old, new))

    @staticmethod
    def refresh_subject_evidence(root, name):
        digest = hashlib.sha256((root / name).read_bytes()).hexdigest()
        (root / f"{name}.sigstore.json").write_bytes(signature_bundle(digest))
        (root / f"{name}.sbom.sigstore.json").write_bytes(dsse_bundle([(name, digest)], "https://spdx.dev/Document/v2.3"))
        provenance = root / "release.provenance.sigstore.json"
        payload = json.loads(base64.b64decode(json.loads(provenance.read_text())["dsseEnvelope"]["payload"]))
        for subject in payload["subject"]:
            if subject["name"] == name:
                subject["digest"]["sha256"] = digest
        bundle = json.loads(provenance.read_text())
        bundle["dsseEnvelope"]["payload"] = base64.b64encode(compact_json(payload)).decode()
        provenance.write_bytes(compact_json(bundle))


class ImageManifestTests(unittest.TestCase):
    def test_accepts_exact_linux_amd64_arm64_index_and_writes_digest(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fixture = ReleaseFixture.__new__(ReleaseFixture)
            layout = root / "image.oci.tar"
            layout.write_bytes(fixture.oci_archive())
            digest_file = root / "digest.txt"
            result = subprocess.run(
                [sys.executable, str(IMAGE_CHECKER), "--layout", str(layout), "--write-digest", str(digest_file)],
                text=True,
                capture_output=True,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertRegex(digest_file.read_text(encoding="ascii"), r"^sha256:[0-9a-f]{64}\n$")

    def test_rejects_extra_or_missing_platform_and_bad_blob_digest(self):
        cases = (("amd64",), ("amd64", "arm64", "386"))
        for architectures in cases:
            with self.subTest(architectures=architectures), tempfile.TemporaryDirectory() as directory:
                layout = Path(directory) / "layout"
                (layout / "blobs" / "sha256").mkdir(parents=True)
                manifests = []
                for architecture in architectures:
                    content = b"{}"
                    digest = hashlib.sha256(content).hexdigest()
                    (layout / "blobs" / "sha256" / digest).write_bytes(content)
                    manifests.append({"digest": f"sha256:{digest}", "size": len(content), "platform": {"os": "linux", "architecture": architecture}})
                (layout / "oci-layout").write_text('{"imageLayoutVersion":"1.0.0"}\n', encoding="ascii")
                (layout / "index.json").write_bytes(compact_json({"schemaVersion": 2, "manifests": manifests}))
                result = subprocess.run([sys.executable, str(IMAGE_CHECKER), "--layout", str(layout)], text=True, capture_output=True)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("platforms", result.stderr)

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fixture = ReleaseFixture.__new__(ReleaseFixture)
            layout = root / "image.oci.tar"
            content = bytearray(fixture.oci_archive())
            content[-10:] = b"tampering!"
            layout.write_bytes(content)
            result = subprocess.run([sys.executable, str(IMAGE_CHECKER), "--layout", str(layout)], text=True, capture_output=True)
            self.assertNotEqual(0, result.returncode)

    def test_rejects_a_mismatched_referenced_layer_blob(self):
        with tempfile.TemporaryDirectory() as directory:
            layout = Path(directory) / "layout"
            blobs = layout / "blobs" / "sha256"
            blobs.mkdir(parents=True)
            config = b"{}"
            config_digest = hashlib.sha256(config).hexdigest()
            (blobs / config_digest).write_bytes(config)
            manifests = []
            for architecture in ("amd64", "arm64"):
                claimed_layer = hashlib.sha256(f"expected-{architecture}".encode()).hexdigest()
                (blobs / claimed_layer).write_bytes(f"tampered-{architecture}".encode())
                manifest = compact_json(
                    {
                        "schemaVersion": 2,
                        "config": {"digest": f"sha256:{config_digest}", "size": len(config)},
                        "layers": [{"digest": f"sha256:{claimed_layer}", "size": len(f"tampered-{architecture}".encode())}],
                    }
                )
                manifest_digest = hashlib.sha256(manifest).hexdigest()
                (blobs / manifest_digest).write_bytes(manifest)
                manifests.append(
                    {
                        "digest": f"sha256:{manifest_digest}",
                        "size": len(manifest),
                        "platform": {"os": "linux", "architecture": architecture},
                    }
                )
            (layout / "oci-layout").write_text('{"imageLayoutVersion":"1.0.0"}\n', encoding="ascii")
            (layout / "index.json").write_bytes(compact_json({"schemaVersion": 2, "manifests": manifests}))
            result = subprocess.run([sys.executable, str(IMAGE_CHECKER), "--layout", str(layout)], text=True, capture_output=True)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("blob digest", result.stderr)

    def test_composes_final_index_from_exact_verified_manifest_digests(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fixture = ReleaseFixture.__new__(ReleaseFixture)
            sources = {}
            expected = {}
            for architecture in ("amd64", "arm64"):
                source = root / f"{architecture}.oci.tar"
                source.write_bytes(fixture.oci_archive((architecture,)))
                sources[architecture] = source
                with tarfile.open(source, "r:") as archive:
                    index = json.load(archive.extractfile("index.json"))
                expected[architecture] = index["manifests"][0]["digest"]

            output = root / "multi.oci.tar"
            digest_file = root / "multi.digest"
            result = subprocess.run(
                [
                    sys.executable,
                    str(IMAGE_CHECKER),
                    "--compose-output",
                    str(output),
                    "--source",
                    f"linux/amd64={sources['amd64']}",
                    "--source",
                    f"linux/arm64={sources['arm64']}",
                    "--source-date-epoch",
                    "1",
                    "--write-digest",
                    str(digest_file),
                ],
                text=True,
                capture_output=True,
            )
            self.assertEqual(0, result.returncode, result.stderr)
            checked = subprocess.run(
                [
                    sys.executable,
                    str(IMAGE_CHECKER),
                    "--layout",
                    str(output),
                    "--expected-manifest",
                    f"linux/amd64={expected['amd64']}",
                    "--expected-manifest",
                    f"linux/arm64={expected['arm64']}",
                ],
                text=True,
                capture_output=True,
            )
            self.assertEqual(0, checked.returncode, checked.stderr)
            mismatch = subprocess.run(
                [
                    sys.executable,
                    str(IMAGE_CHECKER),
                    "--layout",
                    str(output),
                    "--expected-manifest",
                    "linux/amd64=sha256:" + "f" * 64,
                    "--expected-manifest",
                    f"linux/arm64={expected['arm64']}",
                ],
                text=True,
                capture_output=True,
            )
            self.assertNotEqual(0, mismatch.returncode)
            self.assertIn("manifest digest", mismatch.stderr)


class ReleaseTagCheckerTests(unittest.TestCase):
    def setUp(self):
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary_directory.name)
        self.repository = self.root / "repository"
        self.environment = os.environ.copy()
        self.environment.update({"GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1"})
        self.run_command("git", "init", "-q", "-b", "main", str(self.repository), cwd=self.root)
        self.git("config", "user.name", "Release test")
        self.git("config", "user.email", "release@example.invalid")
        self.signing_key = self.root / "signing-key"
        self.other_key = self.root / "other-key"
        for key in (self.signing_key, self.other_key):
            self.run_command("ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", str(key), cwd=self.root)
        (self.repository / "config").mkdir()
        self.config = self.repository / "config/signing-identities.json"
        self.write_config()
        (self.repository / "file").write_text("one\n", encoding="ascii")
        self.git("add", "file")
        self.git("commit", "-q", "-m", "one")

    def tearDown(self):
        self.temporary_directory.cleanup()

    def test_rejects_unsigned_annotated_tag(self):
        self.git("tag", "-a", "v0.1.0", "-m", "unsigned")
        result = self.check("v0.1.0", self.git_output("rev-parse", "HEAD"))
        self.assertNotEqual(0, result.returncode)
        self.assertIn("signature", result.stderr)

    def test_accepts_signed_tag_and_rejects_a_different_expected_commit(self):
        self.sign_tag()
        tagged_commit = self.git_output("rev-parse", "HEAD")
        accepted = self.check("v0.1.0", tagged_commit)
        self.assertEqual(0, accepted.returncode, accepted.stderr)
        self.assertNotEqual(0, self.git_result("config", "--local", "--get", "gpg.format").returncode)
        self.assertNotEqual(0, self.git_result("config", "--local", "--get", "gpg.ssh.allowedSignersFile").returncode)

        (self.repository / "file").write_text("two\n", encoding="ascii")
        self.git("add", "file")
        self.git("commit", "-q", "-m", "two")
        rejected = self.check("v0.1.0", self.git_output("rev-parse", "HEAD"))
        self.assertNotEqual(0, rejected.returncode)
        self.assertIn("target", rejected.stderr)

    def test_rejects_a_signature_from_an_unapproved_key_or_principal(self):
        self.sign_tag()
        tagged_commit = self.git_output("rev-parse", "HEAD")

        self.write_config(key=self.other_key)
        wrong_key = self.check("v0.1.0", tagged_commit)
        self.assertNotEqual(0, wrong_key.returncode)
        self.assertIn("approved SSH identity", wrong_key.stderr)

        self.write_config(principal="other@example.invalid")
        wrong_principal = self.check("v0.1.0", tagged_commit)
        self.assertNotEqual(0, wrong_principal.returncode)
        self.assertIn("tag principal", wrong_principal.stderr)

    def test_rejects_malformed_or_open_signing_identity_configuration(self):
        self.sign_tag()
        tagged_commit = self.git_output("rev-parse", "HEAD")
        valid = json.loads(self.config.read_text(encoding="utf-8"))
        invalid = []
        unknown = json.loads(json.dumps(valid))
        unknown["unexpected"] = True
        invalid.append(json.dumps(unknown))
        boolean_version = json.loads(json.dumps(valid))
        boolean_version["schema_version"] = True
        invalid.append(json.dumps(boolean_version))
        wrong_type = json.loads(json.dumps(valid))
        wrong_type["release_tag"]["key_type"] = "ssh-rsa"
        invalid.append(json.dumps(wrong_type))
        wrong_fingerprint = json.loads(json.dumps(valid))
        wrong_fingerprint["release_tag"]["fingerprint"] = "SHA256:" + "A" * 43
        invalid.append(json.dumps(wrong_fingerprint))
        wrong_workflow = json.loads(json.dumps(valid))
        wrong_workflow["github"]["workflow_identity_template"] = "https://example.invalid/{version}"
        invalid.append(json.dumps(wrong_workflow))
        invalid.append(self.config.read_text(encoding="utf-8").replace('"schema_version": 1', '"schema_version": 1, "schema_version": 1'))

        for content in invalid:
            with self.subTest(content=content):
                self.config.write_text(content, encoding="utf-8")
                result = self.check("v0.1.0", tagged_commit)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("signing identity configuration", result.stderr)

    def sign_tag(self):
        self.git(
            "-c",
            "gpg.format=ssh",
            "-c",
            f"user.signingkey={self.signing_key}",
            "tag",
            "-s",
            "v0.1.0",
            "-m",
            "signed",
        )

    def write_config(self, *, key=None, principal="release@example.invalid"):
        key = self.signing_key if key is None else key
        key_type, encoded_key, *_ = key.with_suffix(".pub").read_text(encoding="ascii").split()
        fingerprint = self.run_command("ssh-keygen", "-lf", str(key.with_suffix(".pub")), "-E", "sha256", cwd=self.root).stdout.split()[1]
        self.config.write_text(
            json.dumps(
                {
                    "schema_version": 1,
                    "release_tag": {
                        "principal": principal,
                        "key_type": key_type,
                        "public_key": f"{key_type} {encoded_key}",
                        "fingerprint": fingerprint,
                    },
                    "github": {
                        "oidc_issuer": "https://token.actions.githubusercontent.com",
                        "repository": "robintra/PerfSentinelHub",
                        "repository_url": "https://github.com/robintra/PerfSentinelHub",
                        "release_workflow": ".github/workflows/release.yml",
                        "tag_ref_template": "refs/tags/v{version}",
                        "workflow_identity_template": "https://github.com/robintra/PerfSentinelHub/.github/workflows/release.yml@refs/tags/v{version}",
                    },
                },
                indent=2,
            )
            + "\n",
            encoding="utf-8",
        )

    def check(self, tag, commit):
        return self.run_command(sys.executable, str(TAG_CHECKER), tag, commit, cwd=self.repository, check=False)

    def git(self, *arguments):
        return self.run_command("git", *arguments, cwd=self.repository)

    def git_result(self, *arguments):
        return self.run_command("git", *arguments, cwd=self.repository, check=False)

    def git_output(self, *arguments):
        return self.git(*arguments).stdout.strip()

    def run_command(self, *arguments, cwd, check=True):
        result = subprocess.run(arguments, cwd=cwd, env=self.environment, text=True, capture_output=True)
        if check:
            self.assertEqual(0, result.returncode, result.stderr)
        return result


class ReleaseWorkflowTests(unittest.TestCase):
    def test_public_input_accepts_only_the_canonical_tag_or_release_url_without_a_secret(self):
        environment = os.environ.copy()
        for name in ("GH_TOKEN", "GITHUB_TOKEN", "COSIGN_PASSWORD"):
            environment.pop(name, None)
        expected = {
            "repository": "robintra/PerfSentinelHub",
            "tag": "v0.1.0",
            "url": "https://github.com/robintra/PerfSentinelHub/releases/tag/v0.1.0",
        }
        for value in (expected["tag"], expected["url"]):
            with self.subTest(value=value):
                result = subprocess.run(
                    [sys.executable, str(VERIFIER), "public-input", value],
                    env=environment,
                    text=True,
                    capture_output=True,
                )
                self.assertEqual(0, result.returncode, result.stderr)
                self.assertEqual(expected, json.loads(result.stdout))

        for value in (
            "v0.1.0-rc.1",
            "https://github.com/robintra/PerfSentinelHub/releases/tag/v0.01.0",
            "https://github.com/other/PerfSentinelHub/releases/tag/v0.1.0",
            "https://github.com/robintra/PerfSentinelHub/releases/tag/v0.1.0?download=1",
        ):
            with self.subTest(value=value):
                result = subprocess.run(
                    [sys.executable, str(VERIFIER), "public-input", value],
                    env=environment,
                    text=True,
                    capture_output=True,
                )
                self.assertNotEqual(0, result.returncode)

    def test_protected_publish_is_a_pure_promotion_of_one_verified_artifact(self):
        content = (REPOSITORY / ".github/workflows/release.yml").read_text(encoding="utf-8")
        self.assertIn("  publish:", content)
        publish = content.split("\n  publish:\n", 1)[1]
        self.assertIn("needs: manifest", publish)
        self.assertIn("environment: hub-release", publish)
        self.assertIn("artifact-ids: ${{ needs.manifest.outputs.artifact_id }}", publish)
        self.assertIn("EXPECTED_ARTIFACT_DIGEST: ${{ needs.manifest.outputs.artifact_digest }}", publish)
        self.assertIn("github-token: ${{ github.token }}", publish)
        self.assertIn("python3 publish-bundle/scripts/verify-release.py verify-published", publish)
        for forbidden in (
            "actions/checkout@",
            "dotnet ",
            "docker build",
            "buildx",
            "helm package",
            "package-native.py",
            "oras resolve",
        ):
            self.assertNotIn(forbidden, publish)

    def test_protected_publish_refetches_and_rechecks_the_signed_tag_before_registry_access(self):
        content = (REPOSITORY / ".github/workflows/release.yml").read_text(encoding="utf-8")
        manifest = content.split("\n  manifest:\n", 1)[1].split("\n  publish:\n", 1)[0]
        publish = content.split("\n  publish:\n", 1)[1]

        self.assertIn("scripts/check-release-tag.py", manifest)
        self.assertIn("config/signing-identities.json", manifest)
        self.assertIn("EXPECTED_SOURCE_COMMIT: ${{ github.sha }}", publish)
        self.assertIn('refs/tags/$tag:refs/tags/$tag', publish)
        self.assertIn('check-release-tag.py "$tag" "$source_commit"', publish)
        self.assertLess(publish.index("check-release-tag.py"), publish.index("docker/login-action@"))

    def test_public_verification_runs_only_the_workflow_commit_verifier_before_attested_artifacts(self):
        content = (REPOSITORY / ".github/workflows/release-verification.yml").read_text(encoding="utf-8")
        public_release = content.split("\n  public-release:\n", 1)[1].split("\n  cryptography:\n", 1)[0]

        self.assertIn("ref: ${{ github.workflow_sha }}", public_release)
        self.assertIn("persist-credentials: false", public_release)
        self.assertNotIn("contents/scripts/verify-release.py?ref=$source_commit", public_release)
        authentication = public_release.split("name: Authenticate downloaded public assets", 1)[1]
        self.assertNotIn("GH_TOKEN", authentication)
        for job in ("native", "image", "chart"):
            match = re.search(rf"(?ms)^  {job}:\n(.*?)(?=^  [a-z-]+:|\Z)", content)
            self.assertIsNotNone(match)
            body = match.group(1)
            self.assertIn("cryptography", body)

    def test_latest_stable_selects_one_global_canonical_semver_from_all_pages(self):
        pages = [
            [
                {"tag_name": "v0.9.9", "draft": False, "prerelease": False},
                {"tag_name": "v0.99.0", "draft": True, "prerelease": False},
                {"tag_name": "v0.98.0", "draft": False, "prerelease": True},
            ],
            [
                {"tag_name": "v0.10.0", "draft": False, "prerelease": False},
                {"tag_name": "v0.010.1", "draft": False, "prerelease": False},
            ],
        ]
        selected = subprocess.run(
            [sys.executable, str(VERIFIER), "latest-stable"],
            input=json.dumps(pages),
            text=True,
            capture_output=True,
        )
        self.assertEqual(0, selected.returncode, selected.stderr)
        self.assertEqual("v0.10.0", selected.stdout.strip())

        pages[1].append({"tag_name": "v0.10.0", "draft": False, "prerelease": False})
        ambiguous = subprocess.run(
            [sys.executable, str(VERIFIER), "latest-stable"],
            input=json.dumps(pages),
            text=True,
            capture_output=True,
        )
        self.assertNotEqual(0, ambiguous.returncode)
        self.assertIn("ambiguous", ambiguous.stderr)

    def test_daily_latest_stable_verification_keeps_one_sanitized_issue(self):
        content = (REPOSITORY / ".github/workflows/release-verification.yml").read_text(encoding="utf-8")
        self.assertIn("schedule:", content)
        self.assertIn("cron: '17 5 * * *'", content)
        self.assertIn("latest-stable", content)
        self.assertIn("release-verification-alert", content)
        self.assertIn("Verification failed. Inspect the workflow run", content)
        self.assertIn("Verification succeeded; closing the alert", content)
        for forbidden in ("${{ toJSON(", "SARIF", "artifact body", "secrets."):
            self.assertNotIn(forbidden, content)

    def test_daily_verification_stands_down_before_the_first_release_only(self):
        content = (REPOSITORY / ".github/workflows/release-verification.yml").read_text(encoding="utf-8")
        inventory = content.split("\n  inventory:\n", 1)[1].split("\n  public-release:\n", 1)[0]
        public_release = content.split("\n  public-release:\n", 1)[1].split("\n  cryptography:\n", 1)[0]
        report = content.split("\n  report:\n", 1)[1]

        self.assertIn("releases?per_page=1", inventory)
        self.assertIn('published=true', inventory)
        self.assertIn('published=false', inventory)
        self.assertIn("needs: inventory", public_release)
        self.assertIn("if: ${{ needs.inventory.outputs.published == 'true' }}", public_release)
        self.assertIn("needs: [inventory, public-release", report)
        self.assertIn("needs.inventory.result != 'success'", report)
        self.assertIn("needs.inventory.outputs.published == 'true' && (", report)

    def test_daily_alert_reuses_one_exact_non_pull_request_issue_across_all_states(self):
        content = (REPOSITORY / ".github/workflows/release-verification.yml").read_text(encoding="utf-8")
        report = content.split("\n  report:\n", 1)[1]

        self.assertIn("github.paginate(github.rest.issues.listForRepo", report)
        self.assertIn("state: 'all'", report)
        self.assertIn("!issue.pull_request", report)
        self.assertIn("issue.title === title", report)
        self.assertIn("state: 'open'", report)
        self.assertIn("for (const duplicate of issues.slice(1))", report)

    def test_repository_chart_renders_only_the_immutable_image_digest(self):
        digest = "sha256:" + "b" * 64
        command = [
            "helm",
            "template",
            "test",
            str(REPOSITORY / "deploy/helm/perf-sentinel-hub"),
            "--set-string",
            "image.repository=localhost:5000/team/perf-sentinel-hub",
            "--set",
            f"image.digest={digest}",
            "--set",
            "sources[0].id=test",
            "--set",
            "sources[0].name=test",
            "--set",
            "sources[0].environment=test",
            "--set",
            "sources[0].baseUrl=http://perf-sentinel:4318",
        ]
        result = subprocess.run(command, text=True, capture_output=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn(f'image: "localhost:5000/team/perf-sentinel-hub@{digest}"', result.stdout)

        for repository in (
            "ghcr.io/robintra/perf-sentinel-hub:latest",
            "ghcr.io/robintra/perf-sentinel-hub@sha256:" + "a" * 64,
        ):
            invalid = command.copy()
            invalid[invalid.index("image.repository=localhost:5000/team/perf-sentinel-hub")] = (
                f"image.repository={repository}"
            )
            rejected = subprocess.run(invalid, text=True, capture_output=True)
            self.assertNotEqual(0, rejected.returncode, repository)

    def test_workflow_has_native_duplicate_build_and_build_only_supply_chain(self):
        workflow = REPOSITORY / ".github/workflows/release.yml"
        self.assertTrue(workflow.is_file())
        content = workflow.read_text(encoding="utf-8")
        for job in ("native-a:", "native-b:", "native-verify:", "oci:", "helm:", "sign:", "attest-provenance:", "attest-sbom:", "manifest:"):
            self.assertIn(f"  {job}", content)
        for runner in ("ubuntu-24.04", "ubuntu-24.04-arm", "macos-15", "windows-2025"):
            self.assertIn(f"runner: {runner}", content)
        for rid in RIDS:
            self.assertIn(f"rid: {rid}", content)
        self.assertIn("actions/attest@1e69f48acb82d1966a394da916b4c1698aa569d6", content)
        self.assertIn("cosign verify-blob", content)
        self.assertIn("--certificate-oidc-issuer https://token.actions.githubusercontent.com", content)
        self.assertIn('python3 scripts/check-release-tag.py "$REQUESTED_TAG" "$GITHUB_SHA"', content)
        self.assertIn("--compose-output", content)
        self.assertNotIn("--platform linux/amd64,linux/arm64", content)
        self.assertNotIn("slsa-framework/slsa-github-generator/", content)
        verification_jobs = content.split("\n  publish:\n", 1)[0]
        for forbidden in ("docker push", "--push", "gh release", "packages: write", "contents: write"):
            self.assertNotIn(forbidden, verification_jobs)


if __name__ == "__main__":
    unittest.main()
