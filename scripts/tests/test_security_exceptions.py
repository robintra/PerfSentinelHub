import json
import re
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timedelta
from pathlib import Path
from zoneinfo import ZoneInfo


REPOSITORY = Path(__file__).resolve().parents[2]
CHECKER = REPOSITORY / "scripts" / "check-security-exceptions.py"
SECURITY_WORKFLOW = REPOSITORY / ".github" / "workflows" / "security-audit.yml"
CODEQL_WORKFLOW = REPOSITORY / ".github" / "workflows" / "codeql.yml"
RELEASE_WORKFLOW = REPOSITORY / ".github" / "workflows" / "release-verification.yml"
GITLEAKS_IGNORE = REPOSITORY / ".gitleaksignore"
OSV_CONFIG = REPOSITORY / "osv-scanner.toml"
PARIS_TODAY = datetime.now(ZoneInfo("Europe/Paris")).date()


def valid_exception(**overrides):
    entry = {
        "advisory": "GHSA-xxxx-xxxx-xxxx",
        "exposure": "The affected API is not reachable from configured Hub inputs.",
        "owner": "@robintra",
        "expires": (PARIS_TODAY + timedelta(days=30)).isoformat(),
        "paths": ["PerfSentinelHub/packages.lock.json"],
    }
    entry.update(overrides)
    return entry


def run_checker(payload):
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        config = root / "config"
        config.mkdir()
        (config / "security-exceptions.json").write_text(
            json.dumps(payload), encoding="utf-8"
        )
        return subprocess.run(
            [sys.executable, str(CHECKER)],
            cwd=root,
            text=True,
            capture_output=True,
            check=False,
        )


class SecurityExceptionsTests(unittest.TestCase):
    def test_accepts_an_empty_policy_and_a_short_lived_exception(self):
        for exceptions in ([], [valid_exception()]):
            with self.subTest(exceptions=exceptions):
                result = run_checker(
                    {"schema_version": 1, "exceptions": exceptions}
                )

                self.assertEqual(0, result.returncode, result.stderr)

    def test_requires_owner_and_exposure(self):
        for field in ("owner", "exposure"):
            with self.subTest(field=field):
                entry = valid_exception()
                del entry[field]

                result = run_checker(
                    {"schema_version": 1, "exceptions": [entry]}
                )

                self.assertEqual(1, result.returncode)
                self.assertIn(field, result.stderr)

    def test_rejects_multiline_owner_and_exposure_metadata(self):
        for field in ("owner", "exposure"):
            with self.subTest(field=field):
                result = run_checker(
                    {
                        "schema_version": 1,
                        "exceptions": [valid_exception(**{field: "first\nsecond"})],
                    }
                )

                self.assertEqual(1, result.returncode)
                self.assertIn(field, result.stderr)

    def test_rejects_expired_exceptions(self):
        result = run_checker(
            {
                "schema_version": 1,
                "exceptions": [
                    valid_exception(
                        expires=(PARIS_TODAY - timedelta(days=1)).isoformat()
                    )
                ],
            }
        )

        self.assertEqual(1, result.returncode)
        self.assertIn("expired", result.stderr)

    def test_rejects_expiry_more_than_ninety_days_away(self):
        result = run_checker(
            {
                "schema_version": 1,
                "exceptions": [
                    valid_exception(
                        expires=(PARIS_TODAY + timedelta(days=91)).isoformat()
                    )
                ],
            }
        )

        self.assertEqual(1, result.returncode)
        self.assertIn("90 days", result.stderr)

    def test_rejects_unknown_advisory_syntax(self):
        result = run_checker(
            {
                "schema_version": 1,
                "exceptions": [valid_exception(advisory="ADVISORY-123")],
            }
        )

        self.assertEqual(1, result.returncode)
        self.assertIn("advisory", result.stderr)

    def test_rejects_malformed_entries_and_paths_without_traceback(self):
        malformed = (
            {"schema_version": True, "exceptions": []},
            {"schema_version": 1, "exceptions": {}},
            {"schema_version": 1, "exceptions": [None]},
            {
                "schema_version": 1,
                "exceptions": [valid_exception(paths=[])],
            },
            {
                "schema_version": 1,
                "exceptions": [valid_exception(paths=["../packages.lock.json"])],
            },
            {
                "schema_version": 1,
                "exceptions": [valid_exception(unexpected="value")],
            },
        )
        for payload in malformed:
            with self.subTest(payload=payload):
                result = run_checker(payload)

                self.assertEqual(1, result.returncode)
                self.assertNotIn("Traceback", result.stderr)

    def test_rejects_duplicate_advisories(self):
        result = run_checker(
            {
                "schema_version": 1,
                "exceptions": [valid_exception(), valid_exception()],
            }
        )

        self.assertEqual(1, result.returncode)
        self.assertIn("duplicate", result.stderr)


