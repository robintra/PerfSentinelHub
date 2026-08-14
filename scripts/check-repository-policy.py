#!/usr/bin/env python3
"""Report GitHub repository-policy drift without changing remote settings."""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path
from urllib.parse import quote


SECRET_REFERENCE = re.compile(r"\$\{\{\s*secrets\.([A-Z][A-Z0-9_]*)\s*}}")
# "secrets:S6338" is a Sonar rule key, not a GitHub secret reference.
SECRET_TOKEN = re.compile(r"(?<![a-z0-9_])secrets(?![a-z0-9_])(?!:S\d)", re.IGNORECASE)
LIST = "list"
NULLABLE = "nullable"


POLICY_SCHEMA = {
    "schema_version": int,
    "repository": str,
    "visibility": str,
    "default_branch": str,
    "repository_settings": {
        "allow_squash_merge": bool,
        "allow_rebase_merge": bool,
        "allow_merge_commit": bool,
        "allow_auto_merge": bool,
        "delete_branch_on_merge": bool,
    },
    "security_features": (LIST, str),
    "branch_ruleset": {
        "ref_include": str,
        "required_approving_review_count": int,
        "required_review_thread_resolution": bool,
        "dismiss_stale_reviews_on_push": bool,
        "require_code_owner_review": bool,
        "require_last_push_approval": bool,
        "strict_required_status_checks_policy": bool,
        "do_not_enforce_on_create": bool,
        "allowed_merge_methods": (LIST, str),
        "required_status_checks": (
            LIST,
            {"context": str, "app_id": int},
        ),
        "require_linear_history": bool,
        "require_signed_commits": bool,
        "allow_force_pushes": bool,
        "allow_deletions": bool,
        "bypass_actors": (
            LIST,
            {"actor_id": (NULLABLE, int), "actor_type": str, "bypass_mode": str},
        ),
        "emergency_bypass_record": (NULLABLE, str),
    },
    "tag_ruleset": {
        "ref_include": str,
        "allow_force_updates": bool,
        "allow_deletions": bool,
        "bypass_actors": (
            LIST,
            {"actor_id": (NULLABLE, int), "actor_type": str, "bypass_mode": str},
        ),
    },
    "release_environment": {
        "name": str,
        "minimum_required_reviewers": int,
        "prevent_self_review": bool,
    },
    "workflow_secrets": (LIST, str),
}

REPOSITORY_SCHEMA = {
    "full_name": str,
    "visibility": str,
    "private": bool,
    "default_branch": str,
    "allow_squash_merge": bool,
    "allow_rebase_merge": bool,
    "allow_merge_commit": bool,
    "allow_auto_merge": bool,
    "delete_branch_on_merge": bool,
    "security_and_analysis": (
        NULLABLE,
        {
            "secret_scanning": {"status": str},
            "secret_scanning_push_protection": {"status": str},
        },
    ),
}
RULESET_SUMMARIES_SCHEMA = (LIST, {"id": int})
BYPASS_ACTORS_SCHEMA = (
    LIST,
    {"actor_id": (NULLABLE, int), "actor_type": str, "bypass_mode": str},
)
CONDITIONS_SCHEMA = {
    "ref_name": {"include": (LIST, str), "exclude": (LIST, str)}
}
PULL_REQUEST_PARAMETERS_SCHEMA = {
    "allowed_merge_methods": (LIST, str),
    "dismiss_stale_reviews_on_push": bool,
    "require_code_owner_review": bool,
    "require_last_push_approval": bool,
    "required_approving_review_count": int,
    "required_review_thread_resolution": bool,
}
STATUS_PARAMETERS_SCHEMA = {
    "do_not_enforce_on_create": bool,
    "strict_required_status_checks_policy": bool,
    "required_status_checks": (
        LIST,
        {"context": str, "integration_id": (NULLABLE, int)},
    ),
}
ENVIRONMENT_SCHEMA = {
    "name": str,
    "protection_rules": (
        LIST,
        {
            "id": int,
            "type": str,
            "prevent_self_review": bool,
            "reviewers": (
                LIST,
                {"type": str, "reviewer": {"id": int, "name": str}},
            ),
        },
    ),
}
# The dedicated gate App, and GitHub Actions itself. Both are stable numeric App ids.
CI_GATE_APP_ID = 4586215
GITHUB_ACTIONS_APP_ID = 15368


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def load_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)


