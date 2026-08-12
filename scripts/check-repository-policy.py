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


SCRIPT_ROOT = Path(__file__).resolve().parents[1]
SECRET_REFERENCE = re.compile(r"\$\{\{\s*secrets\.([A-Z][A-Z0-9_]*)\s*\}\}")
SECRET_TOKEN = re.compile(r"(?<![A-Za-z0-9_])secrets(?![A-Za-z0-9_])", re.IGNORECASE)


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


class GitHubApi:
    def __init__(self, repository: str, fixture: Path | None):
        self.repository = repository
        self.fixture = load_json(fixture) if fixture else None

    def _fixture_key(self, endpoint: str, page: int | None) -> str:
        if endpoint == f"repos/{self.repository}":
            return "repository"
        if endpoint == f"repos/{self.repository}/rulesets":
            return f"rulesets:{page}"
        prefix = f"repos/{self.repository}/rulesets/"
        if endpoint.startswith(prefix):
            return f"ruleset:{endpoint.removeprefix(prefix)}"
        environment = quote("hub-release", safe="")
        if endpoint == f"repos/{self.repository}/environments/{environment}":
            return "environment"
        if endpoint.startswith("apps/"):
            return "app"
        raise ApiError(f"unrecognized fixture endpoint: {endpoint}")

    def get(self, endpoint: str, *, page: int | None = None):
        if self.fixture is not None:
            key = self._fixture_key(endpoint, page)
            response = self.fixture.get(key)
            if not isinstance(response, dict):
                raise ApiError(f"fixture response is missing: {key}")
            if set(response) - {"status", "headers", "body"}:
                raise ApiError(f"fixture response has unknown fields: {key}")
            if type(response.get("status")) is not int or response["status"] != 200:
                raise ApiError(f"GitHub API {key} returned HTTP {response.get('status')}")
            if "body" not in response:
                raise ApiError(f"fixture response body is missing: {key}")
            return response["body"]

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

    def rulesets(self):
        result = []
        for page in range(1, 101):
            payload = self.get(f"repos/{self.repository}/rulesets", page=page)
            if not isinstance(payload, list):
                raise ApiError(f"rulesets page {page} is not an array")
            result.extend(payload)
            if len(payload) < 100:
                return result
        raise ApiError("ruleset pagination exceeded 100 pages")


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
        stripped = SECRET_REFERENCE.sub(lambda match: names.add(match.group(1)) or "", text)
        if SECRET_TOKEN.search(stripped):
            raise ApiError(f"{path}: non-canonical workflow secret reference")
    return names


def inventory_secrets(root: Path):
    try:
        payload = load_json(root / "config" / "secret-inventory.json")
        entries = payload["secrets"]
    except (OSError, UnicodeError, ValueError, KeyError, TypeError) as error:
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


