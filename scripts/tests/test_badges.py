import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parents[2]
CHECKER = REPOSITORY / "scripts" / "check-badges.py"
CANONICAL_LICENSE = (REPOSITORY / "LICENSE").read_text(encoding="utf-8")
REPO_URL = "https://github.com/robintra/PerfSentinelHub"
SONAR_URL = "https://sonarcloud.io"
SONAR_KEY = "robintrassard_PerfSentinelHub"
BADGES = {
    ".NET": (
        "https://img.shields.io/badge/dynamic/json?url=https%3A%2F%2Fraw.githubusercontent.com"
        "%2Frobintra%2FPerfSentinelHub%2Fmain%2Fglobal.json&query=%24.sdk.version"
        "&label=.NET&color=512BD4&logo=dotnet&logoColor=white",
        "https://dotnet.microsoft.com/",
    ),
    "CI": (f"{REPO_URL}/actions/workflows/ci.yml/badge.svg", f"{REPO_URL}/actions/workflows/ci.yml"),
    "Security Audit": (
        f"{REPO_URL}/actions/workflows/security-audit.yml/badge.svg",
        f"{REPO_URL}/actions/workflows/security-audit.yml",
    ),
    "CodeQL": (
        f"{REPO_URL}/actions/workflows/codeql.yml/badge.svg",
        f"{REPO_URL}/actions/workflows/codeql.yml",
    ),
    "Coverage": (
        f"{SONAR_URL}/api/project_badges/measure?project={SONAR_KEY}&metric=coverage",
        f"{SONAR_URL}/summary/overall?id={SONAR_KEY}",
    ),
    "Quality Gate": (
        f"{SONAR_URL}/api/project_badges/measure?project={SONAR_KEY}&metric=alert_status",
        f"{SONAR_URL}/summary/overall?id={SONAR_KEY}",
    ),
    "Release": (
        f"{REPO_URL}/actions/workflows/release.yml/badge.svg",
        f"{REPO_URL}/actions/workflows/release.yml",
    ),
    "Latest release": (
        "https://img.shields.io/github/v/release/robintra/PerfSentinelHub"
        "?display_name=tag&sort=semver&color=512BD4",
        f"{REPO_URL}/releases/latest",
    ),
    "Container image": (
        "https://img.shields.io/badge/ghcr.io-perf--sentinel--hub-2496ED"
        "?logo=docker&logoColor=white",
        f"{REPO_URL}/pkgs/container/perf-sentinel-hub",
    ),
    "Helm chart": (
        "https://img.shields.io/badge/chart-perf--sentinel--hub-0F1689"
        "?logo=helm&logoColor=white",
        f"{REPO_URL}/pkgs/container/charts%2Fperf-sentinel-hub",
    ),
}


def badge(label, image, destination):
    return f'    <a href="{destination}"><img src="{image}" alt="{label}" /></a>'


def complete_readme():
    return '<p align="center">\n' + "".join(
        badge(label, image, destination) + "\n"
        for label, (image, destination) in BADGES.items()
    ) + "</p>\n\n# PerfSentinelHub\n\n"


def add_to_badge_block(addition):
    return complete_readme()[:-1] + addition + "\n"


def write_root(
    root,
    readme,
    *,
    include_daily_workflow=True,
    sdk_version="10.0.400",
    license_text=CANONICAL_LICENSE,
):
    if isinstance(readme, bytes):
        (root / "README.md").write_bytes(readme)
    else:
        (root / "README.md").write_text(readme, encoding="utf-8")
    workflows = root / ".github" / "workflows"
    workflows.mkdir(parents=True)
    for name in ("ci.yml", "codeql.yml", "release.yml"):
        (workflows / name).write_text(f"name: {name}\n", encoding="utf-8")
    if include_daily_workflow:
        (workflows / "security-audit.yml").write_text(
            "name: Security Audit\n", encoding="utf-8"
        )
    (root / "global.json").write_text(
        f'{{"sdk": {{"version": "{sdk_version}"}}}}\n', encoding="utf-8"
    )
    (root / "LICENSE").write_text(license_text, encoding="utf-8")
    (root / ".github/workflows/ci.yml").write_text(
        "sonar.projectKey=robintrassard_PerfSentinelHub\n", encoding="utf-8"
    )
    (root / "CHANGELOG.md").write_text("# Changelog\n", encoding="utf-8")
    (root / "Dockerfile").write_text("FROM scratch\n", encoding="utf-8")
    chart = root / "deploy" / "helm" / "perf-sentinel-hub"
    chart.mkdir(parents=True)
    (chart / "Chart.yaml").write_text("name: perf-sentinel-hub\n", encoding="utf-8")


def run_checker(readme, **fixture_options):
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        write_root(root, readme, **fixture_options)
        return subprocess.run(
            [sys.executable, str(CHECKER), "--root", str(root)],
            cwd=root,
            text=True,
            capture_output=True,
            check=False,
        )