class ApiError(RuntimeError):
    pass


def validate_object_shape(value, schema, label: str) -> None:
    if not isinstance(value, dict) or set(value) != set(schema):
        raise ApiError(f"{label}: expected exact object fields {sorted(schema)}")
    for key, child_schema in schema.items():
        validate_shape(value[key], child_schema, f"{label}.{key}")


def validate_list_shape(value, item_schema, label: str) -> None:
    if not isinstance(value, list):
        raise ApiError(f"{label}: expected array")
    for index, item in enumerate(value):
        validate_shape(item, item_schema, f"{label}[{index}]")


def validate_shape(value, schema, label: str) -> None:
    if isinstance(schema, dict):
        validate_object_shape(value, schema, label)
    elif isinstance(schema, tuple) and schema[0] == LIST:
        validate_list_shape(value, schema[1], label)
    elif isinstance(schema, tuple) and schema[0] == NULLABLE:
        if value is not None:
            validate_shape(value, schema[1], label)
    elif type(value) is not schema:
        raise ApiError(f"{label}: expected {schema.__name__}")


def validate_policy_schema(policy) -> None:
    try:
        validate_shape(policy, POLICY_SCHEMA, "repository policy schema")
    except ApiError as error:
        raise ApiError(str(error)) from error
    branch = policy["branch_ruleset"]
    checks = branch["required_status_checks"]
    settings = policy["repository_settings"]
    expected_settings = {
        "allow_squash_merge": True,
        "allow_rebase_merge": True,
        "allow_merge_commit": False,
        "allow_auto_merge": False,
        "delete_branch_on_merge": True,
    }
    # Every check is pinned to the numeric id of the App that may publish it. Ids are immutable,
    # slugs can be renamed, and a private App cannot be resolved from its slug at all.
    expected_checks = {
        ("CI / Gate", CI_GATE_APP_ID),
        ("Dependency review", GITHUB_ACTIONS_APP_ID),
        ("Trusted SonarCloud", GITHUB_ACTIONS_APP_ID),
        ("CodeQL C#", GITHUB_ACTIONS_APP_ID),
    }
    expected_secrets = {
        "CI_GATE_APP_ID",
        "CI_GATE_APP_PRIVATE_KEY",
        "SONAR_TOKEN",
    }
    if (
        not canonical_repository_policy(policy, settings, expected_settings)
        or not canonical_branch_policy(branch, checks, expected_checks)
        or not canonical_release_policy(policy, expected_secrets)
    ):
        raise ApiError("repository policy schema contains a non-canonical value")


def canonical_repository_policy(policy, settings, expected_settings) -> bool:
    return (
        policy["schema_version"] == 1
        and policy["visibility"] == "public"
        and policy["default_branch"] == "main"
        and settings == expected_settings
        and policy["security_features"] == ["secret_scanning", "secret_scanning_push_protection"]
    )


def canonical_branch_policy(branch, checks, expected_checks) -> bool:
    return (
        branch["ref_include"] == "~DEFAULT_BRANCH"
        and branch["required_approving_review_count"] == 0
        and branch["required_review_thread_resolution"] is True
        and branch["dismiss_stale_reviews_on_push"] is True
        and branch["require_code_owner_review"] is False
        and branch["require_last_push_approval"] is False
        and branch["strict_required_status_checks_policy"] is True
        and branch["do_not_enforce_on_create"] is False
        and sorted(branch["allowed_merge_methods"]) == ["rebase", "squash"]
        and len(branch["allowed_merge_methods"]) == len(set(branch["allowed_merge_methods"]))
        and bool(checks)
        and len({check["context"] for check in checks}) == len(checks)
        and {(check["context"], check["app_id"]) for check in checks} == expected_checks
        and branch["require_linear_history"] is True
        and branch["require_signed_commits"] is True
        and branch["allow_force_pushes"] is False
        and branch["allow_deletions"] is False
        and branch["bypass_actors"] == []
        and branch["emergency_bypass_record"] is None
    )


