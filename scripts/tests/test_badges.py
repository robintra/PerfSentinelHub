import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parents[2]
CHECKER = REPOSITORY / "scripts" / "check-badges.py"
BADGES = {
    "CI": (
        "https://github.com/robintra/PerfSentinelHub/actions/workflows/ci.yml/badge.svg",
        "https://github.com/robintra/PerfSentinelHub/actions/workflows/ci.yml",
    ),
    "Sonar quality": (
        "https://sonarcloud.io/api/project_badges/measure?project=robintra_PerfSentinelHub&metric=alert_status",
        "https://sonarcloud.io/summary/new_code?id=robintra_PerfSentinelHub",
    ),
    "Sonar coverage": (
        "https://sonarcloud.io/api/project_badges/measure?project=robintra_PerfSentinelHub&metric=coverage",
        "https://sonarcloud.io/component_measures?id=robintra_PerfSentinelHub&metric=coverage&view=list",
    ),
    "Qodana": (
        "https://img.shields.io/badge/Qodana-configured-lightgrey",
        "https://github.com/robintra/PerfSentinelHub/actions/workflows/ci.yml",
    ),
    "CodeQL": (
        "https://github.com/robintra/PerfSentinelHub/actions/workflows/codeql.yml/badge.svg",
        "https://github.com/robintra/PerfSentinelHub/actions/workflows/codeql.yml",
    ),
    "Daily audit": (
        "https://github.com/robintra/PerfSentinelHub/actions/workflows/security-audit.yml/badge.svg",
        "https://github.com/robintra/PerfSentinelHub/actions/workflows/security-audit.yml",
    ),
    "OpenSSF Scorecard": (
        "https://api.securityscorecards.dev/projects/github.com/robintra/PerfSentinelHub/badge",
        "https://securityscorecards.dev/viewer/?uri=github.com/robintra/PerfSentinelHub",
    ),
    "Latest release": (
        "https://img.shields.io/github/v/release/robintra/PerfSentinelHub?display_name=tag&sort=semver",
        "https://github.com/robintra/PerfSentinelHub/releases/latest",
    ),
    "GHCR": (
        "https://img.shields.io/badge/GHCR-configured-lightgrey",
        "https://github.com/robintra/PerfSentinelHub/pkgs/container/perf-sentinel-hub",
    ),
    "Helm": (
        "https://img.shields.io/badge/Helm-configured-lightgrey",
        "https://github.com/robintra/PerfSentinelHub/pkgs/container/charts%2Fperf-sentinel-hub",
    ),
    ".NET": (
        "https://img.shields.io/badge/.NET-10.0.302-512BD4",
        "https://github.com/robintra/PerfSentinelHub/blob/main/global.json",
    ),
    "License": (
        "https://img.shields.io/github/license/robintra/PerfSentinelHub",
        "https://github.com/robintra/PerfSentinelHub/blob/main/LICENSE",
    ),
}


def badge(label, image, destination):
    return f"[![{label}]({image})]({destination})"


def complete_readme():
    return "# PerfSentinelHub\n\n" + "\n".join(
        badge(label, image, destination)
        for label, (image, destination) in BADGES.items()
    ) + "\n"


def write_root(
    root,
    readme,
    *,
    include_daily_workflow=True,
    sdk_version="10.0.302",
    license_text="GNU AFFERO GENERAL PUBLIC LICENSE\nVersion 3, 19 November 2007\n",
):
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
    (root / "sonar-project.properties").write_text(
        "sonar.projectKey=robintra_PerfSentinelHub\n", encoding="utf-8"
    )


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

    def test_requires_each_named_badge(self):
        for label, (image, destination) in BADGES.items():
            with self.subTest(label=label):
                readme = complete_readme().replace(
                    badge(label, image, destination) + "\n", ""
                )

                result = run_checker(readme)

                self.assertEqual(1, result.returncode)
                self.assertIn(f"missing badge: {label}", result.stderr)

    def test_rejects_a_badge_linked_to_a_decorative_destination(self):
        image, destination = BADGES["CodeQL"]
        readme = complete_readme().replace(
            badge("CodeQL", image, destination),
            badge("CodeQL", image, "https://github.com/robintra/PerfSentinelHub"),
        )

        result = run_checker(readme)

        self.assertEqual(1, result.returncode)
        self.assertIn("CodeQL badge must link to its evidence", result.stderr)

    def test_rejects_an_unlinked_decorative_badge(self):
        readme = complete_readme() + "![Build](https://img.shields.io/badge/build-pretty-green)\n"

        result = run_checker(readme)

        self.assertEqual(1, result.returncode)
        self.assertIn("badge image without an evidence link", result.stderr)

    def test_rejects_an_unlinked_decorative_image_with_a_generic_url(self):
        readme = complete_readme() + "![Build](https://example.test/status.svg)\n"

        result = run_checker(readme)

        self.assertEqual(1, result.returncode)
        self.assertIn("image without an evidence link: Build", result.stderr)

    def test_allows_an_ordinary_image_outside_the_top_badge_block(self):
        readme = complete_readme() + "\n## Architecture\n\n![Flow](docs/flow.svg)\n"

        result = run_checker(readme)

        self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_an_unknown_linked_decorative_badge(self):
        readme = complete_readme() + badge(
            "Build",
            "https://img.shields.io/badge/build-pretty-green",
            "https://github.com/robintra/PerfSentinelHub",
        ) + "\n"

        result = run_checker(readme)

        self.assertEqual(1, result.returncode)
        self.assertIn("unsupported badge: Build", result.stderr)

    def test_rejects_a_link_to_missing_committed_workflow_evidence(self):
        result = run_checker(complete_readme(), include_daily_workflow=False)

        self.assertEqual(1, result.returncode)
        self.assertIn("missing local evidence: .github/workflows/security-audit.yml", result.stderr)

    def test_rejects_dotnet_badge_when_the_pinned_sdk_differs(self):
        result = run_checker(complete_readme(), sdk_version="10.0.999")

        self.assertEqual(1, result.returncode)
        self.assertIn(".NET badge differs from global.json", result.stderr)

    def test_rejects_license_badge_when_the_license_is_not_agpl_v3(self):
        result = run_checker(complete_readme(), license_text="MIT License\n")

        self.assertEqual(1, result.returncode)
        self.assertIn("License badge requires GNU AGPL version 3 evidence", result.stderr)


if __name__ == "__main__":
    unittest.main()
