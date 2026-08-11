import json
import importlib.util
import os
import subprocess
import sys
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch


REPOSITORY = Path(__file__).resolve().parents[2]
CHECKER = REPOSITORY / "scripts" / "check-supply-chain.py"
SPEC = importlib.util.spec_from_file_location("supply_chain_checker", CHECKER)
checker = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(checker)


def inventory_item(**overrides):
    item = {
        "name": "example-tool",
        "kind": "github-action",
        "version": "1.2.3",
        "digest_or_sha": "a" * 40,
        "released_at": "2026-01-01T00:00:00Z",
        "source": "https://github.com/example/tool/releases/tag/v1.2.3",
        "stabilization_exempt": False,
        "reason": "Pinned for repeatable builds.",
    }
    item.update(overrides)
    return item


def write_inventory(root, *items):
    inventory_path = root / "config" / "supply-chain.json"
    inventory_path.parent.mkdir(parents=True)
    inventory_path.write_text(
        json.dumps({"schema_version": 1, "inventory": list(items)}), encoding="utf-8"
    )


def run_checker(root, online=False):
    arguments = [sys.executable, str(CHECKER)]
    if online:
        arguments.append("--online")
    return subprocess.run(arguments, cwd=root, text=True, capture_output=True, check=False)


class SupplyChainCheckerTests(unittest.TestCase):
    def test_rejects_unpinned_action(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            workflow = root / "ci.yml"
            workflow.write_text("steps:\n  - uses: actions/checkout@v7\n", encoding="utf-8")
            write_inventory(root, inventory_item())

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("full commit SHA", result.stderr)

    def test_rejects_container_without_digest(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "Dockerfile").write_text("FROM alpine:3.22\n", encoding="utf-8")
            write_inventory(root, inventory_item())

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("@sha256:", result.stderr)

    def test_rejects_download_without_checksum(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "install-tools.sh").write_text(
                "curl -fsSL https://example.invalid/tool -o tool\n", encoding="utf-8"
            )
            write_inventory(root, inventory_item())

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("checksum", result.stderr)

    def test_rejects_prerelease_inventory_item(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_inventory(root, inventory_item(version="2.0.0-rc.1"))

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("prerelease", result.stderr)

    def test_rejects_recent_non_exempt_release(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            released_at = (datetime.now(timezone.utc) - timedelta(hours=71)).isoformat()
            write_inventory(root, inventory_item(released_at=released_at))

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("72 hours", result.stderr)

    def test_accepts_pinned_declarations_and_stable_inventory(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            action_sha = "b" * 40
            image_digest = "c" * 64
            checksum = "d" * 64
            (root / "ci.yml").write_text(
                f"steps:\n  - uses: actions/checkout@{action_sha}\n", encoding="utf-8"
            )
            (root / "Dockerfile").write_text(
                f"FROM alpine:3.22@sha256:{image_digest}\n", encoding="utf-8"
            )
            (root / "install-tools.sh").write_text(
                f"curl -fsSL https://example.invalid/tool -o tool\necho '{checksum}  tool' | sha256sum -c -\n",
                encoding="utf-8",
            )
            write_inventory(
                root,
                inventory_item(name="actions/checkout", digest_or_sha=action_sha),
                inventory_item(
                    name="alpine",
                    kind="container",
                    digest_or_sha=f"sha256:{image_digest}",
                ),
                inventory_item(
                    name="example-tool",
                    kind="download",
                    digest_or_sha=f"sha256:{checksum}",
                ),
            )

            result = run_checker(root)

            self.assertEqual(0, result.returncode, result.stderr)

    def test_online_check_rejects_a_moved_action_tag(self):
        item = inventory_item(
            name="actions/checkout",
            digest_or_sha="a" * 40,
            source="https://github.com/actions/checkout/releases/tag/v1.2.3",
        )
        release = {"tag_name": "v1.2.3", "published_at": "2026-01-01T00:00:00Z"}
        commit = {"sha": "b" * 40}

        with patch.object(checker, "fetch_json", side_effect=[(release, {}), (commit, {})]):
            errors = checker.validate_online([item], datetime(2026, 8, 11, tzinfo=timezone.utc))

        self.assertTrue(any("release commit moved" in error for error in errors))

    def test_github_requests_use_the_configured_token(self):
        with patch.dict(os.environ, {"GITHUB_TOKEN": "test-token"}, clear=False):
            headers = checker.request_headers("https://api.github.com/repos/example/tool/releases", "application/json")

        self.assertEqual("Bearer test-token", headers["Authorization"])


if __name__ == "__main__":
    unittest.main()
