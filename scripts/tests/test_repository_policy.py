import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parents[2]
CHECKER = REPOSITORY / "scripts" / "check-repository-policy.py"
APP_ID = 4242
CHECKS = (
    ("CI / Gate", APP_ID),
    ("CI / Dependency review", None),
    ("CI / Trusted Qodana", None),
    ("CI / Trusted SonarCloud", None),
    ("CodeQL / CodeQL C#", None),
)
SECRETS = (
    "CI_GATE_APP_ID",
    "CI_GATE_APP_PRIVATE_KEY",
    "QODANA_TOKEN",
    "SONAR_TOKEN",
)


def public_api_fixture():
    return {
        "repository": {
            "status": 200,
            "body": {
                "full_name": "robintra/PerfSentinelHub",
                "visibility": "public",
                "private": False,
                "default_branch": "main",
                "security_and_analysis": {
                    "secret_scanning": {"status": "enabled"},
                    "secret_scanning_push_protection": {"status": "enabled"},
                },
            },
        },
        "rulesets:1": {
            "status": 200,
            "headers": {},
            "body": [
                {"id": 101},
                {"id": 102},
            ],
        },
        "ruleset:101": {
            "status": 200,
            "body": {
                "id": 101,
                "name": "Protect main",
                "target": "branch",
                "enforcement": "active",
                "bypass_actors": [],
                "conditions": {
                    "ref_name": {"include": ["~DEFAULT_BRANCH"], "exclude": []}
                },
                "rules": [
                    {"type": "required_linear_history"},
                    {"type": "required_signatures"},
                    {"type": "non_fast_forward"},
                    {"type": "deletion"},
                    {
                        "type": "pull_request",
                        "parameters": {
                            "allowed_merge_methods": ["squash", "rebase"],
                            "dismiss_stale_reviews_on_push": True,
                            "require_code_owner_review": False,
                            "require_last_push_approval": False,
                            "required_approving_review_count": 0,
                            "required_review_thread_resolution": True,
                        },
                    },
                    {
                        "type": "required_status_checks",
                        "parameters": {
                            "do_not_enforce_on_create": False,
                            "strict_required_status_checks_policy": True,
                            "required_status_checks": [
                                {"context": context, "integration_id": source}
                                for context, source in CHECKS
                            ],
                        },
                    },
                ],
            },
        },
        "ruleset:102": {
            "status": 200,
            "body": {
                "id": 102,
                "name": "Protect release tags",
                "target": "tag",
                "enforcement": "active",
                "bypass_actors": [],
                "conditions": {
                    "ref_name": {"include": ["refs/tags/v*"], "exclude": []}
                },
                "rules": [
                    {"type": "non_fast_forward"},
                    {"type": "deletion"},
                ],
            },
        },
        "environment": {
            "status": 200,
            "body": {
                "name": "hub-release",
                "protection_rules": [
                    {
                        "id": 7,
                        "type": "required_reviewers",
                        "prevent_self_review": False,
                        "reviewers": [
                            {
                                "type": "User",
                                "reviewer": {"id": 11, "login": "robintra"},
                            }
                        ],
                    }
                ],
            },
        },
        "app": {
            "status": 200,
            "body": {"id": APP_ID, "slug": "perf-sentinel-ci-gate"},
        },
    }


def write_root(root: Path, secret="SONAR_TOKEN"):
    workflows = root / ".github" / "workflows"
    workflows.mkdir(parents=True)
    references = "\n".join(
        f"  {name}: ${{{{ secrets.{name} }}}}" for name in (*SECRETS,)
    )
    if secret not in SECRETS:
        references += f"\n  EXTRA: ${{{{ secrets.{secret} }}}}"
    (workflows / "ci.yml").write_text(f"env:\n{references}\n", encoding="utf-8")
    config = root / "config"
    config.mkdir()
    (config / "secret-inventory.json").write_text(
        json.dumps(
            {
                "schema_version": 1,
                "secrets": [
                    {
                        "name": name,
                        "scope": "Trusted workflow.",
                        "purpose": "Authenticate analysis.",
                        "owner": "Maintainers.",
                        "rotation_procedure": "Replace and verify.",
                    }
                    for name in SECRETS
                ],
            }
        ),
        encoding="utf-8",
    )


def run_checker(api, *, secret="SONAR_TOKEN"):
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        write_root(root, secret)
        fixture = root / "api.json"
        fixture.write_text(json.dumps(api), encoding="utf-8")
        return subprocess.run(
            [
                sys.executable,
                str(CHECKER),
                "--repo",
                "robintra/PerfSentinelHub",
                "--root",
                str(root),
                "--fixture",
                str(fixture),
            ],
            cwd=root,
            text=True,
            capture_output=True,
            check=False,
        )


