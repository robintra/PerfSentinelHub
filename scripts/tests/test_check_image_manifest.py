import hashlib
import importlib.util
import io
import json
import tarfile
import tempfile
import unittest
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "check_image_manifest", REPOSITORY / "scripts" / "check-image-manifest.py"
)
composer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(composer)

EPOCH = 1786406400


def canonical(document) -> bytes:
    return json.dumps(document, sort_keys=True, separators=(",", ":")).encode("ascii")


def descriptor(media_type: str, content: bytes, **extra) -> dict:
    return {
        "mediaType": media_type,
        "digest": "sha256:" + hashlib.sha256(content).hexdigest(),
        "size": len(content),
        **extra,
    }


def write_layout(path: Path, entries: dict[str, bytes]) -> None:
    with tarfile.open(path, "w", format=tarfile.USTAR_FORMAT) as archive:
        for name, content in sorted(entries.items()):
            info = tarfile.TarInfo(name)
            info.size = len(content)
            info.mode = 0o644
            info.uid = info.gid = 0
            info.mtime = EPOCH
            archive.addfile(info, io.BytesIO(content))


def single_platform_layout(path: Path, architecture: str) -> None:
    """The smallest layout the validator accepts: one manifest, one config, one layer."""
    config = canonical({"architecture": architecture, "os": "linux"})
    layer = f"layer-{architecture}".encode("ascii")
    manifest = canonical(
        {
            "schemaVersion": 2,
            "mediaType": "application/vnd.oci.image.manifest.v1+json",
            "config": descriptor("application/vnd.oci.image.config.v1+json", config),
            "layers": [descriptor("application/vnd.oci.image.layer.v1.tar+gzip", layer)],
        }
    )
    manifest_descriptor = descriptor(
        "application/vnd.oci.image.manifest.v1+json",
        manifest,
        platform={"os": "linux", "architecture": architecture},
    )
    index = canonical(
        {
            "schemaVersion": 2,
            "mediaType": "application/vnd.oci.image.index.v1+json",
            "manifests": [manifest_descriptor],
        }
    )
    write_layout(
        path,
        {
            "oci-layout": b'{"imageLayoutVersion":"1.0.0"}',
            "index.json": index,
            **{
                f"blobs/sha256/{hashlib.sha256(blob).hexdigest()}": blob
                for blob in (config, layer, manifest)
            },
        },
    )


class ComposedLayoutTests(unittest.TestCase):
    def compose(self, directory: Path) -> tuple[str, dict[str, bytes]]:
        sources = {}
        for architecture in ("amd64", "arm64"):
            source = directory / f"{architecture}.oci.tar"
            single_platform_layout(source, architecture)
            sources[("linux", architecture)] = source
        output = directory / "composed.oci.tar"
        digest = composer.compose_layout(output, sources, EPOCH)
        with tarfile.open(output) as archive:
            entries = {m.name: archive.extractfile(m).read() for m in archive.getmembers()}
        return digest, entries

    def test_the_index_is_a_blob_that_index_json_points_at(self):
        with tempfile.TemporaryDirectory() as directory:
            digest, entries = self.compose(Path(directory))

            # oras and every other consumer resolve a digest against a
            # referenceable descriptor. An index that exists only as the entry
            # file is unreachable by digest, which is what blocked promotion.
            self.assertIn(f"blobs/sha256/{digest.removeprefix('sha256:')}", entries)

            wrapper = json.loads(entries["index.json"])
            self.assertEqual(1, len(wrapper["manifests"]))
            self.assertEqual(digest, wrapper["manifests"][0]["digest"])
            self.assertEqual(
                "application/vnd.oci.image.index.v1+json",
                wrapper["manifests"][0]["mediaType"],
            )
            # The wrapper entry carries no platform: that is what tells the
            # reader to descend rather than treat it as an image.
            self.assertNotIn("platform", wrapper["manifests"][0])

    def test_the_reported_digest_is_the_index_content_not_the_entry_file(self):
        with tempfile.TemporaryDirectory() as directory:
            digest, entries = self.compose(Path(directory))
            inner = entries[f"blobs/sha256/{digest.removeprefix('sha256:')}"]
            self.assertEqual("sha256:" + hashlib.sha256(inner).hexdigest(), digest)
            # The entry file is a wrapper now, so its own hash is not the image.
            self.assertNotEqual(
                "sha256:" + hashlib.sha256(entries["index.json"]).hexdigest(), digest
            )
            index = json.loads(inner)
            self.assertEqual(
                [("linux", "amd64"), ("linux", "arm64")],
                sorted(
                    (m["platform"]["os"], m["platform"]["architecture"])
                    for m in index["manifests"]
                ),
            )

    def test_the_composed_layout_validates_and_reports_the_same_digest(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory)
            digest, _ = self.compose(path)
            self.assertEqual(
                digest, composer.validated_digest(path / "composed.oci.tar", composer.ALL_PLATFORMS)
            )

    def test_composition_is_byte_identical_for_one_input(self):
        with tempfile.TemporaryDirectory() as one, tempfile.TemporaryDirectory() as two:
            first, _ = self.compose(Path(one))
            second, _ = self.compose(Path(two))
            self.assertEqual(first, second)
            self.assertEqual(
                (Path(one) / "composed.oci.tar").read_bytes(),
                (Path(two) / "composed.oci.tar").read_bytes(),
            )


if __name__ == "__main__":
    unittest.main()