def canonical_release_policy(policy, expected_secrets) -> bool:
    return (
        policy["tag_ruleset"]
        == {
            "ref_include": "refs/tags/v*",
            "allow_force_updates": False,
            "allow_deletions": False,
            "bypass_actors": [],
        }
        and policy["release_environment"]["name"] == "hub-release"
        and policy["release_environment"]["minimum_required_reviewers"] >= 1
        and policy["release_environment"]["prevent_self_review"] is False
        and len(policy["workflow_secrets"]) == len(set(policy["workflow_secrets"]))
        and set(policy["workflow_secrets"]) == expected_secrets
    )


def selected(value, keys, label: str):
    if not isinstance(value, dict):
        raise ApiError(f"{label}: expected object")
    missing = set(keys) - set(value)
    if missing:
        raise ApiError(f"{label}: missing fields {sorted(missing)}")
    return {key: value[key] for key in keys}


def normalize_repository(value):
    fields = tuple(REPOSITORY_SCHEMA)
    if (
        isinstance(value, dict)
        and "security_and_analysis" not in value
        and value.get("visibility") == "private"
        and value.get("private") is True
    ):
        value = {**value, "security_and_analysis": None}
    result = selected(value, fields, "repository")
    security = result["security_and_analysis"]
    if security is not None:
        security = selected(
            security,
            ("secret_scanning", "secret_scanning_push_protection"),
            "repository.security_and_analysis",
        )
        for feature in tuple(security):
            security[feature] = selected(
                security[feature], ("status",), f"repository.{feature}"
            )
        result["security_and_analysis"] = security
    return result


def normalize_bypass_actor(value):
    return selected(value, ("actor_id", "actor_type", "bypass_mode"), "bypass actor")


def normalize_rule(value):
    if not isinstance(value, dict) or not isinstance(value.get("type"), str):
        raise ApiError("rule: missing string type")
    kind = value["type"]
    if kind in {
        "required_linear_history",
        "required_signatures",
        "non_fast_forward",
        "deletion",
    }:
        return {"type": kind}
    if kind == "pull_request":
        rule = selected(value, ("type", "parameters"), "pull_request rule")
        rule["parameters"] = selected(
            rule["parameters"], PULL_REQUEST_PARAMETERS_SCHEMA, "pull_request parameters"
        )
        return rule
    if kind == "required_status_checks":
        return normalize_status_check_rule(value)
    raise ApiError(f"unsupported normalized rule type {kind!r}")


def normalize_status_check_rule(value):
    rule = selected(value, ("type", "parameters"), "required_status_checks rule")
    parameters = selected(
        rule["parameters"], STATUS_PARAMETERS_SCHEMA, "status check parameters"
    )
    checks = parameters["required_status_checks"]
    if not isinstance(checks, list):
        raise ApiError("status checks: expected array")
    parameters["required_status_checks"] = [
        {
            "context": selected(check, ("context",), "status check")["context"],
            "integration_id": check.get("integration_id") if isinstance(check, dict) else None,
        }
        for check in checks
    ]
    rule["parameters"] = parameters
    return rule


