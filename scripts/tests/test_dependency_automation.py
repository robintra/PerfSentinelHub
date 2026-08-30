import copy
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
CHECKER = REPOSITORY_ROOT / "scripts" / "check-dependency-automation.py"
ECOSYSTEMS = {
    "nuget": "/",
    "docker": "/",
    "helm": "/deploy/helm/perf-sentinel-hub",
    "github-actions": "/",
}


def update_block(ecosystem, directory):
    return {
        "package-ecosystem": ecosystem,
        "directory": directory,
        "schedule": {
            "interval": "weekly",
            "day": "monday",
            "time": "06:00",
            "timezone": "Europe/Paris",
        },
        "open-pull-requests-limit": 5,
        "labels": ["dependencies", f"ecosystem:{ecosystem}"],
        "groups": {
            f"ordinary-{ecosystem}": {
                "applies-to": "version-updates",
                "patterns": ["*"],
                "update-types": ["minor", "patch"],
            }
        },
    }


def valid_config():
    return {
        "version": 2,
        "updates": [
            update_block(ecosystem, directory)
            for ecosystem, directory in ECOSYSTEMS.items()
        ],
    }


def dump_yaml_entry(label, item, indent):
    """Render one mapping key or sequence dash, nesting when the item is a non-empty container."""
    prefix = " " * indent
    if isinstance(item, (dict, list)) and item:
        return [f"{prefix}{label}", *dump_yaml(item, indent + 2)]
    return [f"{prefix}{label} {json.dumps(item)}"]


def dump_yaml(value, indent=0):
    if isinstance(value, dict):
        entries = [(f"{key}:", item) for key, item in value.items()]
    else:
        entries = [("-", item) for item in value]
    lines = []
    for label, item in entries:
        lines.extend(dump_yaml_entry(label, item, indent))
    return lines


def write_repository(root, config=None):
    (root / ".github" / "workflows").mkdir(parents=True, exist_ok=True)
    (root / "deploy" / "helm" / "perf-sentinel-hub").mkdir(parents=True, exist_ok=True)
    (root / "config").mkdir(exist_ok=True)
    (root / ".github" / "dependabot.yml").write_text(
        "\n".join(dump_yaml(config or valid_config())) + "\n", encoding="utf-8"
    )
    (root / ".github" / "workflows" / "ci.yml").write_text(
        "name: CI\n", encoding="utf-8"
    )
    (root / "global.json").write_text(
        json.dumps({"sdk": {"version": "10.0.302", "allowPrerelease": False}}),
        encoding="utf-8",
    )
    (root / "Directory.Packages.props").write_text(
        '<PackageVersion Include="Example" Version="1.2.3" />\n', encoding="utf-8"
    )
    (root / "Dockerfile").write_text(
        "FROM example/image:1.2.3@sha256:" + "a" * 64 + "\n", encoding="utf-8"
    )
    (root / "deploy" / "helm" / "perf-sentinel-hub" / "Chart.yaml").write_text(
        "apiVersion: v2\nname: example\nversion: 1.2.3\nappVersion: 1.2.3\n",
        encoding="utf-8",
    )
    (root / "config" / "supply-chain.json").write_text(
        json.dumps(
            {
                "schema_version": 1,
                "inventory": [
                    {"name": "Example", "kind": "nuget", "version": "1.2.3"},
                    {"name": "example/image", "kind": "container", "version": "1.2.3"},
                    {"name": "actions/example", "kind": "github-action", "version": "1.2.3"},
                ],
            }
        ),
        encoding="utf-8",
    )


def run_checker(root):
    return subprocess.run(
        [sys.executable, str(CHECKER)],
        cwd=root,
        text=True,
        capture_output=True,
        check=False,
    )


