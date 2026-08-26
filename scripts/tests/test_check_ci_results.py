import json
import re
import subprocess
import sys
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
CHECKER = ROOT / "scripts" / "check-ci-results.py"
WORKFLOW = ROOT / ".github" / "workflows" / "ci.yml"

ALWAYS_REQUIRED = {
    "action-pins",
    "changes",
    "markdown",
    "secret-scan",
}
EXPENSIVE = {
    "dependency-review",
    "helm",
    "native-aot",
    "oci",
    "quality-tests-coverage",
    "sonar",
}
VALIDATION_JOBS = ALWAYS_REQUIRED | EXPENSIVE
ALL_JOBS = {
    "validate-dispatch",
    *VALIDATION_JOBS,
    "aggregate",
    "publish-gate",
}
DEFAULT_NEEDS = object()


def allowed_results(mode: str, decision: str) -> dict[str, set[str]]:
    allowed = {job: {"success"} for job in VALIDATION_JOBS}
    allowed["validate-dispatch"] = {"success"} if mode == "dispatch" else {"skipped"}
    if decision == "docs":
        for job in EXPENSIVE:
            allowed[job] = {"success", "skipped"}
    if mode in {"fork", "dispatch"}:
        allowed["sonar"] = {"skipped"}
    return allowed


def valid_needs(mode: str, decision: str) -> dict[str, dict]:
    payload = {}
    for job, accepted in allowed_results(mode, decision).items():
        result = "success" if "success" in accepted else "skipped"
        outputs = {"decision": decision} if job == "changes" else {}
        if job == "validate-dispatch" and mode == "dispatch":
            outputs = {
                "head_repository": "contributor/PerfSentinelHub",
                "head_sha": "a" * 40,
            }
        payload[job] = {"outputs": outputs, "result": result}
    return payload


def run_checker(
    mode: str = "internal",
    decision: str = "code",
    *,
    needs: object = DEFAULT_NEEDS,
    raw_needs: str | None = None,
) -> subprocess.CompletedProcess[str]:
    if raw_needs is None:
        raw_needs = json.dumps(
            valid_needs(mode, decision) if needs is DEFAULT_NEEDS else needs
        )
    return subprocess.run(
        [
            sys.executable,
            str(CHECKER),
            "--mode",
            mode,
            "--decision",
            decision,
            "--needs-json",
            raw_needs,
        ],
        cwd=ROOT,
        text=True,
        capture_output=True,
        check=False,
    )


def workflow_text() -> str:
    return WORKFLOW.read_text(encoding="utf-8")


def job_body(name: str) -> str:
    match = re.search(
        rf"^  {re.escape(name)}:\n(?P<body>.*?)(?=^  [a-z0-9-]+:\n|\Z)",
        workflow_text(),
        re.MULTILINE | re.DOTALL,
    )
    if match is None:
        raise AssertionError(f"workflow job is missing: {name}")
    return match.group("body")