def normalize_ruleset(value):
    result = selected(
        value,
        (
            "id",
            "name",
            "target",
            "enforcement",
            "bypass_actors",
            "conditions",
            "rules",
        ),
        "ruleset",
    )
    actors = result["bypass_actors"]
    rules = result["rules"]
    conditions = result["conditions"]
    if not isinstance(actors, list) or not isinstance(rules, list):
        raise ApiError("ruleset actors and rules must be arrays")
    conditions = selected(conditions, ("ref_name",), "ruleset conditions")
    conditions["ref_name"] = selected(
        conditions["ref_name"], ("include", "exclude"), "ruleset ref_name"
    )
    result["bypass_actors"] = [normalize_bypass_actor(actor) for actor in actors]
    result["conditions"] = conditions
    result["rules"] = [normalize_rule(rule) for rule in rules]
    return result


def validate_rule(value, label: str):
    if not isinstance(value, dict) or not isinstance(value.get("type"), str):
        raise ApiError(f"{label}: missing string type")
    kind = value["type"]
    if kind in {
        "required_linear_history",
        "required_signatures",
        "non_fast_forward",
        "deletion",
    }:
        validate_shape(value, {"type": str}, label)
    elif kind == "pull_request":
        validate_shape(
            value,
            {"type": str, "parameters": PULL_REQUEST_PARAMETERS_SCHEMA},
            label,
        )
    elif kind == "required_status_checks":
        validate_shape(
            value,
            {"type": str, "parameters": STATUS_PARAMETERS_SCHEMA},
            label,
        )
    else:
        raise ApiError(f"{label}: unsupported rule type {kind!r}")


def validate_ruleset(value, label: str):
    schema = {
        "id": int,
        "name": str,
        "target": str,
        "enforcement": str,
        "bypass_actors": BYPASS_ACTORS_SCHEMA,
        "conditions": CONDITIONS_SCHEMA,
        "rules": (LIST, dict),
    }
    validate_shape(value, schema, label)
    for index, rule in enumerate(value["rules"]):
        validate_rule(rule, f"{label}.rules[{index}]")


def normalize_environment(value):
    result = selected(value, ("name", "protection_rules"), "environment")
    rules = result["protection_rules"]
    if not isinstance(rules, list):
        raise ApiError("environment protection_rules must be an array")
    normalized = []
    for rule in rules:
        rule = selected(
            rule,
            ("id", "type", "prevent_self_review", "reviewers"),
            "environment protection rule",
        )
        if rule["type"] != "required_reviewers" or not isinstance(rule["reviewers"], list):
            raise ApiError("unsupported environment protection rule")
        reviewers = []
        for entry in rule["reviewers"]:
            entry = selected(entry, ("type", "reviewer"), "environment reviewer")
            reviewer = selected(entry["reviewer"], ("id",), "reviewer identity")
            if entry["type"] == "User" and isinstance(entry["reviewer"], dict):
                reviewer["name"] = entry["reviewer"].get("login")
            elif entry["type"] == "Team" and isinstance(entry["reviewer"], dict):
                reviewer["name"] = entry["reviewer"].get("slug")
            else:
                reviewer["name"] = None
            reviewers.append({"type": entry["type"], "reviewer": reviewer})
        rule["reviewers"] = reviewers
        normalized.append(rule)
    result["protection_rules"] = normalized
    return result


FIXTURE_KEY = re.compile(r"^(?:repository|environment|rulesets:[1-9][0-9]*|ruleset:[1-9][0-9]*)$")


def validate_fixture(fixture: object) -> None:
    if not isinstance(fixture, dict):
        raise ApiError("normalized API schema fixture must be an object")
    for key, response in fixture.items():
        if FIXTURE_KEY.fullmatch(key) is None:
            raise ApiError(f"normalized API schema fixture key is invalid: {key}")
        if not isinstance(response, dict) or set(response) != {"status", "body"}:
            raise ApiError(f"normalized API schema response {key} requires exactly status and body")
        if type(response["status"]) is not int:
            raise ApiError(f"normalized API schema response {key}.status must be int")


