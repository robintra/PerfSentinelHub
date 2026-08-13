import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parents[2]
CHECKER = REPOSITORY / "scripts" / "check-analysis-config.py"
QODANA_DIGEST = "sha256:c893fb5f5dbe54cd4b9c2cb1bd11d711242add66c5a3ac65fe7fc302cdb8c0a3"


QODANA = f'''version: "1.0"
linter: jetbrains/qodana-dotnet:2026.1@{QODANA_DIGEST}
profile:
  name: qodana.recommended
dotnet:
  solution: PerfSentinelHub.sln
exclude:
  - name: All
    paths:
      - PerfSentinelHub/bin
      - PerfSentinelHub/obj
      - PerfSentinelHub.Tests/bin
      - PerfSentinelHub.Tests/obj
      - TestResults
      - artifacts/coverage
      - artifacts/sonar
      - graphify-out
failureConditions:
  severityThresholds:
    critical: 0
    high: 0
'''

SONAR = '''sonar.projectKey=robintrassard_PerfSentinelHub
sonar.organization=robintrassard
sonar.host.url=https://sonarcloud.io
sonar.sources=PerfSentinelHub
sonar.tests=PerfSentinelHub.Tests
sonar.exclusions=**/bin/**,**/obj/**,TestResults/**,artifacts/coverage/**,artifacts/sonar/**,graphify-out/**
sonar.coverageReportPaths=artifacts/sonar/SonarQube.xml
sonar.cs.vstest.reportsPaths=artifacts/coverage/tests.trx
sonar.sourceEncoding=UTF-8
'''


def secret_inventory(**entry_overrides):
    entries = []
    for name, purpose in (
        ("CI_GATE_APP_ID", "Identify the dedicated CI gate GitHub App."),
        ("CI_GATE_APP_PRIVATE_KEY", "Mint a short-lived CI gate installation token."),
        ("QODANA_TOKEN", "Authenticate the trusted Qodana CI analysis job."),
        ("SONAR_TOKEN", "Authenticate the trusted SonarCloud analysis job."),
    ):
        entry = {
            "name": name,
            "scope": "GitHub Actions trusted analysis job only.",
            "purpose": purpose,
            "owner": "PerfSentinelHub repository maintainers.",
            "rotation_procedure": (
                "Revoke the provider token, create a replacement, update the GitHub Actions secret, "
                "and verify the trusted analysis job."
            ),
        }
        if name == "SONAR_TOKEN":
            entry.update(entry_overrides)
        entries.append(entry)
    return {"schema_version": 1, "secrets": entries}


def supply_chain():
    return {
        "schema_version": 1,
        "inventory": [
            {
                "name": "jetbrains/qodana-dotnet",
                "kind": "container",
                "version": "2026.1",
                "digest_or_sha": QODANA_DIGEST,
                "released_at": "2026-04-21T09:02:03.110286Z",
                "source": "https://hub.docker.com/v2/namespaces/jetbrains/repositories/qodana-dotnet/tags/2026.1",
                "reason": "Latest eligible stable Qodana for .NET image.",
            }
        ],
    }


def write_repository(root, *, qodana=QODANA, sonar=SONAR, secrets=None, workflow=None):
    (root / "qodana.yaml").write_text(qodana, encoding="utf-8")
    (root / "sonar-project.properties").write_text(sonar, encoding="utf-8")
    config = root / "config"
    config.mkdir()
    (config / "secret-inventory.json").write_text(
        json.dumps(secrets or secret_inventory()), encoding="utf-8"
    )
    (config / "supply-chain.json").write_text(
        json.dumps(supply_chain()), encoding="utf-8"
    )
    if workflow is not None:
        workflows = root / ".github" / "workflows"
        workflows.mkdir(parents=True)
        (workflows / "analysis.yml").write_text(workflow, encoding="utf-8")


def run_checker(root, *arguments):
    return subprocess.run(
        [sys.executable, str(CHECKER), *arguments],
        cwd=root,
        text=True,
        capture_output=True,
        check=False,
    )


