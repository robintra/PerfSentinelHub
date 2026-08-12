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

    def test_rejects_conditional_nested_and_alternative_project_versions(self):
        cases = {
            "version condition": '<Project><PropertyGroup><Version Condition="\'$(Release)\' == \'true\'">0.1.0</Version></PropertyGroup></Project>\n',
            "property group condition": '<Project><PropertyGroup Condition="\'$(Release)\' == \'true\'"><Version>0.1.0</Version></PropertyGroup></Project>\n',
            "alternative prefix": "<Project><PropertyGroup><Version>0.1.0</Version><VersionPrefix>0.1.1</VersionPrefix></PropertyGroup></Project>\n",
            "nested version": "<Project><Choose><When Condition=\"'$(Release)' == 'true'\"><PropertyGroup><Version>0.1.0</Version></PropertyGroup></When></Choose></Project>\n",
        }
        for name, project in cases.items():
            with self.subTest(name=name), tempfile.TemporaryDirectory(dir=self.root) as directory:
                fixture = Path(directory)
                self.write_contract(fixture)
                (fixture / "PerfSentinelHub/PerfSentinelHub.csproj").write_text(project, encoding="utf-8")

                result = self.run_checker("v0.1.0", fixture)

                self.assertNotEqual(0, result.returncode)
                self.assertIn("project version", result.stderr)

    def test_rejects_explicit_and_implicit_project_version_overrides(self):
        with tempfile.TemporaryDirectory(dir=self.root) as directory:
            fixture = Path(directory)
            self.write_contract(fixture)
            (fixture / "version.props").write_text(
                "<Project><PropertyGroup><Version>0.1.1</Version></PropertyGroup></Project>\n",
                encoding="utf-8",
            )
            (fixture / "PerfSentinelHub/PerfSentinelHub.csproj").write_text(
                '<Project><Import Project="../version.props"/><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>\n',
                encoding="utf-8",
            )

            explicit = self.run_checker("v0.1.0", fixture)

            self.assertNotEqual(0, explicit.returncode)
            self.assertIn("project version", explicit.stderr)

        with tempfile.TemporaryDirectory(dir=self.root) as directory:
            fixture = Path(directory)
            self.write_contract(fixture)
            (fixture / "Directory.Build.props").write_text(
                "<Project><PropertyGroup><Version>0.1.1</Version></PropertyGroup></Project>\n",
                encoding="utf-8",
            )

            implicit = self.run_checker("v0.1.0", fixture)

            self.assertNotEqual(0, implicit.returncode)
            self.assertIn("project version", implicit.stderr)

    def test_accepts_minimal_valid_chart_scalar_forms(self):
        variants = (
            ("version : '0.1.0'", "appVersion: 0.1.0"),
            ('version: "0.1.0" # release', "appVersion : '0.1.0'"),
        )
        for version_line, app_version_line in variants:
            with self.subTest(version=version_line), tempfile.TemporaryDirectory(dir=self.root) as directory:
                fixture = Path(directory)
                self.write_contract(fixture)
                chart = fixture / "deploy/helm/perf-sentinel-hub/Chart.yaml"
                text = chart.read_text(encoding="utf-8")
                text = text.replace("version: 0.1.0", version_line)
                text = text.replace('appVersion: "0.1.0"', app_version_line)
                chart.write_text(text, encoding="utf-8")

                result = self.run_checker("v0.1.0", fixture)

                self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_duplicate_or_noncanonical_chart_structures(self):
        additions = (
            "version : '0.1.1'\n",
            "appVersion : 0.1.1\n",
            "{version: 0.1.1}\n",
            "  version: 0.1.1\n",
        )
        for addition in additions:
            with self.subTest(addition=addition.strip()), tempfile.TemporaryDirectory(dir=self.root) as directory:
                fixture = Path(directory)
                self.write_contract(fixture)
                chart = fixture / "deploy/helm/perf-sentinel-hub/Chart.yaml"
                chart.write_text(chart.read_text(encoding="utf-8") + addition, encoding="utf-8")

                result = self.run_checker("v0.1.0", fixture)

                self.assertNotEqual(0, result.returncode)
                self.assertIn("chart", result.stderr)

    def test_accepts_minimal_valid_docker_label_forms(self):
        variants = (
            "LABEL org.opencontainers.image.version=0.1.0",
            "label org.opencontainers.image.title=hub \\\n      org.opencontainers.image.version='0.1.0'",
        )
        for label in variants:
            with self.subTest(label=label), tempfile.TemporaryDirectory(dir=self.root) as directory:
                fixture = Path(directory)
                self.write_contract(fixture)
                dockerfile = fixture / "Dockerfile"
                dockerfile.write_text(
                    dockerfile.read_text(encoding="utf-8").replace(
                        'LABEL org.opencontainers.image.version="0.1.0"', label
                    ),
                    encoding="utf-8",
                )

                result = self.run_checker("v0.1.0", fixture)

                self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_every_duplicate_or_noncanonical_docker_version_label(self):
        additions = (
            "LABEL org.opencontainers.image.version=0.1.1\n",
            "label org.opencontainers.image.version='0.1.1'\n",
            "LABEL org.opencontainers.image.title=hub \\\n      org.opencontainers.image.version=0.1.1\n",
            'LABEL ["org.opencontainers.image.version", "0.1.1"]\n',
            "LABEL org.opencontainers.image.version=0.1.0 org.opencontainers.image.version=0.1.1\n",
            "# escape=`\nLABEL org.opencontainers.image.title=hub `\n      org.opencontainers.image.version=0.1.1\n",
        )
        for addition in additions:
            with self.subTest(addition=addition.strip()), tempfile.TemporaryDirectory(dir=self.root) as directory:
                fixture = Path(directory)
                self.write_contract(fixture)
                dockerfile = fixture / "Dockerfile"
                dockerfile.write_text(dockerfile.read_text(encoding="utf-8") + addition, encoding="utf-8")

                result = self.run_checker("v0.1.0", fixture)

                self.assertNotEqual(0, result.returncode)
                self.assertIn("image version label", result.stderr)

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
            'apiVersion: v2\nname: perf-sentinel-hub\ntype: application\nversion: 0.1.0\nappVersion: "0.1.0"\n',
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