class CheckCiResultsTests(unittest.TestCase):
    def test_accepts_the_complete_internal_fork_and_dispatch_truth_table(self):
        for mode in ("internal", "fork", "dispatch"):
            for decision in ("code", "docs"):
                with self.subTest(mode=mode, decision=decision):
                    result = run_checker(mode, decision)

                    self.assertEqual(0, result.returncode, result.stderr)
                    self.assertIn("gate passed", result.stdout)

    def test_docs_may_skip_expensive_jobs_but_code_may_not(self):
        for mode in ("internal", "fork", "dispatch"):
            needs = valid_needs(mode, "docs")
            for job in EXPENSIVE:
                needs[job]["result"] = "skipped"
            result = run_checker(mode, "docs", needs=needs)
            with self.subTest(mode=mode, decision="docs"):
                self.assertEqual(0, result.returncode, result.stderr)

        for mode in ("internal", "fork", "dispatch"):
            for job, accepted in allowed_results(mode, "code").items():
                if "success" not in accepted:
                    continue
                needs = valid_needs(mode, "code")
                needs[job]["result"] = "skipped"
                with self.subTest(mode=mode, decision="code", job=job):
                    result = run_checker(mode, "code", needs=needs)
                    self.assertEqual(1, result.returncode)
                    self.assertIn(job, result.stderr)

    def test_trusted_analysis_must_be_skipped_for_forks_and_dispatches(self):
        for mode in ("fork", "dispatch"):
            for job in ("sonar",):
                needs = valid_needs(mode, "code")
                needs[job]["result"] = "success"
                with self.subTest(mode=mode, job=job):
                    result = run_checker(mode, "code", needs=needs)
                    self.assertEqual(1, result.returncode)
                    self.assertIn(job, result.stderr)

    def test_failure_and_cancellation_fail_every_mode_closed(self):
        for mode in ("internal", "fork", "dispatch"):
            for decision in ("code", "docs"):
                for job in allowed_results(mode, decision):
                    for bad_result in ("failure", "cancelled"):
                        needs = valid_needs(mode, decision)
                        needs[job]["result"] = bad_result
                        with self.subTest(
                            mode=mode,
                            decision=decision,
                            job=job,
                            result=bad_result,
                        ):
                            result = run_checker(mode, decision, needs=needs)
                            self.assertEqual(1, result.returncode)
                            self.assertIn(job, result.stderr)

    def test_missing_and_unexpected_jobs_fail_closed(self):
        for mode in ("internal", "fork", "dispatch"):
            expected = valid_needs(mode, "code")
            for missing in tuple(expected):
                needs = valid_needs(mode, "code")
                del needs[missing]
                with self.subTest(mode=mode, missing=missing):
                    result = run_checker(mode, "code", needs=needs)
                    self.assertEqual(1, result.returncode)
                    self.assertIn("missing", result.stderr)

            needs = valid_needs(mode, "code")
            needs["surprise"] = {"outputs": {}, "result": "success"}
            with self.subTest(mode=mode, unexpected="surprise"):
                result = run_checker(mode, "code", needs=needs)
                self.assertEqual(1, result.returncode)
                self.assertIn("unexpected", result.stderr)

    def test_result_variants_and_wrong_json_types_fail_without_traceback(self):
        variants = ("neutral", "timed_out", "action_required", "", None, True, 1, [], {})
        for variant in variants:
            needs = valid_needs("internal", "code")
            needs["markdown"]["result"] = variant
            with self.subTest(result=variant):
                result = run_checker(needs=needs)
                self.assertNotEqual(0, result.returncode)
                self.assertNotIn("Traceback", result.stderr)

        for payload in (None, [], "needs", 1, True):
            with self.subTest(payload=payload):
                result = run_checker(needs=payload)
                self.assertNotEqual(0, result.returncode)
                self.assertNotIn("Traceback", result.stderr)

    def test_duplicate_malformed_missing_and_unexpected_fields_fail_closed(self):
        malformed = (
            "{",
            '{"changes":{"result":"success","outputs":{}},"changes":{"result":"success","outputs":{}}}',
            '{"changes":{"result":"success","result":"failure","outputs":{}}}',
        )
        for raw in malformed:
            with self.subTest(raw=raw):
                result = run_checker(raw_needs=raw)
                self.assertNotEqual(0, result.returncode)
                self.assertNotIn("Traceback", result.stderr)

        mutations = []
        for missing in ("result", "outputs"):
            needs = valid_needs("internal", "code")
            del needs["markdown"][missing]
            mutations.append(needs)
        needs = valid_needs("internal", "code")
        needs["markdown"]["unexpected"] = "value"
        mutations.append(needs)
        for value in (None, [], "outputs", 1, True):
            needs = valid_needs("internal", "code")
            needs["markdown"]["outputs"] = value
            mutations.append(needs)
        needs = valid_needs("internal", "code")
        needs["markdown"]["outputs"] = {"unexpected": "value"}
        mutations.append(needs)

        for index, needs in enumerate(mutations):
            with self.subTest(mutation=index):
                result = run_checker(needs=needs)
                self.assertEqual(1, result.returncode)
                self.assertNotIn("Traceback", result.stderr)

    def test_change_decision_output_must_match_the_checked_decision(self):
        for mode in ("internal", "fork", "dispatch"):
            needs = valid_needs(mode, "code")
            needs["changes"]["outputs"]["decision"] = "docs"
            with self.subTest(mode=mode):
                result = run_checker(mode, "code", needs=needs)
                self.assertEqual(1, result.returncode)
                self.assertIn("decision", result.stderr)

    def test_successful_dispatch_requires_a_canonical_verified_head(self):
        mutations = (
            ("head_sha", "A" * 40),
            ("head_sha", "a" * 39),
            ("head_repository", "missing-owner"),
            ("head_repository", "owner/repo/extra"),
        )
        for field, value in mutations:
            needs = valid_needs("dispatch", "code")
            needs["validate-dispatch"]["outputs"][field] = value
            with self.subTest(field=field, value=value):
                result = run_checker("dispatch", "code", needs=needs)
                self.assertEqual(1, result.returncode)
                self.assertIn(field, result.stderr)