class RepositoryPolicyTests(unittest.TestCase):
    def test_accepts_public_repository_fixture(self):
        result = run_checker(public_api_fixture())

        self.assertEqual(0, result.returncode, result.stderr)

    def test_reports_private_repository_as_visibility_drift(self):
        api = public_api_fixture()
        api["repository"]["body"].update(visibility="private", private=True)

        result = run_checker(api)

        self.assertEqual(1, result.returncode)
        self.assertIn("visibility", result.stderr)

    def test_requires_exact_checks_and_dedicated_app_source(self):
        for mutation in ("missing", "generic-source"):
            with self.subTest(mutation=mutation):
                api = public_api_fixture()
                checks = next(
                    rule["parameters"]["required_status_checks"]
                    for rule in api["ruleset:101"]["body"]["rules"]
                    if rule["type"] == "required_status_checks"
                )
                if mutation == "missing":
                    checks.pop()
                else:
                    checks[0]["integration_id"] = None

                result = run_checker(api)

                self.assertEqual(1, result.returncode)
                self.assertIn("required status checks", result.stderr)

    def test_accepts_omitted_source_only_for_checks_without_an_expected_app(self):
        api = public_api_fixture()
        checks = next(
            rule["parameters"]["required_status_checks"]
            for rule in api["ruleset:101"]["body"]["rules"]
            if rule["type"] == "required_status_checks"
        )
        for check in checks[1:]:
            del check["integration_id"]

        result = run_checker(api)

        self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_force_push_and_branch_deletion(self):
        for forbidden_rule in ("non_fast_forward", "deletion"):
            with self.subTest(forbidden_rule=forbidden_rule):
                api = public_api_fixture()
                rules = api["ruleset:101"]["body"]["rules"]
                rules[:] = [rule for rule in rules if rule["type"] != forbidden_rule]

                result = run_checker(api)

                self.assertEqual(1, result.returncode)
                self.assertIn("default branch", result.stderr)
                self.assertIn(forbidden_rule if forbidden_rule == "non_fast_forward" else "deletion", result.stderr)

    def test_requires_signed_commits_and_rejects_every_bypass_actor(self):
        for mutation in ("unsigned", "administrator-bypass"):
            with self.subTest(mutation=mutation):
                api = public_api_fixture()
                if mutation == "unsigned":
                    rules = api["ruleset:101"]["body"]["rules"]
                    rules[:] = [rule for rule in rules if rule["type"] != "required_signatures"]
                else:
                    api["ruleset:101"]["body"]["bypass_actors"] = [
                        {
                            "actor_id": 5,
                            "actor_type": "RepositoryRole",
                            "bypass_mode": "always",
                        }
                    ]

                result = run_checker(api)

                self.assertEqual(1, result.returncode)
                self.assertIn("bypass" if mutation.endswith("bypass") else "required_signatures", result.stderr)

    def test_requires_public_security_features(self):
        for feature in ("secret_scanning", "secret_scanning_push_protection"):
            with self.subTest(feature=feature):
                api = public_api_fixture()
                api["repository"]["body"]["security_and_analysis"][feature]["status"] = "disabled"

                result = run_checker(api)

                self.assertEqual(1, result.returncode)
                self.assertIn(feature, result.stderr)

    def test_requires_release_environment_reviewer(self):
        for reviewers in ([], [None]):
            with self.subTest(reviewers=reviewers):
                api = public_api_fixture()
                if reviewers:
                    api["environment"]["body"]["protection_rules"][0]["reviewers"] = reviewers
                else:
                    api["environment"]["body"]["protection_rules"] = []

                result = run_checker(api)

                self.assertEqual(1, result.returncode)
                self.assertIn("manual approval", result.stderr)

    def test_requires_protected_v_tags_without_deletion(self):
        for mutation in ("missing-force-protection", "missing-deletion-protection"):
            with self.subTest(mutation=mutation):
                api = public_api_fixture()
                rules = api["ruleset:102"]["body"]["rules"]
                missing = "deletion" if mutation.endswith("deletion-protection") else "non_fast_forward"
                rules[:] = [rule for rule in rules if rule["type"] != missing]

                result = run_checker(api)

                self.assertEqual(1, result.returncode)
                self.assertIn("release tag", result.stderr)

    def test_rejects_undeclared_workflow_secret(self):
        result = run_checker(public_api_fixture(), secret="UNDECLARED_TOKEN")

        self.assertEqual(1, result.returncode)
        self.assertIn("UNDECLARED_TOKEN", result.stderr)

    def test_fails_closed_on_authentication_error(self):
        api = public_api_fixture()
        api["repository"] = {"status": 401, "body": {"message": "Bad credentials"}}

        result = run_checker(api)

        self.assertEqual(1, result.returncode)
        self.assertIn("HTTP 401", result.stderr)

    def test_preserves_known_drift_when_later_private_endpoints_are_forbidden(self):
        api = public_api_fixture()
        api["repository"]["body"].update(
            visibility="private", private=True, security_and_analysis=None
        )
        api["rulesets:1"] = {"status": 403, "body": {"message": "Upgrade required"}}

        result = run_checker(api)

        self.assertEqual(1, result.returncode)
        self.assertIn("visibility", result.stderr)
        self.assertIn("HTTP 403", result.stderr)

    def test_follows_ruleset_pagination_and_fails_if_a_page_is_missing(self):
        for missing_second_page in (False, True):
            with self.subTest(missing_second_page=missing_second_page):
                api = public_api_fixture()
                api["rulesets:1"]["body"] = [{"id": number} for number in range(1000, 1100)]
                for number in range(1000, 1100):
                    api[f"ruleset:{number}"] = {
                        "status": 200,
                        "body": {
                            "id": number,
                            "name": f"Unrelated {number}",
                            "target": "branch",
                            "enforcement": "disabled",
                            "bypass_actors": [],
                            "conditions": {"ref_name": {"include": ["refs/heads/other"], "exclude": []}},
                            "rules": [],
                        },
                    }
                if not missing_second_page:
                    api["rulesets:2"] = {"status": 200, "headers": {}, "body": [{"id": 101}, {"id": 102}]}

                result = run_checker(api)

                if missing_second_page:
                    self.assertEqual(1, result.returncode)
                    self.assertIn("rulesets:2", result.stderr)
                else:
                    self.assertEqual(0, result.returncode, result.stderr)


if __name__ == "__main__":
    unittest.main()
