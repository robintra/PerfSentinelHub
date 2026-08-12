import base64
import hashlib
import io
import json
import subprocess
import sys
import tarfile
import tempfile
import unittest
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parents[2]
VERIFIER = REPOSITORY / "scripts" / "verify-release.py"
IMAGE_CHECKER = REPOSITORY / "scripts" / "check-image-manifest.py"
VERSION = "0.1.0"
COMMIT = "d61dec54c28bac1a092f97089bb2a426bbef39a6"
RIDS = ("linux-x64", "linux-arm64", "osx-arm64", "win-x64")


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

    def oci_archive(self):
        manifests = []
        blobs = {}
        config = b"{}"
        config_digest = hashlib.sha256(config).hexdigest()
        blobs[config_digest] = config
        for architecture in ("amd64", "arm64"):
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

    def chart_archive(self, *, mutable=False):
        digest = "tag: mutable" if mutable else f"digest: {self.oci_digest}"
        image = 'image: "{{ .Values.image.repository }}:{{ .Values.image.tag }}"' if mutable else 'image: "{{ .Values.image.repository }}@{{ .Values.image.digest }}"'
        output = io.BytesIO()
        with tarfile.open(fileobj=output, mode="w:gz") as archive:
            self.add_tar_bytes(archive, "perf-sentinel-hub/Chart.yaml", b"apiVersion: v2\nname: perf-sentinel-hub\nversion: 0.1.0\nappVersion: \"0.1.0\"\n")
            self.add_tar_bytes(archive, "perf-sentinel-hub/values.yaml", f"image:\n  repository: ghcr.io/robintra/perf-sentinel-hub\n  {digest}\n".encode())
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
        for path in list(root.iterdir()):
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


class ReleaseWorkflowTests(unittest.TestCase):
    def test_repository_chart_renders_only_the_immutable_image_digest(self):
        digest = "sha256:" + "b" * 64
        result = subprocess.run(
            [
                "helm",
                "template",
                "test",
                str(REPOSITORY / "deploy/helm/perf-sentinel-hub"),
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
            ],
            text=True,
            capture_output=True,
        )
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn(f"image: \"perf-sentinel-hub@{digest}\"", result.stdout)
        self.assertNotIn("perf-sentinel-hub:local", result.stdout)

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
        self.assertNotIn("slsa-framework/slsa-github-generator/", content)
        for forbidden in ("docker push", "--push", "gh release", "packages: write", "contents: write"):
            self.assertNotIn(forbidden, content)


if __name__ == "__main__":
    unittest.main()