class BadgeTests(unittest.TestCase):
    def test_accepts_the_complete_evidence_linked_badge_set(self):
        result = run_checker(complete_readme())

        self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_non_lf_line_endings_in_the_canonical_prefix(self):
        canonical = complete_readme().encode()
        variants = {
            "CRLF": canonical.replace(b"\n", b"\r\n"),
            "CR": canonical.replace(b"\n", b"\r"),
            "mixed": canonical.replace(b"\n", b"\r\n", 1),
        }
        for name, readme in variants.items():
            with self.subTest(name=name):
                result = run_checker(readme)

                self.assertEqual(1, result.returncode)
                self.assertIn("canonical top badge block", result.stderr)

    def test_rejects_collapsed_shortcut_and_title_variants(self):
        first, second = (
            badge(label, image, destination)
            for label, (image, destination) in tuple(BADGES.items())[:2]
        )
        variants = {
            "collapsed lines": complete_readme().replace(
                f"{first}\n{second}", f"{first}{second}"
            ),
            "shortcut image": add_to_badge_block("![Build]\n"),
            "different title": complete_readme().replace(
                "# PerfSentinelHub", "# Other", 1
            ),
        }
        for name, readme in variants.items():
            with self.subTest(name=name):
                result = run_checker(readme)

                self.assertEqual(1, result.returncode)
                self.assertIn("canonical top badge block", result.stderr)

    def test_requires_each_named_badge(self):
        for label, (image, destination) in BADGES.items():
            with self.subTest(label=label):
                readme = complete_readme().replace(
                    badge(label, image, destination) + "\n", ""
                )

                result = run_checker(readme)

                self.assertEqual(1, result.returncode)
                self.assertIn("canonical top badge block", result.stderr)

    def test_rejects_a_badge_linked_to_a_decorative_destination(self):
        image, destination = BADGES["CodeQL"]
        readme = complete_readme().replace(
            badge("CodeQL", image, destination),
            badge("CodeQL", image, "https://github.com/robintra/PerfSentinelHub"),
        )

        result = run_checker(readme)

        self.assertEqual(1, result.returncode)
        self.assertIn("canonical top badge block", result.stderr)

    def test_rejects_an_unlinked_decorative_badge(self):
        readme = add_to_badge_block(
            "![Build](https://img.shields.io/badge/build-pretty-green)\n"
        )

        result = run_checker(readme)

        self.assertEqual(1, result.returncode)
        self.assertIn("canonical top badge block", result.stderr)

    def test_rejects_an_unlinked_decorative_image_with_a_generic_url(self):
        readme = add_to_badge_block("![Build](https://example.test/status.svg)\n")

        result = run_checker(readme)

        self.assertEqual(1, result.returncode)
        self.assertIn("canonical top badge block", result.stderr)

    def test_allows_an_ordinary_image_outside_the_top_badge_block(self):
        readme = complete_readme() + "\n## Architecture\n\n![Flow](docs/flow.svg)\n"

        result = run_checker(readme)

        self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_noncanonical_image_syntax_in_the_top_badge_block(self):
        additions = {
            "reference image": "![Build][status]\n[status]: https://example.test/status.svg\n",
            "reference-linked image": "[![Build][status]][evidence]\n",
            "HTML img": '<img alt="Build" src="https://example.test/status.svg">\n',
            "HTML picture": "<picture><source srcset=\"status.svg\"></picture>\n",
            "HTML svg": "<svg role=\"img\"></svg>\n",
        }
        for name, addition in additions.items():
            with self.subTest(name=name):
                result = run_checker(add_to_badge_block(addition))

                self.assertEqual(1, result.returncode)
                self.assertIn("canonical top badge block", result.stderr)

    def test_rejects_an_unknown_linked_decorative_badge(self):
        readme = add_to_badge_block(
            badge(
                "Build",
                "https://img.shields.io/badge/build-pretty-green",
                "https://github.com/robintra/PerfSentinelHub",
            ) + "\n"
        )

        result = run_checker(readme)

        self.assertEqual(1, result.returncode)
        self.assertIn("canonical top badge block", result.stderr)

    def test_rejects_a_link_to_missing_committed_workflow_evidence(self):
        result = run_checker(complete_readme(), include_daily_workflow=False)

        self.assertEqual(1, result.returncode)
        self.assertIn("missing local evidence: .github/workflows/security-audit.yml", result.stderr)

    def test_rejects_license_badge_when_the_license_is_not_canonical(self):
        invalid_licenses = {
            "truncated AGPL": CANONICAL_LICENSE[:500],
            "MIT with copied phrases": (
                "MIT License\nGNU AFFERO GENERAL PUBLIC LICENSE\n"
                "Version 3, 19 November 2007\n"
            ),
        }
        for name, license_text in invalid_licenses.items():
            with self.subTest(name=name):
                result = run_checker(complete_readme(), license_text=license_text)

                self.assertEqual(1, result.returncode)
                self.assertIn("LICENSE differs from canonical AGPL-3.0-only", result.stderr)


if __name__ == "__main__":
    unittest.main()