class SecurityWorkflowContractTests(unittest.TestCase):
    def test_daily_audit_covers_every_required_scanner_and_trigger(self):
        workflow = SECURITY_WORKFLOW.read_text(encoding="utf-8")

        self.assertIn("cron: '17 5 * * *'", workflow)
        for path in (
            "Directory.Packages.props",
            "**/packages.lock.json",
            "Dockerfile",
            "deploy/helm/**",
            ".github/workflows/**",
            "config/security-exceptions.json",
        ):
            self.assertIn(path, workflow)
        for command in (
            "make audit",
            "osv-scanner scan",
            "gitleaks git",
            "trivy-action",
            "zizmor",
            "scorecard-action",
            "syft",
            "check-supply-chain.py",
            "check-security-exceptions.py",
        ):
            self.assertIn(command, workflow)
        self.assertIn("fetch-depth: 0", workflow)
        self.assertIn("--log-opts=--all", workflow)

    def test_audit_uploads_each_sarif_with_a_distinct_category(self):
        workflow = SECURITY_WORKFLOW.read_text(encoding="utf-8")
        categories = re.findall(r"^\s+category: (security-audit/[a-z0-9-]+)$", workflow, re.MULTILINE)

        self.assertEqual(
            {
                "security-audit/gitleaks",
                "security-audit/osv",
                "security-audit/scorecard",
                "security-audit/trivy-config",
                "security-audit/trivy-fs",
                "security-audit/trivy-image",
                "security-audit/zizmor",
            },
            set(categories),
        )
        self.assertEqual(len(categories), len(set(categories)))

    def test_scheduled_notification_is_one_issue_keyed_by_workflow_name(self):
        workflow = SECURITY_WORKFLOW.read_text(encoding="utf-8")
        notification = workflow.split("  notify:", maxsplit=1)[1]

        self.assertIn("github.event_name == 'schedule'", notification)
        self.assertIn("issues: write", notification)
        self.assertIn("security-audit", notification)
        self.assertIn("context.workflow", notification)
        self.assertIn("listForRepo", notification)
        self.assertIn("issues.update", notification)
        self.assertIn("issues.create", notification)

    def test_codeql_uses_manual_locked_release_build_and_both_query_suites(self):
        workflow = CODEQL_WORKFLOW.read_text(encoding="utf-8")

        self.assertIn("pull_request:", workflow)
        self.assertIn("branches: [main]", workflow)
        self.assertIn("schedule:", workflow)
        self.assertIn("build-mode: manual", workflow)
        self.assertIn("queries: +security-extended", workflow)
        self.assertIn("dotnet restore PerfSentinelHub.sln --locked-mode", workflow)
        self.assertIn(
            "dotnet build PerfSentinelHub.sln -c Release --no-restore --warnaserror",
            workflow,
        )
        self.assertIn("github/codeql-action/init@", workflow)
        self.assertIn("github/codeql-action/analyze@", workflow)

    def test_release_verification_is_secret_free_and_checks_stable_release_hashes(self):
        workflow = RELEASE_WORKFLOW.read_text(encoding="utf-8")

        self.assertIn("workflow_dispatch:", workflow)
        self.assertIn("release:", workflow)
        self.assertIn("types: [published]", workflow)
        self.assertIn("SHA256SUMS", workflow)
        self.assertIn("sha256", workflow)
        self.assertIn("^v0[.][0-9]+[.][0-9]+$", workflow)
        self.assertNotIn("secrets.", workflow)

    def test_every_third_party_action_is_immutably_pinned(self):
        for path in (SECURITY_WORKFLOW, CODEQL_WORKFLOW, RELEASE_WORKFLOW):
            workflow = path.read_text(encoding="utf-8")
            for number, line in enumerate(workflow.splitlines(), start=1):
                if "uses:" not in line:
                    continue
                with self.subTest(path=path.name, line=number):
                    self.assertRegex(line, r"uses: [A-Za-z0-9._/-]+@[0-9a-f]{40}(?: # .+)?$")

    def test_gitleaks_exceptions_are_only_exact_reviewed_history_fingerprints(self):
        expected = (
            "0bc53d0a3c91654ed25ca09de6fc08c6a6488535:PerfSentinelHub.Tests/ConfigurationTests.cs:generic-api-key:99",
            "b80ab61dc3f7cf165be9f6b30854463e4fa2962d:PerfSentinelHub.Tests/HubApplicationFactory.cs:generic-api-key:45",
            "b80ab61dc3f7cf165be9f6b30854463e4fa2962d:PerfSentinelHub.Tests/ImportApiTests.cs:generic-api-key:11",
            "b80ab61dc3f7cf165be9f6b30854463e4fa2962d:README.md:generic-api-key:18",
        )
        fingerprints = tuple(GITLEAKS_IGNORE.read_text(encoding="utf-8").splitlines())

        self.assertEqual(expected, fingerprints)
        self.assertTrue(
            all(
                re.fullmatch(
                    r"[0-9a-f]{40}:[A-Za-z0-9._/-]+:generic-api-key:[1-9][0-9]*",
                    fingerprint,
                )
                for fingerprint in fingerprints
            )
        )
        workflow = SECURITY_WORKFLOW.read_text(encoding="utf-8")
        self.assertIn("--gitleaks-ignore-path=.gitleaksignore", workflow)

    def test_license_override_is_narrow_and_the_allowlist_is_spdx_only(self):
        self.assertEqual(
            '''[[PackageOverrides]]
name = "SQLite"
version = "3.53.4"
ecosystem = "NuGet"
license.override = ["blessing"]
reason = "SQLite is distributed under the SPDX-listed SQLite Blessing."
''',
            OSV_CONFIG.read_text(encoding="utf-8"),
        )
        workflow = SECURITY_WORKFLOW.read_text(encoding="utf-8")
        self.assertIn("--config=osv-scanner.toml", workflow)
        self.assertIn("--licenses=MIT,Apache-2.0,blessing", workflow)


if __name__ == "__main__":
    unittest.main()
