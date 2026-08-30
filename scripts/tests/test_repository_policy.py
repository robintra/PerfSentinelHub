import json
import importlib.util
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parents[2]
CHECKER = REPOSITORY / "scripts" / "check-repository-policy.py"
CHECKER_SPEC = importlib.util.spec_from_file_location("repository_policy_checker", CHECKER)
CHECKER_MODULE = importlib.util.module_from_spec(CHECKER_SPEC)
CHECKER_SPEC.loader.exec_module(CHECKER_MODULE)
CI_GATE_APP_ID = 4586215
GITHUB_ACTIONS_APP_ID = 15368
CHECKS = (
    ("CI / Gate", CI_GATE_APP_ID),
    ("Dependency review", GITHUB_ACTIONS_APP_ID),
    ("Trusted SonarCloud", GITHUB_ACTIONS_APP_ID),
    ("CodeQL C#", GITHUB_ACTIONS_APP_ID),
)
SECRETS = (
    "CI_GATE_APP_ID",
    "CI_GATE_APP_PRIVATE_KEY",
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
                "allow_squash_merge": True,
                "allow_rebase_merge": True,
                "allow_merge_commit": False,
                "allow_auto_merge": False,
                "delete_branch_on_merge": True,
                "security_and_analysis": {
                    "secret_scanning": {"status": "enabled"},
                    "secret_scanning_push_protection": {"status": "enabled"},
                },
            },
        },
        "rulesets:1": {
            "status": 200,
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
                "bypass_actors": [
                    {"actor_id": 5, "actor_type": "RepositoryRole", "bypass_mode": "always"}
                ],
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
                                "reviewer": {"id": 11, "name": "robintra"},
                            }
                        ],
                    }
                ],
            },
        },
    }


def write_root(root: Path, secret="SONAR_TOKEN", policy_mutator=None):
    workflows = root / ".github" / "workflows"
    workflows.mkdir(parents=True)
    references = "\n".join(
        f"  {name}: ${{{{ secrets.{name} }}}}" for name in (*SECRETS,)
    )
    if secret not in SECRETS:
        references += f"\n  EXTRA: ${{{{ secrets.{secret} }}}}"
    (workflows / "ci.yml").write_text(f"env:\n{references}\n", encoding="utf-8")
    policy = json.loads(
        (REPOSITORY / ".github" / "repository-policy.json").read_text(encoding="utf-8")
    )
    if policy_mutator is not None:
        policy_mutator(policy)
    (root / ".github" / "repository-policy.json").write_text(
        json.dumps(policy), encoding="utf-8"
    )
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