class CiWorkflowTests(unittest.TestCase):
    def test_dependency_review_falls_back_until_the_repository_is_public(self):
        job = job_body("dependency-review")
        self.assertIn(
            "github.event_name == 'pull_request' && github.event.repository.private == false",
            job,
        )
        self.assertIn(
            "github.event_name != 'pull_request' || github.event.repository.private == true",
            job,
        )
        self.assertIn("make audit", job)

    def test_security_and_workflow_tools_use_the_canonical_locked_downloaders(self):
        expected = {
            "download-secret-scanners.sh": ("gitleaks_8.30.1", "trufflehog_3.96.0"),
            "download-workflow-tools.sh": ("actionlint_1.7.12", "zizmor-x86_64", "ruff-x86_64"),
        }
        for filename, markers in expected.items():
            with self.subTest(script=filename):
                path = ROOT / "scripts" / filename
                self.assertTrue(path.is_file(), f"missing locked downloader: {filename}")
                script = path.read_text(encoding="utf-8")
                self.assertTrue(script.startswith("#!/bin/dash\nset -eu\n"))
                for marker in markers:
                    self.assertIn(marker, script)
                if filename == "download-secret-scanners.sh":
                    self.assertIn(
                        "--retry 5 --retry-all-errors --retry-delay 2",
                        script,
                    )

        for name in ("secret-scan", "action-pins"):
            with self.subTest(job=name):
                body = job_body(name)
                self.assertIn("path: trusted", body)
                self.assertIn("path: candidate", body)
                self.assertIn("INTERNAL_PR", body)
                self.assertIn("trusted_root", body)

    def test_workflow_has_only_the_bounded_gate_jobs(self):
        names = set(re.findall(r"^  ([a-z0-9-]+):$", workflow_text(), re.MULTILINE))
        self.assertEqual(ALL_JOBS, names)

    def test_every_job_has_explicit_permissions_timeout_and_hardening_first(self):
        harden_sha = "05e31511f85b41b11d1cf0ef85d0992719546e2c"
        for name in ALL_JOBS:
            with self.subTest(job=name):
                body = job_body(name)
                self.assertRegex(body, r"(?m)^    permissions:\n")
                self.assertRegex(body, r"(?m)^    timeout-minutes: [0-9]+$")
                uses = re.findall(r"(?m)^      - uses: ([^\s]+)", body)
                self.assertTrue(uses)
                self.assertEqual(f"step-security/harden-runner@{harden_sha}", uses[0])
                self.assertRegex(
                    body,
                    r"step-security/harden-runner@[0-9a-f]{40}[\s\S]*?egress-policy: audit",
                )

        for action in re.findall(r"(?m)^\s+- uses: ([^\s#]+)", workflow_text()):
            self.assertRegex(action, r"^[A-Za-z0-9._/-]+@[0-9a-f]{40}$")

    def test_fork_boundary_has_no_target_event_secrets_or_write_token(self):
        text = workflow_text()
        self.assertNotIn("pull_request_target", text)
        self.assertIn("pull_request:", text)
        self.assertIn("workflow_dispatch:", text)
        self.assertIn("pr_number:", text)
        self.assertIn("head_sha:", text)

        secret_jobs = {
            name
            for name in ALL_JOBS
            if "secrets." in job_body(name)
        }
        self.assertEqual({"publish-gate", "sonar"}, secret_jobs)
        self.assertIn("secrets.SONAR_TOKEN", job_body("sonar"))
        for name in ("sonar",):
            body = job_body(name)
            self.assertIn("github.event_name == 'pull_request'", body)
            self.assertIn("github.event.pull_request.head.repo.full_name == github.repository", body)

        write_jobs = {name for name in ALL_JOBS if re.search(r"(?m)^      [a-z-]+: write$", job_body(name))}
        self.assertEqual(set(), write_jobs)
        publisher = job_body("publish-gate")
        self.assertNotRegex(publisher, r"(?m)^      checks: write$")
        self.assertIn("github.event_name == 'workflow_dispatch'", publisher)
        self.assertIn("github.event_name == 'pull_request'", publisher)
        self.assertIn("github.event.pull_request.head.repo.full_name == github.repository", publisher)
        self.assertIn("github.event.repository.default_branch", publisher)
        self.assertNotIn("needs.validate-dispatch.outputs.head_repository", publisher)

    def test_dispatch_verifies_open_pr_and_exact_sha_before_untrusted_checkout(self):
        preflight = job_body("validate-dispatch")
        self.assertNotIn("actions/checkout@", preflight)
        self.assertIn("inputs.pr_number", preflight)
        self.assertIn("inputs.head_sha", preflight)
        self.assertRegex(preflight, r"pull\.state\s*!==\s*['\"]open['\"]")
        self.assertRegex(preflight, r"pull\.head\.sha\s*!==\s*requestedSha")
        self.assertIn("pull.head.repo.full_name", preflight)
        self.assertLess(
            preflight.index("pull.head.repo.full_name ==="),
            preflight.index("core.setOutput('head_sha'"),
        )

        for name in VALIDATION_JOBS - {"changes", "sonar"}:
            body = job_body(name)
            if "actions/checkout@" not in body:
                continue
            with self.subTest(job=name):
                self.assertIn("needs.validate-dispatch.outputs.head_repository", body)
                self.assertIn("needs.validate-dispatch.outputs.head_sha", body)
                self.assertIn("persist-credentials: false", body)

    def test_change_detection_is_fail_closed_and_markdown_and_secrets_always_run(self):
        changes = job_body("changes")
        self.assertIn("previous_filename", changes)
        self.assertIn("pull.changed_files", changes)
        self.assertIn("files.length", changes)
        self.assertGreaterEqual(changes.count("github.rest.pulls.get"), 2)
        self.assertIn("confirmed.head.sha", changes)
        self.assertLess(changes.index("github.paginate"), changes.index("const confirmedResponse"))
        self.assertIn("decision", changes)
        self.assertIn("docs", changes)
        self.assertIn("code", changes)
        self.assertIn("README.md", changes)
        self.assertIn("docs/", changes)

        for name in ("markdown", "secret-scan"):
            body = job_body(name)
            self.assertRegex(body, r"(?m)^    if: .*always\(\)")
            self.assertNotIn("needs.changes.outputs.decision == 'code'", body)

    def test_aggregate_and_api_gate_are_fail_closed_and_distinct(self):
        aggregate = job_body("aggregate")
        self.assertIn("CI / Internal validation", aggregate)
        self.assertIn("CI / Fork validation", aggregate)
        self.assertRegex(aggregate, r"(?m)^    if: .*always\(\)")
        self.assertIn("toJSON(needs)", aggregate)
        self.assertIn("--mode", aggregate)
        self.assertIn("internal", aggregate)
        self.assertIn("fork", aggregate)
        for dependency in VALIDATION_JOBS:
            self.assertIn(dependency, aggregate)

        exact_needs = {"validate-dispatch", *VALIDATION_JOBS}
        for name in ("aggregate", "publish-gate"):
            match = re.search(r"(?m)^    needs: \[([^]]+)\]$", job_body(name))
            self.assertIsNotNone(match)
            self.assertEqual(exact_needs, {item.strip() for item in match.group(1).split(",")})

        publisher = job_body("publish-gate")
        self.assertRegex(publisher, r"(?m)^    if: .*always\(\)")
        self.assertIn("toJSON(needs)", publisher)
        self.assertIn("GATE_MODE", publisher)
        self.assertIn("'dispatch'", publisher)
        self.assertIn("'internal'", publisher)
        self.assertIn('--mode "$GATE_MODE"', publisher)
        self.assertIn("github.rest.checks.create", publisher)
        self.assertIn("name: 'CI / Gate'", publisher)
        self.assertIn("conclusion", publisher)
        self.assertIn("steps.gate.outcome", publisher)
        self.assertIn("ref: ${{ github.event.repository.default_branch }}", publisher)

    def test_required_gate_is_only_created_with_the_dedicated_app_token(self):
        text = workflow_text()
        automatic_names = re.findall(r"(?m)^    name:\s+(.+)$", text)
        self.assertNotIn("CI / Gate", automatic_names)
        self.assertEqual(1, text.count("name: 'CI / Gate'"))

        publisher = job_body("publish-gate")
        app_sha = "bcd2ba49218906704ab6c1aa796996da409d3eb1"
        self.assertIn(f"actions/create-github-app-token@{app_sha}", publisher)
        self.assertIn("secrets.CI_GATE_APP_ID", publisher)
        self.assertIn("secrets.CI_GATE_APP_PRIVATE_KEY", publisher)
        self.assertIn("permission-checks: write", publisher)
        self.assertIn("repositories: ${{ github.event.repository.name }}", publisher)
        self.assertIn("github-token: ${{ steps.app-token.outputs.token }}", publisher)
        self.assertNotIn("github-token: ${{ github.token }}", publisher)
        self.assertNotRegex(publisher, r"(?m)^      checks: write$")

        activation = (ROOT / "docs" / "ci-gate.md").read_text(encoding="utf-8")
        self.assertIn("expected source", activation)
        self.assertIn("dedicated GitHub App", activation)
        self.assertIn("CI_GATE_APP_ID", activation)
        self.assertIn("CI_GATE_APP_PRIVATE_KEY", activation)

    def test_quality_job_names_and_runs_every_required_suite(self):
        quality = job_body("quality-tests-coverage")
        for suite in (
            "health",
            "import",
            "polling",
            "overload",
            "backpressure",
            "response-streaming",
            "SQLite retention",
            "bounded-storage",
        ):
            self.assertIn(suite, quality)
        # The coverage run carries no --filter allowlist any more: it runs the
        # whole solution, so a suite added later is covered by construction
        # instead of by remembering to extend a list of class names here.
        self.assertIn("dotnet test PerfSentinelHub.sln", quality)
        self.assertNotIn("--filter", quality)

        self.assertIn("sonar.qualitygate.wait=true", job_body("sonar"))

    def test_untrusted_fork_jobs_cannot_save_default_branch_caches(self):
        internal_cache = "cache: ${{ github.event_name == 'pull_request' && github.event.pull_request.head.repo.full_name == github.repository }}"
        for name in ("quality-tests-coverage", "native-aot"):
            with self.subTest(job=name):
                self.assertIn(internal_cache, job_body(name))
        self.assertNotIn("cache: true", job_body("dependency-review"))
        self.assertIn("cache: true", job_body("sonar"))

    def test_native_smoke_supplies_the_minimum_valid_source_configuration(self):
        native = job_body("native-aot")
        for variable in (
            "Hub__Sources__0__Id",
            "Hub__Sources__0__Name",
            "Hub__Sources__0__Environment",
            "Hub__Sources__0__BaseUrl",
        ):
            self.assertIn(variable, native)


if __name__ == "__main__":
    unittest.main()