class AnalysisConfigCheckerTests(unittest.TestCase):
    def test_accepts_strict_analysis_configuration_and_known_workflow_secrets(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_repository(
                root,
                workflow=(
                    "jobs:\n  analysis:\n    env:\n"
                    "      CI_GATE_APP_ID: ${{ secrets.CI_GATE_APP_ID }}\n"
                    "      CI_GATE_APP_PRIVATE_KEY: ${{ secrets.CI_GATE_APP_PRIVATE_KEY }}\n"
                    "      QODANA_TOKEN: ${{ secrets.QODANA_TOKEN }}\n"
                    "      SONAR_TOKEN: ${{ secrets.SONAR_TOKEN }}\n"
                ),
            )

            result = run_checker(root)

            self.assertEqual(0, result.returncode, result.stderr)

    def test_requires_dedicated_gate_app_secret_metadata(self):
        for missing in ("CI_GATE_APP_ID", "CI_GATE_APP_PRIVATE_KEY"):
            with self.subTest(missing=missing):
                with tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    inventory = secret_inventory()
                    inventory["secrets"] = [
                        entry for entry in inventory["secrets"] if entry["name"] != missing
                    ]
                    write_repository(root, secrets=inventory)

                    result = run_checker(root)

                    self.assertEqual(1, result.returncode)
                    self.assertIn(missing, result.stderr)

    def test_rejects_starter_profile_and_commented_failure_conditions(self):
        cases = (
            QODANA.replace("qodana.recommended", "qodana.starter"),
            QODANA.replace("failureConditions:", "#failureConditions:"),
        )
        for qodana in cases:
            with self.subTest(qodana=qodana):
                with tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    write_repository(root, qodana=qodana)

                    result = run_checker(root)

                    self.assertEqual(1, result.returncode)
                    self.assertIn("qodana.yaml", result.stderr)

    def test_rejects_mutable_qodana_image_and_blanket_exclusion(self):
        cases = (
            QODANA.replace(f"@{QODANA_DIGEST}", ""),
            QODANA.replace("    paths:\n", "").replace(
                "      - PerfSentinelHub/bin\n      - PerfSentinelHub/obj\n"
                "      - PerfSentinelHub.Tests/bin\n      - PerfSentinelHub.Tests/obj\n"
                "      - TestResults\n      - artifacts/coverage\n      - artifacts/sonar\n"
                "      - graphify-out\n",
                "",
            ),
        )
        for qodana in cases:
            with self.subTest(qodana=qodana):
                with tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    write_repository(root, qodana=qodana)

                    result = run_checker(root)

                    self.assertEqual(1, result.returncode)
                    self.assertIn("qodana.yaml", result.stderr)

    def test_rejects_qodana_exclusion_outside_generated_build_paths(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_repository(
                root,
                qodana=QODANA.replace("      - graphify-out", "      - PerfSentinelHub/Api"),
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("PerfSentinelHub/Api", result.stderr)

    def test_rejects_missing_sonar_coverage_path_and_source_exclusion(self):
        cases = (
            SONAR.replace(
                "sonar.coverageReportPaths=artifacts/sonar/SonarQube.xml\n", ""
            ),
            SONAR.replace("graphify-out/**", "PerfSentinelHub/Api/**"),
        )
        for sonar in cases:
            with self.subTest(sonar=sonar):
                with tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    write_repository(root, sonar=sonar)

                    result = run_checker(root)

                    self.assertEqual(1, result.returncode)
                    self.assertIn("sonar-project.properties", result.stderr)

    def test_rejects_ambiguous_yaml_and_properties_forms(self):
        cases = (
            (QODANA.replace("profile:", "'profile':"), SONAR),
            (QODANA, SONAR.replace("sonar.sources=", "sonar.sources =")),
            (QODANA, SONAR + "sonar.sources=Other\n"),
        )
        for qodana, sonar in cases:
            with self.subTest(qodana=qodana, sonar=sonar):
                with tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    write_repository(root, qodana=qodana, sonar=sonar)

                    result = run_checker(root)

                    self.assertEqual(1, result.returncode)

    def test_rejects_workflow_secret_absent_from_inventory_without_echoing_expression(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            expression = "${{ secrets.DEPLOY_TOKEN }}"
            write_repository(root, workflow=f"env:\n  TOKEN: {expression}\n")

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("DEPLOY_TOKEN", result.stderr)
            self.assertNotIn(expression, result.stderr)

    def test_rejects_noncanonical_workflow_secret_reference(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_repository(
                root,
                workflow="env:\n  TOKEN: ${{ secrets['SONAR_TOKEN'] }}\n",
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("non-canonical secret reference", result.stderr)

    def test_rejects_secret_metadata_missing_required_field(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_repository(root, secrets=secret_inventory(owner=""))

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("owner", result.stderr)

    def test_requires_exact_integer_secret_inventory_schema_version(self):
        cases = ((True, 1), (1.0, 1), ("1", 1), (1, 0))
        for schema_version, expected_status in cases:
            with self.subTest(schema_version=schema_version):
                with tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    inventory = secret_inventory()
                    inventory["schema_version"] = schema_version
                    write_repository(root, secrets=inventory)

                    result = run_checker(root)

                    self.assertEqual(expected_status, result.returncode, result.stderr)
                    self.assertNotIn("Traceback", result.stderr)

    def test_rejects_field_or_metadata_that_resembles_a_secret_value(self):
        cases = (
            secret_inventory(value="sqa_abcdefghijklmnopqrstuvwxyz012345"),
            secret_inventory(purpose="Bearer abcdefghijklmnopqrstuvwxyz0123456789"),
        )
        for secrets in cases:
            with self.subTest(secrets=secrets):
                with tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    write_repository(root, secrets=secrets)

                    result = run_checker(root)

                    self.assertEqual(1, result.returncode)
                    self.assertIn("secret-inventory.json", result.stderr)

    def test_require_inputs_checks_cobertura_sonarqube_and_test_reports(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_repository(root)

            missing = run_checker(root, "--require-analysis-inputs")
            (root / "artifacts" / "coverage").mkdir(parents=True)
            (root / "artifacts" / "sonar").mkdir(parents=True)
            (root / "artifacts" / "coverage" / "coverage.cobertura.xml").write_text(
                '<coverage lines-covered="0" lines-valid="0" />\n', encoding="utf-8"
            )
            (root / "artifacts" / "coverage" / "tests.trx").write_text(
                '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010" />\n',
                encoding="utf-8",
            )
            (root / "artifacts" / "sonar" / "SonarQube.xml").write_text(
                '<coverage version="1" />\n', encoding="utf-8"
            )
            present = run_checker(root, "--require-analysis-inputs")

            self.assertEqual(1, missing.returncode)
            self.assertIn("coverage.cobertura.xml", missing.stderr)
            self.assertEqual(0, present.returncode, present.stderr)


if __name__ == "__main__":
    unittest.main()