class GitHubApi:
    def __init__(self, repository: str, fixture: Path | None):
        self.repository = repository
        self.fixture = load_json(fixture) if fixture else None
        if self.fixture is not None:
            validate_fixture(self.fixture)

    def _fixture_key(self, endpoint: str, page: int | None) -> str:
        if endpoint == f"repos/{self.repository}":
            return "repository"
        if endpoint == f"repos/{self.repository}/rulesets":
            if page is None:
                raise ApiError("the ruleset listing requires a page number")
            return f"rulesets:{page}"
        prefix = f"repos/{self.repository}/rulesets/"
        if endpoint.startswith(prefix):
            return f"ruleset:{endpoint.removeprefix(prefix)}"
        environment = quote("hub-release", safe="")
        if endpoint == f"repos/{self.repository}/environments/{environment}":
            return "environment"
        raise ApiError(f"unrecognized fixture endpoint: {endpoint}")

    @staticmethod
    def _live(endpoint: str, page: int | None):
        command = [
            "gh",
            "api",
            "--method",
            "GET",
            "-H",
            "Accept: application/vnd.github+json",
            "-H",
            "X-GitHub-Api-Version: 2026-03-10",
            endpoint,
        ]
        if page is not None:
            command.extend(("-f", "per_page=100", "-f", f"page={page}"))
        result = subprocess.run(command, text=True, capture_output=True, check=False)
        if result.returncode:
            detail = result.stderr.strip().splitlines()[0] if result.stderr.strip() else "request failed"
            raise ApiError(f"GitHub API GET {endpoint} failed: {detail}")
        try:
            return json.loads(result.stdout, object_pairs_hook=unique_object)
        except (ValueError, TypeError) as error:
            raise ApiError(f"GitHub API GET {endpoint} returned invalid JSON: {error}") from error

    def _fetch(self, endpoint, *, page=None, schema=None, validator=None, normalizer=lambda value: value):
        key = self._fixture_key(endpoint, page)
        fixture = self.fixture
        if fixture is not None:
            response = fixture.get(key)
            if response is None:
                raise ApiError(f"normalized API schema fixture response is missing: {key}")
            if response["status"] != 200:
                raise ApiError(f"GitHub API {key} returned HTTP {response['status']}")
            value = response["body"]
        else:
            try:
                value = normalizer(self._live(endpoint, page))
            except ApiError as error:
                if str(error).startswith("GitHub API GET"):
                    raise
                raise ApiError(f"normalized API schema {key}: {error}") from error
        try:
            if validator is not None:
                validator(value, f"normalized API schema {key}")
            else:
                validate_shape(value, schema, f"normalized API schema {key}")
        except ApiError as error:
            raise ApiError(str(error)) from error
        return value

    def repository_data(self):
        return self._fetch(
            f"repos/{self.repository}",
            schema=REPOSITORY_SCHEMA,
            normalizer=normalize_repository,
        )

    def rulesets(self):
        result = []
        for page in range(1, 101):
            payload = self._fetch(
                f"repos/{self.repository}/rulesets",
                page=page,
                schema=RULESET_SUMMARIES_SCHEMA,
                normalizer=lambda value: [
                    selected(item, ("id",), "ruleset summary")
                    for item in value
                ] if isinstance(value, list) else value,
            )
            result.extend(payload)
            if len(payload) < 100:
                return result
        raise ApiError("ruleset pagination exceeded 100 pages")

    def ruleset(self, identifier: int):
        return self._fetch(
            f"repos/{self.repository}/rulesets/{identifier}",
            validator=validate_ruleset,
            normalizer=normalize_ruleset,
        )

    def environment(self, name: str):
        return self._fetch(
            f"repos/{self.repository}/environments/{quote(name, safe='')}",
            schema=ENVIRONMENT_SCHEMA,
            normalizer=normalize_environment,
        )


def require_object(value, label: str):
    if not isinstance(value, dict):
        raise ApiError(f"{label} is missing or not an object")
    return value


def rule_map(ruleset, label: str):
    rules = ruleset.get("rules")
    if not isinstance(rules, list) or any(not isinstance(rule, dict) for rule in rules):
        raise ApiError(f"{label} rules are missing or ambiguous")
    result = {}
    for rule in rules:
        kind = rule.get("type")
        if not isinstance(kind, str) or kind in result:
            raise ApiError(f"{label} contains an ambiguous rule type")
        result[kind] = rule
    return result


