import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parents[2]
CHECKER = REPOSITORY / "scripts" / "check-analysis-config.py"



SCANNER = (
    "dotnet tool run dotnet-sonarscanner begin /k:robintrassard_PerfSentinelHub /o:robintrassard "
    "/d:sonar.host.url=https://sonarcloud.io /d:sonar.token=\"$SONAR_TOKEN\" "
    "/d:sonar.qualitygate.wait=true /d:sonar.coverageReportPaths=artifacts/sonar/SonarQube.xml "
    "/d:sonar.cs.vstest.reportsPaths=artifacts/coverage/tests.trx /d:sonar.sourceEncoding=UTF-8 "
    "/d:sonar.python.version=3.12 "
    '"/d:sonar.exclusions=**/bin/**,**/obj/**,TestResults/**,artifacts/coverage/**,artifacts/sonar/**,graphify-out/**" '
    '"/d:sonar.coverage.exclusions=scripts/**" '
    "/d:sonar.issue.ignore.multicriteria=nugethash,clipath,clishell,cliargs,sqlbuilder,asciiclass,imagepin "
    "/d:sonar.issue.ignore.multicriteria.nugethash.ruleKey=secrets:S6338 "
    "/d:sonar.issue.ignore.multicriteria.nugethash.resourceKey=config/supply-chain.json "
    "/d:sonar.issue.ignore.multicriteria.clipath.ruleKey=pythonsecurity:S8707 "
    '"/d:sonar.issue.ignore.multicriteria.clipath.resourceKey=scripts/**" '
    "/d:sonar.issue.ignore.multicriteria.clishell.ruleKey=pythonsecurity:S8705 "
    '"/d:sonar.issue.ignore.multicriteria.clishell.resourceKey=scripts/**" '
    "/d:sonar.issue.ignore.multicriteria.cliargs.ruleKey=pythonsecurity:S6350 "
    '"/d:sonar.issue.ignore.multicriteria.cliargs.resourceKey=scripts/**" '
    "/d:sonar.issue.ignore.multicriteria.sqlbuilder.ruleKey=csharpsquid:S2077 "
    "/d:sonar.issue.ignore.multicriteria.sqlbuilder.resourceKey=PerfSentinelHub/Storage/HubDatabase.cs "
    "/d:sonar.issue.ignore.multicriteria.asciiclass.ruleKey=python:S6353 "
    '"/d:sonar.issue.ignore.multicriteria.asciiclass.resourceKey=scripts/**" '
    "/d:sonar.issue.ignore.multicriteria.imagepin.ruleKey=docker:S8431 "
    "/d:sonar.issue.ignore.multicriteria.imagepin.resourceKey=Dockerfile\n"
)
SONAR = SCANNER


def secret_inventory(**entry_overrides):
    entries = []
    for name, purpose in (
        ("CI_GATE_APP_ID", "Identify the dedicated CI gate GitHub App."),
        ("CI_GATE_APP_PRIVATE_KEY", "Mint a short-lived CI gate installation token."),
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
        "inventory": [],
    }


def write_repository(root, *, sonar=SONAR, secrets=None, workflow=None):
    workflows = root / ".github" / "workflows"
    workflows.mkdir(parents=True)
    (workflows / "ci.yml").write_text(sonar, encoding="utf-8")
    (workflows / "sonar-main.yml").write_text(sonar, encoding="utf-8")
    config = root / "config"
    config.mkdir()
    (config / "secret-inventory.json").write_text(
        json.dumps(secrets or secret_inventory()), encoding="utf-8"
    )
    (config / "supply-chain.json").write_text(
        json.dumps(supply_chain()), encoding="utf-8"
    )
    if workflow is not None:
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

    def test_rejects_missing_sonar_coverage_path_and_source_exclusion(self):
        cases = (
            SONAR.replace("/d:sonar.coverageReportPaths=artifacts/sonar/SonarQube.xml ", ""),
            SONAR.replace("graphify-out/**", "PerfSentinelHub/Api/**"),
        )
        for sonar in cases:
            with self.subTest(sonar=sonar):
                with tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    write_repository(root, sonar=sonar)

                    result = run_checker(root)

                    self.assertEqual(1, result.returncode)
                    self.assertIn(".github/workflows/ci.yml", result.stderr)

    def test_rejects_missing_scanner_arguments(self):
        cases = (
            SONAR.replace("/o:robintrassard ", ""),
            SONAR.replace("/d:sonar.qualitygate.wait=true ", ""),
        )
        for sonar in cases:
            with self.subTest(sonar=sonar):
                with tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    write_repository(root, sonar=sonar)

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