def validate(repository: str, root: Path, policy, api: GitHubApi):
    errors = []
    expected_fields = {
        "schema_version", "repository", "visibility", "default_branch",
        "security_features", "branch_ruleset", "tag_ruleset",
        "release_environment", "workflow_secrets",
    }
    if not isinstance(policy, dict) or set(policy) != expected_fields or policy.get("schema_version") != 1:
        raise ApiError("repository policy schema is invalid")
    if policy["repository"] != repository:
        raise ApiError("--repo differs from repository-policy.json")

    expected_secrets = set(policy["workflow_secrets"])
    inventoried = inventory_secrets(root)
    referenced = workflow_secrets(root)
    for name in sorted(referenced - inventoried):
        errors.append(f"workflow secret {name} is absent from the inventory")
    if inventoried != expected_secrets or referenced != expected_secrets:
        errors.append(
            "workflow secret references must exactly match policy: "
            f"expected={sorted(expected_secrets)}, inventoried={sorted(inventoried)}, "
            f"referenced={sorted(referenced)}"
        )

    repository_data = require_object(api.get(f"repos/{repository}"), "repository response")
    if repository_data.get("full_name") != repository:
        errors.append("repository identity drift")
    if repository_data.get("visibility") != policy["visibility"] or repository_data.get("private") is not False:
        errors.append("repository visibility must be public")
    if repository_data.get("default_branch") != policy["default_branch"]:
        errors.append("default branch drift")
    security = repository_data.get("security_and_analysis")
    if not isinstance(security, dict):
        errors.append("public security_and_analysis fields are missing")
    else:
        for feature in policy["security_features"]:
            state = security.get(feature)
            if not isinstance(state, dict) or state.get("status") != "enabled":
                errors.append(f"public security feature {feature} must be enabled")

    try:
        summaries = api.rulesets()
    except ApiError as error:
        return [*errors, str(error)]
    detailed = []
    seen = set()
    for summary in summaries:
        if not isinstance(summary, dict) or type(summary.get("id")) is not int:
            raise ApiError("ruleset summary lacks an integer id")
        identifier = summary["id"]
        if identifier in seen:
            raise ApiError(f"duplicate ruleset id {identifier}")
        seen.add(identifier)
        detailed.append(require_object(api.get(f"repos/{repository}/rulesets/{identifier}"), f"ruleset {identifier}"))

    branch_policy = require_object(policy["branch_ruleset"], "branch policy")
    branch = active_ruleset(detailed, "branch", branch_policy["ref_include"], "default branch")
    branch_rules = rule_map(branch, "default branch ruleset")
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
    pull_request = branch_rules.get("pull_request")
    parameters = pull_request.get("parameters") if isinstance(pull_request, dict) else None
    if not isinstance(parameters, dict):
        errors.append("pull_request rule is required for everyone")
    else:
        if parameters.get("required_approving_review_count") != branch_policy["required_approving_review_count"]:
            errors.append("pull_request approval count drift")
        if parameters.get("required_review_thread_resolution") is not branch_policy["required_review_thread_resolution"]:
            errors.append("pull_request conversations must be resolved")
        if sorted(parameters.get("allowed_merge_methods", [])) != sorted(branch_policy["allowed_merge_methods"]):
            errors.append("pull_request merge methods drift")

    expected_checks = []
    for check in branch_policy["required_status_checks"]:
        app_slug = check.get("app_slug")
        source = None
        if app_slug is not None:
            app = require_object(api.get(f"apps/{quote(app_slug, safe='')}"), f"GitHub App {app_slug}")
            if app.get("slug") != app_slug or type(app.get("id")) is not int:
                raise ApiError(f"GitHub App {app_slug} identity is ambiguous")
            source = app["id"]
        expected_checks.append({"context": check.get("context"), "integration_id": source})
    status_rule = branch_rules.get("required_status_checks")
    status_parameters = status_rule.get("parameters") if isinstance(status_rule, dict) else None
    actual_checks = status_parameters.get("required_status_checks") if isinstance(status_parameters, dict) else None
    normalized_checks = []
    if isinstance(actual_checks, list):
        for item in actual_checks:
            if (
                not isinstance(item, dict)
                or not isinstance(item.get("context"), str)
                or set(item) - {"context", "integration_id"}
                or item.get("integration_id") is not None
                and type(item.get("integration_id")) is not int
            ):
                normalized_checks = None
                break
            normalized_checks.append(
                {
                    "context": item["context"],
                    "integration_id": item.get("integration_id"),
                }
            )
    else:
        normalized_checks = None
    if (
        normalized_checks is None
        or status_parameters.get("strict_required_status_checks_policy") is not True
        or sorted(normalized_checks, key=lambda item: item["context"])
        != sorted(expected_checks, key=lambda item: item["context"])
    ):
        errors.append("required status checks differ or lack the expected App source")

    tag_policy = require_object(policy["tag_ruleset"], "tag policy")
    tag = active_ruleset(detailed, "tag", tag_policy["ref_include"], "release tag")
    tag_rules = rule_map(tag, "release tag ruleset")
    if tag.get("bypass_actors") != tag_policy["bypass_actors"]:
        errors.append("release tag bypass is forbidden")
    if not tag_policy["allow_force_updates"] and "non_fast_forward" not in tag_rules:
        errors.append("release tag force updates must be forbidden")
    if not tag_policy["allow_deletions"] and "deletion" not in tag_rules:
        errors.append("release tag deletion protection is required")

    environment_policy = require_object(policy["release_environment"], "release environment policy")
    environment_name = environment_policy["name"]
    environment = require_object(api.get(f"repos/{repository}/environments/{quote(environment_name, safe='')}"), "release environment")
    rules = environment.get("protection_rules")
    reviewers = [rule for rule in rules if isinstance(rule, dict) and rule.get("type") == "required_reviewers"] if isinstance(rules, list) else []
    if len(reviewers) != 1:
        errors.append(f"{environment_name} manual approval is required")
    else:
        reviewer_entries = reviewers[0].get("reviewers")
        valid_reviewers = (
            isinstance(reviewer_entries, list)
            and len(reviewer_entries) >= environment_policy["minimum_required_reviewers"]
            and all(
                isinstance(entry, dict)
                and entry.get("type") in {"User", "Team"}
                and isinstance(entry.get("reviewer"), dict)
                and type(entry["reviewer"].get("id")) is int
                for entry in reviewer_entries
            )
        )
        if (
            not valid_reviewers
            or reviewers[0].get("prevent_self_review") is not environment_policy["prevent_self_review"]
        ):
            errors.append(f"{environment_name} manual approval reviewer policy drift")

    return errors


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", required=True)
    parser.add_argument("--root", type=Path, default=Path.cwd())
    parser.add_argument("--fixture", type=Path, help=argparse.SUPPRESS)
    arguments = parser.parse_args(argv)
    try:
        policy = load_json(SCRIPT_ROOT / ".github" / "repository-policy.json")
        errors = validate(arguments.repo, arguments.root, policy, GitHubApi(arguments.repo, arguments.fixture))
    except (OSError, UnicodeError, ValueError, TypeError, KeyError, ApiError) as error:
        errors = [f"repository policy check failed closed: {error}"]
    for error in errors:
        print(error, file=sys.stderr)
    return int(bool(errors))


if __name__ == "__main__":
    raise SystemExit(main())