def active_ruleset(rulesets, target: str, include: str, label: str):
    matches = []
    for ruleset in rulesets:
        if not isinstance(ruleset, dict):
            raise ApiError("ruleset response contains a non-object")
        conditions = ruleset.get("conditions")
        ref_name = conditions.get("ref_name") if isinstance(conditions, dict) else None
        included = ref_name.get("include") if isinstance(ref_name, dict) else None
        excluded = ref_name.get("exclude") if isinstance(ref_name, dict) else None
        if (
            ruleset.get("target") == target
            and ruleset.get("enforcement") == "active"
            and included == [include]
            and excluded == []
        ):
            matches.append(ruleset)
    if len(matches) != 1:
        raise ApiError(f"exactly one active {label} ruleset is required")
    return matches[0]


def workflow_secrets(root: Path):
    names = set()
    workflows = root / ".github" / "workflows"
    if not workflows.is_dir():
        raise ApiError(".github/workflows is missing")
    for path in sorted((*workflows.glob("*.yml"), *workflows.glob("*.yaml"))):
        try:
            text = path.read_text(encoding="utf-8")
        except (OSError, UnicodeError) as error:
            raise ApiError(f"unable to read {path}: {error}") from error
        def collect(match) -> str:
            names.add(match.group(1))
            return ""

        stripped = SECRET_REFERENCE.sub(collect, text)
        if SECRET_TOKEN.search(stripped):
            raise ApiError(f"{path}: non-canonical workflow secret reference")
    return names


def inventory_secrets(root: Path):
    try:
        payload = load_json(root / "config" / "secret-inventory.json")
        entries = payload["secrets"]
    except (OSError, ValueError, KeyError, TypeError) as error:
        raise ApiError(f"invalid secret inventory: {error}") from error
    if not isinstance(entries, list):
        raise ApiError("secret inventory entries are missing")
    names = []
    for entry in entries:
        if not isinstance(entry, dict) or not isinstance(entry.get("name"), str):
            raise ApiError("secret inventory entry is ambiguous")
        names.append(entry["name"])
    if len(names) != len(set(names)):
        raise ApiError("secret inventory contains duplicate names")
    return set(names)


def secret_errors(root: Path, policy) -> list[str]:
    expected_secrets = set(policy["workflow_secrets"])
    inventoried = inventory_secrets(root)
    referenced = workflow_secrets(root)
    errors = [
        f"workflow secret {name} is absent from the inventory"
        for name in sorted(referenced - inventoried)
    ]
    if inventoried != expected_secrets or referenced != expected_secrets:
        errors.append(
            "workflow secret references must exactly match policy: "
            f"expected={sorted(expected_secrets)}, inventoried={sorted(inventoried)}, "
            f"referenced={sorted(referenced)}"
        )
    return errors


def repository_errors(repository: str, policy, api: GitHubApi) -> list[str]:
    repository_data = api.repository_data()
    errors = []
    if repository_data.get("full_name") != repository:
        errors.append("repository identity drift")
    if repository_data.get("visibility") != policy["visibility"] or repository_data.get("private") is not False:
        errors.append("repository visibility must be public")
    if repository_data.get("default_branch") != policy["default_branch"]:
        errors.append("default branch drift")
    for field, expected in policy["repository_settings"].items():
        if repository_data[field] is not expected:
            errors.append(f"repository setting {field} must be {str(expected).lower()}")
    security = repository_data.get("security_and_analysis")
    if not isinstance(security, dict):
        errors.append("public security_and_analysis fields are missing")
        return errors
    for feature in policy["security_features"]:
        state = security.get(feature)
        if not isinstance(state, dict) or state.get("status") != "enabled":
            errors.append(f"public security feature {feature} must be enabled")
    return errors


