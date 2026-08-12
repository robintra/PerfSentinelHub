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
            "changelog heading": (
                "CHANGELOG.md",
                "## [0.1.0] - 2026-08-12",
                "## [0.1.1] - 2026-08-12",
            ),
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

    def test_rejects_case_insensitive_project_version_overrides_on_the_search_path(self):
        cases = (
            (
                "PerfSentinelHub/PerfSentinelHub.csproj",
                "<Project><PropertyGroup><Version>0.1.0</Version><versionprefix>0.1.1</versionprefix></PropertyGroup></Project>\n",
            ),
            (
                "Directory.Build.props",
                "<Project><PropertyGroup><version>0.1.1</version></PropertyGroup></Project>\n",
            ),
            (
                "Directory.Build.targets",
                "<Project><PropertyGroup><versionprefix>0.1.1</versionprefix></PropertyGroup></Project>\n",
            ),
            (
                "PerfSentinelHub/Directory.Build.props",
                "<Project><PropertyGroup><versionsuffix>rc.1</versionsuffix></PropertyGroup></Project>\n",
            ),
            (
                "PerfSentinelHub/Directory.Build.targets",
                "<Project><PropertyGroup><version>0.1.1</version></PropertyGroup></Project>\n",
            ),
        )
        for relative, content in cases:
            with self.subTest(relative=relative), tempfile.TemporaryDirectory(dir=self.root) as directory:
                fixture = Path(directory)
                self.write_contract(fixture)
                (fixture / relative).write_text(content, encoding="utf-8")

                result = self.run_checker("v0.1.0", fixture)

                self.assertNotEqual(0, result.returncode)
                self.assertIn("project version", result.stderr)

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

    def test_accepts_one_visible_changelog_heading_outside_hidden_markdown(self):
        with tempfile.TemporaryDirectory(dir=self.root) as directory:
            fixture = Path(directory)
            self.write_contract(fixture)
            (fixture / "CHANGELOG.md").write_text(
                "# Changelog\n\n"
                "<!-- ## [9.9.9] - 2000-01-01 -->\n"
                "<!--\n## [8.8.8] - 2000-01-01\n-->\n"
                "```markdown\n## [7.7.7] - 2000-01-01\n```\n"
                "~~~text\n## [6.6.6] - 2000-01-01\n~~~\n"
                "## [0.1.0] - 2026-08-12\n",
                encoding="utf-8",
            )

            result = self.run_checker("v0.1.0", fixture)

            self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_hidden_duplicate_unclosed_or_noncanonical_changelog_headings(self):
        heading = "## [0.1.0] - 2026-08-12"
        changelogs = (
            ("multiline comment", f"<!--\n{heading}\n-->\n"),
            ("single-line comment", f"<!-- {heading} -->\n"),
            ("backtick fence", f"```markdown\n{heading}\n```\n"),
            ("tilde fence", f"~~~text\n{heading}\n~~~\n"),
            ("duplicate visible", f"{heading}\n{heading}\n"),
            ("unclosed comment", f"{heading}\n<!--\n"),
            ("unclosed backtick fence", f"{heading}\n```text\n"),
            ("unclosed tilde fence", f"{heading}\n~~~text\n"),
            ("missing date", "## [0.1.0]\n"),
            ("invalid date", "## [0.1.0] - 2026-02-30\n"),
            ("trailing whitespace", f"{heading} \n"),
        )
        for name, changelog in changelogs:
            with self.subTest(name=name), tempfile.TemporaryDirectory(dir=self.root) as directory:
                fixture = Path(directory)
                self.write_contract(fixture)
                (fixture / "CHANGELOG.md").write_text(changelog, encoding="utf-8")

                result = self.run_checker("v0.1.0", fixture)

                self.assertNotEqual(0, result.returncode)
                self.assertIn("changelog heading", result.stderr)
                self.assertNotIn("Traceback", result.stderr)

    def test_rejects_indented_changelog_release_headings_as_noncanonical_ambiguities(self):
        heading = "## [0.1.0] - 2026-08-12"
        for spaces in (1, 2, 3, 4):
            with self.subTest(spaces=spaces), tempfile.TemporaryDirectory(dir=self.root) as directory:
                fixture = Path(directory)
                self.write_contract(fixture)
                (fixture / "CHANGELOG.md").write_text(
                    f"{heading}\n{' ' * spaces}{heading}\n",
                    encoding="utf-8",
                )

                result = self.run_checker("v0.1.0", fixture)

                self.assertNotEqual(0, result.returncode)
                self.assertIn("changelog heading", result.stderr)
                self.assertNotIn("Traceback", result.stderr)

    def test_rejects_backtick_fence_info_that_could_mask_a_release_heading(self):
        heading = "## [0.1.0] - 2026-08-12"
        with tempfile.TemporaryDirectory(dir=self.root) as directory:
            fixture = Path(directory)
            self.write_contract(fixture)
            (fixture / "CHANGELOG.md").write_text(
                f"{heading}\n```markdown`invalid\n{heading}\n```\n",
                encoding="utf-8",
            )

            result = self.run_checker("v0.1.0", fixture)

            self.assertNotEqual(0, result.returncode)
            self.assertIn("changelog heading", result.stderr)
            self.assertNotIn("Traceback", result.stderr)

    def test_accepts_backticks_in_tilde_fence_info(self):
        heading = "## [0.1.0] - 2026-08-12"
        with tempfile.TemporaryDirectory(dir=self.root) as directory:
            fixture = Path(directory)
            self.write_contract(fixture)
            (fixture / "CHANGELOG.md").write_text(
                f"{heading}\n~~~markdown`valid\n{heading}\n~~~\n",
                encoding="utf-8",
            )

            result = self.run_checker("v0.1.0", fixture)

            self.assertEqual(0, result.returncode, result.stderr)

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

    def test_accepts_the_version_label_in_the_effective_final_docker_stage(self):
        with tempfile.TemporaryDirectory(dir=self.root) as directory:
            fixture = Path(directory)
            self.write_contract(fixture)
            (fixture / "Dockerfile").write_text(
                "FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build\n"
                "WORKDIR /src\n"
                "FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final\n"
                "LABEL org.opencontainers.image.title=perf-sentinel-hub \\\n"
                "      org.opencontainers.image.version=\"0.1.0\"\n",
                encoding="utf-8",
            )

            result = self.run_checker("v0.1.0", fixture)

            self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_any_dockerfile_heredoc_without_a_traceback(self):
        with tempfile.TemporaryDirectory(dir=self.root) as directory:
            fixture = Path(directory)
            self.write_contract(fixture)
            (fixture / "Dockerfile").write_text(
                "FROM scratch\n"
                "RUN <<EOF\n"
                "LABEL org.opencontainers.image.version=0.1.0\n"
                "EOF\n",
                encoding="utf-8",
            )

            result = self.run_checker("v0.1.0", fixture)

            self.assertNotEqual(0, result.returncode)
            self.assertIn("image version label", result.stderr)
            self.assertNotIn("Traceback", result.stderr)

    def test_rejects_missing_or_ambiguous_final_docker_stage_version_labels(self):
        dockerfiles = (
            (
                "label only in build stage",
                "FROM scratch AS build\n"
                "LABEL org.opencontainers.image.version=0.1.0\n"
                "FROM scratch AS final\n",
            ),
            (
                "labels in build and final stages",
                "FROM scratch AS build\n"
                "LABEL org.opencontainers.image.version=0.1.0\n"
                "FROM scratch AS final\n"
                "LABEL org.opencontainers.image.version=0.1.0\n",
            ),
            (
                "duplicate labels in final stage",
                "FROM scratch AS build\n"
                "FROM scratch AS final\n"
                "LABEL org.opencontainers.image.version=0.1.0\n"
                "LABEL org.opencontainers.image.version=0.1.0\n",
            ),
        )
        for name, dockerfile in dockerfiles:
            with self.subTest(name=name), tempfile.TemporaryDirectory(dir=self.root) as directory:
                fixture = Path(directory)
                self.write_contract(fixture)
                (fixture / "Dockerfile").write_text(dockerfile, encoding="utf-8")

                result = self.run_checker("v0.1.0", fixture)

                self.assertNotEqual(0, result.returncode)
                self.assertIn("image version label", result.stderr)

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
