import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parents[2]
CHECKER = REPOSITORY / "scripts" / "check-version.py"


class VersionContractTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(CHECKER.is_file(), "scripts/check-version.py is missing")
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary_directory.name)
        self.write_contract()

    def tearDown(self):
        if hasattr(self, "temporary_directory"):
            self.temporary_directory.cleanup()

    def test_accepts_the_stable_v0_version_contract(self):
        result = self.run_checker("v0.1.0")

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("version contract matches v0.1.0", result.stdout)

    def test_rejects_prerelease_and_non_v0_tags(self):
        for tag in ("v0.1.0-beta.1", "0.1.0", "v1.0.0", "v0.1", "v0.1.0+build"):
            with self.subTest(tag=tag):
                result = self.run_checker(tag)
                self.assertNotEqual(0, result.returncode)
                self.assertIn("stable tag", result.stderr)

    def test_rejects_each_drifting_declaration(self):
        cases = {
            "project version": ("PerfSentinelHub/PerfSentinelHub.csproj", "<Version>0.1.0</Version>", "<Version>0.1.1</Version>"),
            "chart version": ("deploy/helm/perf-sentinel-hub/Chart.yaml", "version: 0.1.0", "version: 0.1.1"),
            "chart appVersion": ("deploy/helm/perf-sentinel-hub/Chart.yaml", 'appVersion: "0.1.0"', 'appVersion: "0.1.1"'),
            "changelog heading": ("CHANGELOG.md", "## [0.1.0] - 2026-08-12", "## [0.1.1] - 2026-08-12"),
            "image version label": ("Dockerfile", 'LABEL org.opencontainers.image.version="0.1.0"', 'LABEL org.opencontainers.image.version="0.1.1"'),
        }
        for expected_error, (relative, current, replacement) in cases.items():
            with self.subTest(declaration=expected_error):
                fixture = Path(tempfile.mkdtemp(dir=self.root))
                self.write_contract(fixture)
                path = fixture / relative
                path.write_text(path.read_text(encoding="utf-8").replace(current, replacement), encoding="utf-8")

                result = self.run_checker("v0.1.0", fixture)

                self.assertNotEqual(0, result.returncode)
                self.assertIn(expected_error, result.stderr)
                shutil.rmtree(fixture)

    def test_rejects_missing_or_ambiguous_declarations(self):
        cases = {
            "project version": ("PerfSentinelHub/PerfSentinelHub.csproj", "<Version>0.1.0</Version>", ""),
            "chart version": ("deploy/helm/perf-sentinel-hub/Chart.yaml", "version: 0.1.0", ""),
            "chart appVersion": ("deploy/helm/perf-sentinel-hub/Chart.yaml", 'appVersion: "0.1.0"', ""),
            "changelog heading": ("CHANGELOG.md", "## [0.1.0] - 2026-08-12", ""),
            "image version label": ("Dockerfile", 'LABEL org.opencontainers.image.version="0.1.0"', ""),
        }
        for expected_error, (relative, current, replacement) in cases.items():
            with self.subTest(declaration=expected_error):
                fixture = Path(tempfile.mkdtemp(dir=self.root))
                self.write_contract(fixture)
                path = fixture / relative
                path.write_text(path.read_text(encoding="utf-8").replace(current, replacement), encoding="utf-8")

                result = self.run_checker("v0.1.0", fixture)

                self.assertNotEqual(0, result.returncode)
                self.assertIn(expected_error, result.stderr)
                shutil.rmtree(fixture)

    def run_checker(self, tag, root=None):
        return subprocess.run(
            [sys.executable, str(CHECKER), tag],
            cwd=root or self.root,
            text=True,
            capture_output=True,
            check=False,
        )

    def write_contract(self, root=None):
        root = root or self.root
        (root / "PerfSentinelHub").mkdir(parents=True)
        (root / "deploy/helm/perf-sentinel-hub").mkdir(parents=True)
        (root / "PerfSentinelHub/PerfSentinelHub.csproj").write_text(
            "<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>\n",
            encoding="utf-8",
        )
        (root / "deploy/helm/perf-sentinel-hub/Chart.yaml").write_text(
            'apiVersion: v2\nname: perf-sentinel-hub\nversion: 0.1.0\nappVersion: "0.1.0"\n',
            encoding="utf-8",
        )
        (root / "CHANGELOG.md").write_text(
            "# Changelog\n\n## [0.1.0] - 2026-08-12\n\n- Initial release.\n",
            encoding="utf-8",
        )
        (root / "Dockerfile").write_text(
            'FROM scratch\nLABEL org.opencontainers.image.version="0.1.0"\n',
            encoding="utf-8",
        )


if __name__ == "__main__":
    unittest.main()