def detailed_rulesets(api: GitHubApi, summaries) -> list:
    detailed = []
    seen = set()
    for summary in summaries:
        if not isinstance(summary, dict) or type(summary.get("id")) is not int:
            raise ApiError("ruleset summary lacks an integer id")
        identifier = summary["id"]
        if identifier in seen:
            raise ApiError(f"duplicate ruleset id {identifier}")
        seen.add(identifier)
        detail = api.ruleset(identifier)
        if detail["id"] != identifier:
            raise ApiError(f"normalized API schema ruleset {identifier}: id mismatch")
        detailed.append(detail)
    return detailed


def pull_request_errors(branch_rules, branch_policy) -> list[str]:
    pull_request = branch_rules.get("pull_request")
    parameters = pull_request.get("parameters") if isinstance(pull_request, dict) else None
    if not isinstance(parameters, dict):
        return ["pull_request rule is required for everyone"]
    errors = []
    if parameters.get("required_approving_review_count") != branch_policy["required_approving_review_count"]:
        errors.append("pull_request approval count drift")
    if parameters.get("required_review_thread_resolution") is not branch_policy["required_review_thread_resolution"]:
        errors.append("pull_request conversations must be resolved")
    for field in (
        "dismiss_stale_reviews_on_push",
        "require_code_owner_review",
        "require_last_push_approval",
    ):
        if parameters[field] is not branch_policy[field]:
            errors.append(f"pull_request {field} drift")
    if sorted(parameters.get("allowed_merge_methods", [])) != sorted(branch_policy["allowed_merge_methods"]):
        errors.append("pull_request merge methods drift")
    return errors


def normalized_status_checks(status_parameters):
    actual_checks = status_parameters.get("required_status_checks") if isinstance(status_parameters, dict) else None
    if not isinstance(actual_checks, list):
        return None
    normalized = []
    for item in actual_checks:
        if (
            not isinstance(item, dict)
            or not isinstance(item.get("context"), str)
            or set(item) - {"context", "integration_id"}
            or (
                item.get("integration_id") is not None
                and type(item.get("integration_id")) is not int
            )
        ):
            return None
        normalized.append({"context": item["context"], "integration_id": item.get("integration_id")})
    return normalized


def status_check_errors(branch_rules, branch_policy) -> list[str]:
    expected_checks = [
        {"context": check.get("context"), "integration_id": check.get("app_id")}
        for check in branch_policy["required_status_checks"]
    ]
    status_rule = branch_rules.get("required_status_checks")
    status_parameters = status_rule.get("parameters") if isinstance(status_rule, dict) else None
    normalized_checks = normalized_status_checks(status_parameters)
    if (
        status_parameters is None
        or normalized_checks is None
        or status_parameters.get("strict_required_status_checks_policy") is not True
        or status_parameters.get("strict_required_status_checks_policy")
        is not branch_policy["strict_required_status_checks_policy"]
        or status_parameters.get("do_not_enforce_on_create")
        is not branch_policy["do_not_enforce_on_create"]
        or sorted(normalized_checks, key=lambda item: item["context"])
        != sorted(expected_checks, key=lambda item: item["context"])
    ):
        return ["required status checks differ or lack the expected App source"]
    return []


def branch_errors(detailed, policy) -> list[str]:
    branch_policy = require_object(policy["branch_ruleset"], "branch policy")
    branch = active_ruleset(detailed, "branch", branch_policy["ref_include"], "default branch")
    branch_rules = rule_map(branch, "default branch ruleset")
    expected_branch_rules = {
        "required_linear_history",
        "required_signatures",
        "non_fast_forward",
        "deletion",
        "pull_request",
        "required_status_checks",
    }
    errors = []
    if set(branch_rules) != expected_branch_rules:
        errors.append("default branch rule set differs from policy")
    if branch.get("bypass_actors") != branch_policy["bypass_actors"] or branch_policy.get("emergency_bypass_record") is not None:
        errors.append("administrator or actor bypass is forbidden without exception")
    for kind, enabled in (
        ("required_linear_history", branch_policy["require_linear_history"]),
        ("required_signatures", branch_policy["require_signed_commits"]),
        ("non_fast_forward", not branch_policy["allow_force_pushes"]),
    ):
        if enabled and kind not in branch_rules:
            errors.append(f"default branch requires {kind}")
    if not branch_policy["allow_deletions"] and "deletion" not in branch_rules:
        errors.append("default branch requires deletion protection")
    errors.extend(pull_request_errors(branch_rules, branch_policy))
    errors.extend(status_check_errors(branch_rules, branch_policy))
    return errors


