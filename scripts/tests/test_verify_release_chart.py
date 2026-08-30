import importlib.util
import io
import tarfile
import tempfile
import unittest
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "verify_release", REPOSITORY / "scripts" / "verify-release.py"
)
verify = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(verify)

VERSION = "0.1.0"
DIGEST = "sha256:" + "a" * 64
PREFIX = "perf-sentinel-hub/"


def chart_archive(path: Path, chart_yaml: str) -> None:
    members = {
        PREFIX + "Chart.yaml": chart_yaml,
        PREFIX + "values.yaml": (
            "image:\n"
            "  repository: ghcr.io/robintra/perf-sentinel-hub\n"
            f"  digest: {DIGEST}\n"
        ),
        PREFIX + "templates/_helpers.tpl": verify.IMAGE_HELPER,
        PREFIX + "templates/deployment.yaml": (
            "spec:\n"
            "  containers:\n"
            '    - image: {{ include "perf-sentinel-hub.image" . | quote }}\n'
        ),
    }
    with tarfile.open(path, "w:gz") as archive:
        for name, text in sorted(members.items()):
            content = text.encode("utf-8")
            info = tarfile.TarInfo(name)
            info.size = len(content)
            info.mode = 0o644
            archive.addfile(info, io.BytesIO(content))


class ChartVersionTests(unittest.TestCase):
    def validate(self, chart_yaml: str):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "chart.tgz"
            chart_archive(path, chart_yaml)
            verify.validate_chart(path, VERSION, DIGEST)

    def test_accepts_the_form_helm_package_writes(self):
        # helm rewrites Chart.yaml on package: it sorts the keys and drops the
        # quotes around appVersion, so the quoted form never survives packaging.
        self.validate(
            "apiVersion: v2\n"
            f"appVersion: {VERSION}\n"
            "description: Hub\n"
            "name: perf-sentinel-hub\n"
            "type: application\n"
            f"version: {VERSION}\n"
        )

    def test_accepts_the_quoted_form_the_source_carries(self):
        self.validate(
            "apiVersion: v2\n"
            "name: perf-sentinel-hub\n"
            f"version: {VERSION}\n"
            f'appVersion: "{VERSION}"\n'
        )

    def test_rejects_a_version_that_is_not_the_release(self):
        for chart_yaml in (
            f"name: perf-sentinel-hub\nversion: 0.2.0\nappVersion: {VERSION}\n",
            f"name: perf-sentinel-hub\nversion: {VERSION}\nappVersion: 0.2.0\n",
        ):
            with self.assertRaisesRegex(ValueError, "chart version differs from release"):
                self.validate(chart_yaml)

    def test_rejects_a_version_that_is_only_a_prefix(self):
        # Anchored, so 0.1.0 must not be satisfied by 0.1.01 on either key.
        with self.assertRaisesRegex(ValueError, "chart version differs from release"):
            self.validate(
                f"name: perf-sentinel-hub\nversion: {VERSION}1\nappVersion: {VERSION}\n"
            )

    def test_the_committed_chart_carries_the_pinned_image_helper(self):
        helpers = (
            REPOSITORY / "deploy/helm/perf-sentinel-hub/templates/_helpers.tpl"
        ).read_text(encoding="utf-8")

        # verify-release.py only ever sees the packaged chart, so without this
        # the source and the pin drift until a tag is already cut.
        self.assertIn(verify.IMAGE_HELPER, helpers)


if __name__ == "__main__":
    unittest.main()