def run_checker(api, *, secret="SONAR_TOKEN", policy_mutator=None):
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        write_root(root, secret, policy_mutator)
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
    def test_normalizes_private_api_security_omission_as_activation_drift(self):
        repository = public_api_fixture()["repository"]["body"]
        repository.update(visibility="private", private=True)
        del repository["security_and_analysis"]

        normalized = CHECKER_MODULE.normalize_repository(repository)

        self.assertIsNone(normalized["security_and_analysis"])

    def test_accepts_public_repository_fixture(self):
        result = run_checker(public_api_fixture())

        self.assertEqual(0, result.returncode, result.stderr)

    def test_reports_private_repository_as_visibility_drift(self):
        api = public_api_fixture()
        api["repository"]["body"].update(visibility="private", private=True)

        result = run_checker(api)

        self.assertEqual(1, result.returncode)
        self.assertIn("visibility", result.stderr)

    def test_policy_schema_is_recursively_closed_and_strictly_typed(self):
        mutations = {
            "top-level unknown": lambda policy: policy.update(unexpected=True),
            "branch unknown": lambda policy: policy["branch_ruleset"].update(unexpected=True),
            "check unknown": lambda policy: policy["branch_ruleset"]["required_status_checks"][0].update(unexpected=True),
            "tag missing": lambda policy: policy["tag_ruleset"].pop("allow_deletions"),
            "environment bool integer": lambda policy: policy["release_environment"].update(minimum_required_reviewers=True),
            "secret non-string": lambda policy: policy["workflow_secrets"].append(None),
            "feature non-list": lambda policy: policy.update(security_features={}),
        }
        for name, mutation in mutations.items():
            with self.subTest(name=name):
                result = run_checker(public_api_fixture(), policy_mutator=mutation)

                self.assertEqual(1, result.returncode)
                self.assertIn("policy schema", result.stderr)

    def test_normalized_api_schema_rejects_unknown_missing_and_wrong_types(self):
        def mutate(api, name):
            if name == "wrapper unknown":
                api["repository"]["headers"] = {}
            elif name == "repository unknown":
                api["repository"]["body"]["unexpected"] = True
            elif name == "repository bool as integer":
                api["repository"]["body"]["allow_squash_merge"] = 1
            elif name == "security missing":
                del api["repository"]["body"]["security_and_analysis"]["secret_scanning"]
            elif name == "summary unknown":
                api["rulesets:1"]["body"][0]["name"] = "unexpected"
            elif name == "ruleset bool id":
                api["ruleset:101"]["body"]["id"] = True
            elif name == "condition unknown":
                api["ruleset:101"]["body"]["conditions"]["unexpected"] = []
            elif name == "simple rule unknown":
                api["ruleset:101"]["body"]["rules"][0]["parameters"] = {}
            elif name == "pull parameter missing":
                rule = next(rule for rule in api["ruleset:101"]["body"]["rules"] if rule["type"] == "pull_request")
                del rule["parameters"]["dismiss_stale_reviews_on_push"]
            elif name == "check context wrong type":
                checks = next(rule["parameters"]["required_status_checks"] for rule in api["ruleset:101"]["body"]["rules"] if rule["type"] == "required_status_checks")
                checks[0]["context"] = False
            elif name == "environment unknown":
                api["environment"]["body"]["unexpected"] = True
            elif name == "protection bool id":
                api["environment"]["body"]["protection_rules"][0]["id"] = True
            elif name == "reviewer unknown":
                api["environment"]["body"]["protection_rules"][0]["reviewers"][0]["reviewer"]["login"] = "unexpected"

        names = (
            "wrapper unknown", "repository unknown", "repository bool as integer",
            "security missing", "summary unknown", "ruleset bool id",
            "condition unknown", "simple rule unknown", "pull parameter missing",
            "check context wrong type", "environment unknown", "protection bool id",
            "reviewer unknown",
        )
        for name in names:
            with self.subTest(name=name):
                api = public_api_fixture()
                mutate(api, name)

                result = run_checker(api)

                self.assertEqual(1, result.returncode)
                self.assertIn("normalized API schema", result.stderr)

    def test_requires_exact_global_merge_settings(self):
        expected = {
            "allow_squash_merge": True,
            "allow_rebase_merge": True,
            "allow_merge_commit": False,
            "allow_auto_merge": False,
            "delete_branch_on_merge": True,
        }
        for field, value in expected.items():
            with self.subTest(field=field):
                api = public_api_fixture()
                api["repository"]["body"][field] = not value

                result = run_checker(api)

                self.assertEqual(1, result.returncode)
                self.assertIn(field, result.stderr)

    def test_accepts_only_the_canonical_pull_request_review_policy(self):
        non_canonical = {
            "extra approval": lambda policy: policy["branch_ruleset"].update(required_approving_review_count=1),
            "unresolved threads": lambda policy: policy["branch_ruleset"].update(required_review_thread_resolution=False),
            "stale reviews kept": lambda policy: policy["branch_ruleset"].update(dismiss_stale_reviews_on_push=False),
            "code owner review": lambda policy: policy["branch_ruleset"].update(require_code_owner_review=True),
            "last push approval": lambda policy: policy["branch_ruleset"].update(require_last_push_approval=True),
            "merge method dropped": lambda policy: policy["branch_ruleset"].update(allowed_merge_methods=["squash"]),
        }

        result = run_checker(public_api_fixture())

        self.assertEqual(0, result.returncode, result.stderr)
        for name, mutation in non_canonical.items():
            with self.subTest(name=name):
                result = run_checker(public_api_fixture(), policy_mutator=mutation)

                self.assertEqual(1, result.returncode)
                self.assertIn("non-canonical", result.stderr)

    def test_rejects_each_pull_request_review_semantic_drift(self):
        expected = {
            "dismiss_stale_reviews_on_push": True,
            "require_code_owner_review": False,
            "require_last_push_approval": False,
        }
        for field, value in expected.items():
            with self.subTest(field=field):
                api = public_api_fixture()
                pull_request = next(
                    rule
                    for rule in api["ruleset:101"]["body"]["rules"]
                    if rule["type"] == "pull_request"
                )
                pull_request["parameters"][field] = not value

                result = run_checker(api)

                self.assertEqual(1, result.returncode)
                self.assertIn(field, result.stderr)

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

    def test_rejects_a_check_whose_publishing_app_is_omitted(self):
        api = public_api_fixture()
        checks = next(
            rule["parameters"]["required_status_checks"]
            for rule in api["ruleset:101"]["body"]["rules"]
            if rule["type"] == "required_status_checks"
        )
        for check in checks[1:]:
            del check["integration_id"]

        result = run_checker(api)

        self.assertEqual(1, result.returncode)
        self.assertIn("normalized API schema", result.stderr)

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

    def test_requires_signed_commits_and_rejects_any_bypass_beyond_the_admin_role(self):
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
                        },
                        {"actor_id": 99, "actor_type": "Team", "bypass_mode": "always"},
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
                self.assertIn(
                    "normalized API schema" if reviewers else "manual approval",
                    result.stderr,
                )

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
                    api["rulesets:2"] = {"status": 200, "body": [{"id": 101}, {"id": 102}]}

                result = run_checker(api)

                if missing_second_page:
                    self.assertEqual(1, result.returncode)
                    self.assertIn("rulesets:2", result.stderr)
                else:
                    self.assertEqual(0, result.returncode, result.stderr)


if __name__ == "__main__":
    unittest.main()