def tag_errors(detailed, policy) -> list[str]:
    tag_policy = require_object(policy["tag_ruleset"], "tag policy")
    tag = active_ruleset(detailed, "tag", tag_policy["ref_include"], "release tag")
    tag_rules = rule_map(tag, "release tag ruleset")
    errors = []
    if set(tag_rules) != {"non_fast_forward", "deletion"}:
        errors.append("release tag rule set differs from policy")
    if tag.get("bypass_actors") != tag_policy["bypass_actors"]:
        errors.append("release tag bypass is forbidden")
    if not tag_policy["allow_force_updates"] and "non_fast_forward" not in tag_rules:
        errors.append("release tag force updates must be forbidden")
    if not tag_policy["allow_deletions"] and "deletion" not in tag_rules:
        errors.append("release tag deletion protection is required")
    return errors


def environment_errors(policy, api: GitHubApi) -> list[str]:
    environment_policy = require_object(policy["release_environment"], "release environment policy")
    environment_name = environment_policy["name"]
    environment = api.environment(environment_name)
    rules = environment.get("protection_rules")
    reviewers = [rule for rule in rules if isinstance(rule, dict) and rule.get("type") == "required_reviewers"] if isinstance(rules, list) else []
    if len(reviewers) != 1:
        return [f"{environment_name} manual approval is required"]
    reviewer_entries = reviewers[0].get("reviewers")
    valid_reviewers = (
        isinstance(reviewer_entries, list)
        and len(reviewer_entries) >= environment_policy["minimum_required_reviewers"]
        and all(
            isinstance(entry, dict)
            and entry.get("type") in {"User", "Team"}
            and isinstance(entry.get("reviewer"), dict)
            and type(entry["reviewer"].get("id")) is int
            and isinstance(entry["reviewer"].get("name"), str)
            and bool(entry["reviewer"]["name"])
            for entry in reviewer_entries
        )
    )
    if (
        not valid_reviewers
        or reviewers[0].get("prevent_self_review") is not environment_policy["prevent_self_review"]
    ):
        return [f"{environment_name} manual approval reviewer policy drift"]
    return []


def validate(repository: str, root: Path, policy, api: GitHubApi):
    validate_policy_schema(policy)
    if policy["repository"] != repository:
        raise ApiError("--repo differs from repository-policy.json")

    errors = secret_errors(root, policy)
    errors.extend(repository_errors(repository, policy, api))
    try:
        summaries = api.rulesets()
    except ApiError as error:
        return [*errors, str(error)]
    detailed = detailed_rulesets(api, summaries)
    errors.extend(branch_errors(detailed, policy))
    errors.extend(tag_errors(detailed, policy))
    errors.extend(environment_errors(policy, api))
    return errors


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument("--fixture", type=Path, help=argparse.SUPPRESS)
    arguments = parser.parse_args(argv)
    try:
        policy = load_json(arguments.root / ".github" / "repository-policy.json")
        errors = validate(arguments.repo, arguments.root, policy, GitHubApi(arguments.repo, arguments.fixture))
    except (OSError, ValueError, TypeError, KeyError, ApiError) as error:
        errors = [f"repository policy check failed closed: {error}"]
    for error in errors:
        print(error, file=sys.stderr)
    return int(bool(errors))


if __name__ == "__main__":
    raise SystemExit(main())
