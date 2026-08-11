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
        "name": "example/tool",
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
            write_inventory(
                root,
                inventory_item(
                    kind="download",
                    digest_or_sha="sha256:" + "a" * 64,
                    artifact_url="https://github.com/example/tool/releases/download/v1.2.3/tool",
                ),
            )

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
                "curl -fsSL https://github.com/example/tool/releases/download/v1.2.3/tool -o tool\n", encoding="utf-8"
            )
            write_inventory(
                root,
                inventory_item(
                    kind="download",
                    digest_or_sha="sha256:" + "a" * 64,
                    artifact_url="https://github.com/example/tool/releases/download/v1.2.3/tool",
                ),
            )

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
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            (root / "install-tools.sh").write_text(
                f"curl -fsSL {artifact_url} -o tool\necho '{checksum}  tool' | sha256sum -c -\n",
                encoding="utf-8",
            )
            write_inventory(
                root,
                inventory_item(
                    name="actions/checkout",
                    digest_or_sha=action_sha,
                    source="https://github.com/actions/checkout/releases/tag/v1.2.3",
                ),
                inventory_item(
                    name="alpine",
                    kind="container",
                    digest_or_sha=f"sha256:{image_digest}",
                    source="https://mcr.microsoft.com/v2/alpine/manifests/3.22",
                ),
                inventory_item(
                    name="example-tool",
                    kind="download",
                    digest_or_sha=f"sha256:{checksum}",
                    artifact_url=artifact_url,
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

    def test_rejects_complete_semver_prerelease_suffixes(self):
        for suffix in ("pre", "eap", "m1", "anything"):
            with self.subTest(suffix=suffix):
                errors = checker.validate_inventory(
                    [inventory_item(version=f"1.2.3-{suffix}")],
                    datetime(2026, 8, 11, tzinfo=timezone.utc),
                )
                self.assertTrue(any("prerelease" in error for error in errors))

    def test_rejects_unsupported_source_host(self):
        errors = checker.validate_inventory(
            [inventory_item(source="https://mirror.example.invalid/tool")],
            datetime(2026, 8, 11, tzinfo=timezone.utc),
        )

        self.assertTrue(any("unsupported official source" in error for error in errors))

    def test_rejects_unknown_kind_with_an_official_github_release_url(self):
        errors = checker.validate_inventory(
            [inventory_item(kind="unknown-kind")],
            datetime(2026, 8, 11, tzinfo=timezone.utc),
        )

        self.assertTrue(any("unknown inventory kind" in error for error in errors))

    def test_rejects_invalid_helm_commit_identifier(self):
        errors = checker.validate_inventory(
            [inventory_item(name="helm", kind="github-release", digest_or_sha="sha256:" + "a" * 40)],
            datetime(2026, 8, 11, tzinfo=timezone.utc),
        )

        self.assertTrue(any("raw release commit SHA" in error for error in errors))

    def test_accepts_exact_future_action_owner_names(self):
        names = {item["name"] for item in json.loads((REPOSITORY / "config" / "supply-chain.json").read_text())["inventory"]}

        self.assertTrue(
            {
                "JetBrains/qodana-action",
                "SonarSource/sonarqube-scan-action",
                "slsa-framework/slsa-github-generator",
            }.issubset(names)
        )

    def test_rejects_download_not_checked_against_its_inventory_digest(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            expected = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            (root / "install-tools.sh").write_text(
                f"curl -fsSL {artifact_url} -o tool\necho '{'e' * 64}  tool' | sha256sum -c -\n",
                encoding="utf-8",
            )
            write_inventory(
                root,
                inventory_item(
                    kind="download",
                    digest_or_sha=f"sha256:{expected}",
                    artifact_url=artifact_url,
                ),
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("does not bind", result.stderr)

    def test_rejects_download_checksum_command_without_check_mode(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            (root / "install-tools.sh").write_text(
                f"curl -fsSL {artifact_url} -o tool\necho '{checksum}  tool'; sha256sum tool\n",
                encoding="utf-8",
            )
            write_inventory(root, inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url))

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("does not bind", result.stderr)

    def test_rejects_commented_checksum_check(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            (root / "install-tools.sh").write_text(
                f"curl -fsSL {artifact_url} -o tool\necho '{checksum}  tool' # sha256sum -c -\n",
                encoding="utf-8",
            )
            write_inventory(root, inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url))

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("does not bind", result.stderr)

    def test_accepts_active_checksum_file_check(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            (root / "install-tools.sh").write_text(
                f"curl -fsSL {artifact_url} -o tool\necho '{checksum}  tool' > tool.sha256\nsha256sum -c tool.sha256\n",
                encoding="utf-8",
            )
            write_inventory(root, inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url))

            result = run_checker(root)

            self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_checksum_check_from_dev_null(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            (root / "install-tools.sh").write_text(
                f"curl -fsSL {artifact_url} -o tool\necho '{checksum}  tool'; sha256sum -c - </dev/null\n",
                encoding="utf-8",
            )
            write_inventory(root, inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url))

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("does not bind", result.stderr)

    def test_rejects_stderr_redirected_checksum_file(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            (root / "install-tools.sh").write_text(
                f"curl -fsSL {artifact_url} -o tool\necho '{checksum}  tool' 2> tool.sha256\nsha256sum -c tool.sha256\n",
                encoding="utf-8",
            )
            write_inventory(root, inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url))

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("does not bind", result.stderr)

    def test_accepts_checksum_pipeline_to_standard_input(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            (root / "install-tools.sh").write_text(
                f"curl -fsSL {artifact_url} -o tool\necho '{checksum}  tool' | sha256sum -c -\n",
                encoding="utf-8",
            )
            write_inventory(root, inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url))

            result = run_checker(root)

            self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_control_sequence_before_checksum_pipeline(self):
        for operator in (";", "&&", "||"):
            with self.subTest(operator=operator), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                checksum = "d" * 64
                artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
                (root / "install-tools.sh").write_text(
                    f"curl -fsSL {artifact_url} -o tool\necho '{checksum}  tool' {operator} true | sha256sum -c -\n",
                    encoding="utf-8",
                )
                write_inventory(root, inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url))

                result = run_checker(root)

                self.assertEqual(1, result.returncode)
                self.assertIn("does not bind", result.stderr)

    def test_rejects_control_sequence_before_checksum_file(self):
        for operator in (";", "&&", "||"):
            with self.subTest(operator=operator), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                checksum = "d" * 64
                artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
                (root / "install-tools.sh").write_text(
                    f"curl -fsSL {artifact_url} -o tool\necho '{checksum}  tool' {operator} true > tool.sha256\nsha256sum -c tool.sha256\n",
                    encoding="utf-8",
                )
                write_inventory(root, inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url))

                result = run_checker(root)

                self.assertEqual(1, result.returncode)
                self.assertIn("does not bind", result.stderr)

    def test_rejects_extra_pipe_before_checksum_pipeline(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            (root / "install-tools.sh").write_text(
                f"curl -fsSL {artifact_url} -o tool\necho '{checksum}  tool' | true | sha256sum -c -\n",
                encoding="utf-8",
            )
            write_inventory(root, inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url))

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("does not bind", result.stderr)

    def test_rejects_extra_pipe_before_checksum_file(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            (root / "install-tools.sh").write_text(
                f"curl -fsSL {artifact_url} -o tool\necho '{checksum}  tool' | true > tool.sha256\nsha256sum -c tool.sha256\n",
                encoding="utf-8",
            )
            write_inventory(root, inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url))

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("does not bind", result.stderr)

    def test_rejects_escaped_pipe_before_checksum_pipeline(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            (root / "install-tools.sh").write_text(
                f"curl -fsSL {artifact_url} -o tool\necho '{checksum}  tool' \\| true | sha256sum -c -\n",
                encoding="utf-8",
            )
            write_inventory(root, inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url))

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("does not bind", result.stderr)

    def test_rejects_checksum_file_check_with_control_suffix(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            (root / "install-tools.sh").write_text(
                f"curl -fsSL {artifact_url} -o tool\necho '{checksum}  tool' > tool.sha256\nsha256sum -c tool.sha256 && true\n",
                encoding="utf-8",
            )
            write_inventory(root, inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url))

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("does not bind", result.stderr)

    def test_online_rejects_dotnet_metadata_digest_drift(self):
        item = inventory_item(
            name="dotnet-sdk",
            kind="dotnet-sdk",
            version="10.0.302",
            digest_or_sha="sha512:" + "a" * 128,
            released_at="2026-07-14T00:00:00Z",
            source="https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json",
            artifact_url="https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.302/dotnet-sdk-10.0.302-linux-x64.tar.gz",
        )
        payload = {
            "releases": [{"release-date": "2026-07-14", "sdk": {"version": "10.0.302", "files": [{"url": item["artifact_url"], "hash": "b" * 128}]}}]
        }

        with patch.object(checker, "fetch_json", return_value=(payload, {})):
            errors = checker.validate_online([item], datetime(2026, 8, 11, tzinfo=timezone.utc))

        self.assertTrue(any(".NET metadata digest" in error for error in errors))

    def test_online_rejects_nuget_version_hash_and_timestamp_drift(self):
        item = inventory_item(
            name="Example.Package",
            kind="nuget",
            version="1.2.3",
            digest_or_sha="sha512-base64:expected",
            released_at="2026-01-01T00:00:00Z",
            source="https://api.nuget.org/v3/registration5-gz-semver2/example.package/1.2.3.json",
        )
        payload = {
            "listed": True,
            "published": "2026-01-02T00:00:00Z",
            "catalogEntry": {"version": "1.2.4"},
            "packageContent": "https://api.nuget.org/v3-flatcontainer/example.package/1.2.3/example.package.1.2.3.nupkg",
        }

        with patch.object(checker, "fetch_json", return_value=(payload, {})):
            errors = checker.validate_online([item], datetime(2026, 8, 11, tzinfo=timezone.utc))

        self.assertTrue(any("NuGet version" in error for error in errors))
        self.assertTrue(any("NuGet release timestamp" in error for error in errors))
        self.assertTrue(any("NuGet checksum" in error for error in errors))

    def test_online_rejects_publisher_download_checksum_drift(self):
        artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
        item = inventory_item(
            name="tool",
            kind="download",
            digest_or_sha="sha256:" + "a" * 64,
            artifact_url=artifact_url,
        )
        release = {
            "tag_name": "v1.2.3",
            "published_at": "2026-01-01T00:00:00Z",
            "assets": [{"browser_download_url": artifact_url, "digest": "sha256:" + "b" * 64}],
        }

        with patch.object(checker, "fetch_json", return_value=(release, {})):
            errors = checker.validate_online([item], datetime(2026, 8, 11, tzinfo=timezone.utc))

        self.assertTrue(any("publisher checksum" in error for error in errors))

    def test_online_rejects_container_without_response_digest(self):
        item = inventory_item(
            name="example/container",
            kind="container",
            digest_or_sha="sha256:" + "a" * 64,
            source="https://mcr.microsoft.com/v2/example/container/manifests/1.2.3",
        )

        with patch.object(checker, "fetch_manifest_digest", return_value=None):
            errors = checker.validate_online([item], datetime(2026, 8, 11, tzinfo=timezone.utc))

        self.assertTrue(any("did not provide Docker-Content-Digest" in error for error in errors))


if __name__ == "__main__":
    unittest.main()