class DependencyAutomationTests(unittest.TestCase):
    def check_invalid(self, mutate, diagnostic):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config = valid_config()
            mutate(config, root)
            write_repository(root, config)

            result = run_checker(root)

            self.assertEqual(1, result.returncode, result.stderr)
            self.assertIn(diagnostic, result.stderr)

    def test_repository_configuration_is_valid(self):
        result = run_checker(REPOSITORY_ROOT)

        self.assertEqual(0, result.returncode, result.stderr)

    def test_accepts_the_complete_policy(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_repository(root)

            result = run_checker(root)

            self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_duplicate_ecosystem_ownership(self):
        self.check_invalid(
            lambda config, _root: config["updates"].append(copy.deepcopy(config["updates"][0])),
            "owned exactly once",
        )

    def test_rejects_renovate_ownership(self):
        def add_renovate(_config, root):
            (root / "renovate.json").write_text("{}\n", encoding="utf-8")

        self.check_invalid(add_renovate, "Renovate")

    def test_rejects_nonweekly_or_mistimed_updates(self):
        for key, value in (
            ("interval", "daily"),
            ("day", "tuesday"),
            ("time", "05:00"),
            ("timezone", "UTC"),
        ):
            with self.subTest(key=key):
                self.check_invalid(
                    lambda config, _root, key=key, value=value: config["updates"][0][
                        "schedule"
                    ].__setitem__(key, value),
                    "Monday at 06:00 Europe/Paris",
                )

    def test_rejects_any_release_delay(self):
        for value in ({"default-days": 3}, {"default-days": 1}, {}):
            with self.subTest(cooldown=value):
                self.check_invalid(
                    lambda config, _root, value=value: config["updates"][0].__setitem__(
                        "cooldown", value
                    ),
                    "release delays are forbidden",
                )

    def test_rejects_non_integer_policy_numbers(self):
        cases = (
            (
                "version",
                (True, 2.0, "2"),
                lambda config, value: config.__setitem__("version", value),
                "version 2",
            ),
            (
                "pull-request-limit",
                (True, 5.0, "5"),
                lambda config, value: config["updates"][0].__setitem__(
                    "open-pull-requests-limit", value
                ),
                "bounded and labeled",
            ),
        )
        for field, values, mutate, diagnostic in cases:
            for value in values:
                with self.subTest(field=field, value=value):
                    self.check_invalid(
                        lambda config, _root, mutate=mutate, value=value: mutate(
                            config, value
                        ),
                        diagnostic,
                    )

    def test_rejects_prerelease_opt_in(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_repository(root)
            (root / "global.json").write_text(
                json.dumps({"sdk": {"version": "10.0.302", "allowPrerelease": True}}),
                encoding="utf-8",
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("stable-only", result.stderr)

    def test_rejects_a_prerelease_inventory_version(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_repository(root)
            inventory_path = root / "config" / "supply-chain.json"
            inventory = json.loads(inventory_path.read_text(encoding="utf-8"))
            inventory["inventory"][0]["version"] = "1.2.4-rc.1"
            inventory_path.write_text(json.dumps(inventory), encoding="utf-8")

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("stable-only", result.stderr)

    def test_rejects_a_prerelease_container_version(self):
        for version in ("1.2.4-rc.1", "2026.1-eap", "2026.1_EAP", "2026.1.EaP"):
            with self.subTest(version=version), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                write_repository(root)
                inventory_path = root / "config" / "supply-chain.json"
                inventory = json.loads(inventory_path.read_text(encoding="utf-8"))
                inventory["inventory"][1]["version"] = version
                inventory_path.write_text(json.dumps(inventory), encoding="utf-8")

                result = run_checker(root)

                self.assertEqual(1, result.returncode)
                self.assertIn("stable-only", result.stderr)

    def test_rejects_security_grouping(self):
        def group_security(config, _root):
            config["updates"][0]["groups"]["security"] = {
                "applies-to": "security-updates",
                "patterns": ["*"],
            }

        self.check_invalid(group_security, "security updates must remain isolated")

    def test_rejects_incompatible_ordinary_grouping(self):
        for key, value in (
            ("applies-to", "security-updates"),
            ("patterns", ["Microsoft.*"]),
            ("update-types", ["major", "minor", "patch"]),
        ):
            with self.subTest(key=key):
                self.check_invalid(
                    lambda config, _root, key=key, value=value: next(
                        iter(config["updates"][0]["groups"].values())
                    ).__setitem__(key, value),
                    "ordinary patch/minor",
                )

    def test_rejects_missing_labels_or_unbounded_pull_requests(self):
        for key, value in (("labels", []), ("open-pull-requests-limit", 100)):
            with self.subTest(key=key):
                self.check_invalid(
                    lambda config, _root, key=key, value=value: config["updates"][0].__setitem__(
                        key, value
                    ),
                    "bounded and labeled",
                )

    def test_rejects_dependabot_auto_merge_workflows(self):
        def add_auto_merge(_config, root):
            workflow = root / ".github" / "workflows" / "dependabot-automerge.yml"
            workflow.parent.mkdir(parents=True, exist_ok=True)
            workflow.write_text(
                "name: Dependency updates\n"
                "jobs:\n"
                "  merge:\n"
                "    if: ${{ github.actor == 'dependabot[bot]' }}\n"
                f"    # {'distance-' * 30}\n"
                "    steps:\n"
                "      - run: gh pr merge \"$PR_URL\" --squash --auto\n",
                encoding="utf-8",
            )

        self.check_invalid(add_auto_merge, "auto-merge")

    def test_rejects_lexically_obfuscated_dependabot_auto_merge_workflows(self):
        def add_auto_merge(_config, root):
            workflow = root / ".github" / "workflows" / "merge.yml"
            workflow.parent.mkdir(parents=True, exist_ok=True)
            workflow.write_text(
                "name: Dependency updates\n"
                "jobs:\n"
                "  merge:\n"
                "    if: ${{ github . actor == 'DEPENDABOT[bot]' }}\n"
                "    steps:\n"
                "      - run: gh pr merge \"$MANUAL_PR_URL\" --squash\n"
                "      - run: |\n"
                "          gh \\\n"
                "            pr \\\n"
                "            merge \"$PR_URL\" --squash \\\n"
                "            --auto\n",
                encoding="utf-8",
            )

        self.check_invalid(add_auto_merge, "auto-merge")

    def test_rejects_direct_dependabot_merges_without_auto(self):
        identities = (
            "github . actor == 'dependabot[bot]'",
            "github . event . pull_request . user . login == 'DEPENDABOT[BOT]'",
        )
        for identity in identities:
            with self.subTest(identity=identity):
                def add_direct_merge(_config, root, identity=identity):
                    workflow = root / ".github" / "workflows" / "merge.yml"
                    workflow.parent.mkdir(parents=True, exist_ok=True)
                    workflow.write_text(
                        "name: Dependency updates\n"
                        "jobs:\n"
                        "  merge:\n"
                        f"    if: ${{{{ {identity} }}}}\n"
                        "    steps:\n"
                        "      - run: |\n"
                        "          gh \\\n"
                        "            pr \\\n"
                        "            merge \"$PR_URL\" --squash\n",
                        encoding="utf-8",
                    )

                self.check_invalid(add_direct_merge, "auto-merge")

    def test_rejects_absolute_gh_paths_for_dependabot_merges(self):
        cases = (
            ("/usr/bin/gh", "github.actor"),
            ("/usr/local/bin/gh", "github.event.pull_request.user.login"),
            ("/opt/actions/tools/current/gh", "github . actor"),
        )
        for command, identity in cases:
            with self.subTest(command=command, identity=identity):
                def add_direct_merge(_config, root, command=command, identity=identity):
                    workflow = root / ".github" / "workflows" / "merge.yml"
                    workflow.parent.mkdir(parents=True, exist_ok=True)
                    workflow.write_text(
                        "name: Dependency updates\n"
                        "jobs:\n"
                        "  merge:\n"
                        f"    if: ${{{{ {identity} == 'dependabot[bot]' }}}}\n"
                        "    steps:\n"
                        f"      - run: {command} pr merge \"$PR_URL\" --squash\n",
                        encoding="utf-8",
                    )

                self.check_invalid(add_direct_merge, "auto-merge")


if __name__ == "__main__":
    unittest.main()
