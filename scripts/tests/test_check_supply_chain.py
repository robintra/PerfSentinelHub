import base64
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
ARTIFACT_URL = "https://github.com/example/tool/releases/download/v1.2.3/tool"
CHECKSUM = "d" * 64
NUGET_HASH = base64.b64encode(b"n" * 64).decode("ascii")
NUGET_DIGEST = f"sha512-base64:{NUGET_HASH}"
DOWNLOAD_HEADER = ("#!/bin/dash", "set -eu")
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


def dotnet_inventory_item():
    return inventory_item(
        name="dotnet-sdk",
        kind="dotnet-sdk",
        version="10.0.302",
        digest_or_sha="sha512:" + "a" * 128,
        released_at="2026-07-14T00:00:00Z",
        source=checker.DOTNET_RELEASES,
        artifact_url="https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.302/dotnet-sdk-10.0.302-linux-x64.tar.gz",
    )


def write_required_declarations(root, packages=()):
    (root / "global.json").write_text(
        json.dumps({"sdk": {"version": "10.0.302"}}), encoding="utf-8"
    )
    package_lines = "\n".join(
        f'    <PackageVersion Include="{name}" Version="{version}" />'
        for name, version in packages
    )
    (root / "Directory.Packages.props").write_text(
        f"<Project>\n  <ItemGroup>\n{package_lines}\n  </ItemGroup>\n</Project>\n",
        encoding="utf-8",
    )


def canonical_download_lines(url=ARTIFACT_URL, checksum=CHECKSUM, output="tool"):
    return (
        *DOWNLOAD_HEADER,
        f"/usr/bin/curl -q -fsSL {url} -o {output}",
        f"/usr/bin/printf '{checksum}  {output}\\n' | /usr/bin/sha256sum -c -",
    )


def write_download_fixture(root, item, *body):
    (root / "install-tools.sh").write_text(
        "\n".join((*DOWNLOAD_HEADER, *body)) + "\n", encoding="utf-8"
    )
    write_inventory(root, item, dotnet_inventory_item())
    write_required_declarations(root)


def run_checker(root, online=False):
    arguments = [sys.executable, str(CHECKER)]
    if online:
        arguments.append("--online")
    return subprocess.run(arguments, cwd=root, text=True, capture_output=True, check=False)


def run_download_lines(*lines, items=None):
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        body = lines
        if tuple(lines[:2]) != DOWNLOAD_HEADER:
            body = (*DOWNLOAD_HEADER, *lines)
        (root / "install-tools.sh").write_text("\n".join(body) + "\n", encoding="utf-8")
        if items is None:
            items = (
                inventory_item(
                    kind="download",
                    digest_or_sha=f"sha256:{CHECKSUM}",
                    artifact_url=ARTIFACT_URL,
                ),
            )
        inventory = list(items)
        if not any(item.get("kind") == "dotnet-sdk" for item in inventory):
            inventory.append(dotnet_inventory_item())
        write_inventory(root, *inventory)
        write_required_declarations(root)
        return run_checker(root)


def run_repository_file(filename, content, *items):
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        target = root / filename
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding="utf-8")
        inventory = [*items, dotnet_inventory_item()]
        write_inventory(root, *inventory)
        write_required_declarations(root)
        return run_checker(root)


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

    def test_rejects_container_tag_that_differs_from_inventory(self):
        digest = "c" * 64
        container = inventory_item(
            name="mcr.microsoft.com/dotnet/runtime-deps",
            kind="container",
            version="10.0.10-noble-chiseled-extra",
            digest_or_sha=f"sha256:{digest}",
            released_at="2026-07-14T00:00:00Z",
            source="https://mcr.microsoft.com/v2/dotnet/runtime-deps/manifests/10.0.10-noble-chiseled-extra",
        )

        result = run_repository_file(
            "Dockerfile",
            f"FROM mcr.microsoft.com/dotnet/runtime-deps:latest@sha256:{digest}\n",
            container,
        )

        self.assertEqual(1, result.returncode)
        self.assertIn("tag", result.stderr)

    def test_rejects_container_source_tag_that_differs_from_inventory(self):
        item = inventory_item(
            name="mcr.microsoft.com/dotnet/sdk",
            kind="container",
            version="10.0.302-noble-aot",
            digest_or_sha="sha256:" + "c" * 64,
            source="https://mcr.microsoft.com/v2/dotnet/sdk/manifests/latest",
        )

        errors = checker.validate_inventory(
            [item], datetime(2026, 8, 11, tzinfo=timezone.utc)
        )

        self.assertTrue(any("source" in error for error in errors))

    def test_rejects_download_without_checksum(self):
        result = run_download_lines(
            f"/usr/bin/curl -q -fsSL {ARTIFACT_URL} -o tool"
        )

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

    def test_rejects_non_boolean_stabilization_exemptions(self):
        now = datetime(2026, 8, 11, tzinfo=timezone.utc)
        for value in ("false", "true", 0, 1, None):
            with self.subTest(value=value):
                errors = checker.validate_inventory(
                    [inventory_item(stabilization_exempt=value)], now
                )

                self.assertTrue(any("boolean" in error for error in errors))

    def test_rejects_invalid_or_overlong_stabilization_expiry(self):
        now = datetime(2026, 8, 11, tzinfo=timezone.utc)
        expiries = (None, "not-a-date", "2026-08-10T00:00:00Z", "2026-11-10T00:00:01Z")
        for expiry in expiries:
            with self.subTest(expiry=expiry):
                item = inventory_item(
                    released_at="2026-08-10T00:00:00Z",
                    stabilization_exempt=True,
                )
                if expiry is not None:
                    item["expiry"] = expiry

                errors = checker.validate_inventory([item], now)

                self.assertTrue(any("expiry" in error for error in errors))

    def test_accepts_a_bounded_stabilization_exemption(self):
        now = datetime(2026, 8, 11, tzinfo=timezone.utc)
        item = inventory_item(
            released_at="2026-08-10T00:00:00Z",
            stabilization_exempt=True,
            expiry="2026-09-01T00:00:00Z",
        )

        errors = checker.validate_inventory([item], now)

        self.assertEqual([], errors)

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
                f"FROM mcr.microsoft.com/dotnet/sdk:10.0.302-noble-aot@sha256:{image_digest}\n",
                encoding="utf-8",
            )
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            (root / "install-tools.sh").write_text(
                "\n".join(canonical_download_lines(artifact_url, checksum)) + "\n",
                encoding="utf-8",
            )
            write_inventory(
                root,
                dotnet_inventory_item(),
                inventory_item(
                    name="actions/checkout",
                    digest_or_sha=action_sha,
                    source="https://github.com/actions/checkout/releases/tag/v1.2.3",
                ),
                inventory_item(
                    name="mcr.microsoft.com/dotnet/sdk",
                    kind="container",
                    version="10.0.302-noble-aot",
                    digest_or_sha=f"sha256:{image_digest}",
                    released_at="2026-07-14T00:00:00Z",
                    source="https://mcr.microsoft.com/v2/dotnet/sdk/manifests/10.0.302-noble-aot",
                ),
                inventory_item(
                    name="example-tool",
                    kind="download",
                    digest_or_sha=f"sha256:{checksum}",
                    artifact_url=artifact_url,
                ),
            )
            write_required_declarations(root)

            result = run_checker(root)

            self.assertEqual(0, result.returncode, result.stderr)

    def test_accepts_canonical_download_to_safe_relative_path(self):
        destination = "tools/tool-1.2_3.tar.gz"

        result = run_download_lines(
            f"/usr/bin/curl -q -fsSL {ARTIFACT_URL} -o {destination}",
            f"/usr/bin/printf '{CHECKSUM}  {destination}\\n' | /usr/bin/sha256sum -c -",
        )

        self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_noncanonical_download_command_suffixes(self):
        suffixes = {
            "semicolon": " ; true",
            "and": " && true",
            "or": " || true",
            "pipe": " | true",
            "stdout redirect": " >/dev/null",
            "stderr redirect": " 2>/dev/null",
            "inline comment": " # comment",
            "trailing whitespace": " ",
        }
        for name, suffix in suffixes.items():
            with self.subTest(name=name):
                result = run_download_lines(
                    f"/usr/bin/curl -q -fsSL {ARTIFACT_URL} -o tool{suffix}",
                    f"/usr/bin/printf '{CHECKSUM}  tool\\n' | /usr/bin/sha256sum -c -",
                )

                self.assertEqual(1, result.returncode)
                self.assertIn("canonical", result.stderr)

    def test_rejects_obfuscated_downloaders_with_literal_url(self):
        for downloader in (r"c\url", "cu''rl", "w''get"):
            with self.subTest(downloader=downloader):
                result = run_download_lines(
                    f"{downloader} -fsSL {ARTIFACT_URL} -o tool",
                )

                self.assertEqual(1, result.returncode)
                self.assertIn("canonical", result.stderr)

    def test_rejects_combined_downloader_and_url_obfuscation(self):
        commands = (
            rf"c\url -fsSL h\ttps://github.com/example/tool/releases/download/v1.2.3/tool -o tool",
            f"cu''rl -fsSL HTTPS://github.com/example/tool/releases/download/v1.2.3/tool -o tool",
            f"cu''rl -fsSL https:''//github.com/example/tool/releases/download/v1.2.3/tool -o tool",
        )
        for command in commands:
            with self.subTest(command=command):
                result = run_download_lines(command)

                self.assertEqual(1, result.returncode)
                self.assertIn("canonical", result.stderr)

    def test_rejects_shell_redefinitions_sources_and_environment_hooks(self):
        prefixes = {
            "echo function": "echo() { :; }",
            "sha256sum function": "sha256sum() { return 0; }",
            "absolute executable function": "function /usr/bin/sha256sum { return 0; }",
            "source": ". ./helpers.sh",
            "BASH_ENV": "BASH_ENV=./helpers.sh",
            "ENV": "ENV=./helpers.sh",
            "LD_PRELOAD": "LD_PRELOAD=./helpers.so",
        }
        for name, prefix in prefixes.items():
            with self.subTest(name=name):
                result = run_download_lines(
                    prefix,
                    f"curl -fsSL {ARTIFACT_URL} -o tool",
                    f"echo '{CHECKSUM}  tool' | sha256sum -c -",
                )

                self.assertEqual(1, result.returncode)
                self.assertIn("canonical", result.stderr)

    def test_rejects_alternative_shell_downloaders(self):
        commands = (
            "gh release download v1.2.3 --repo example/tool --pattern tool",
            "g\\h release d''ownload v1.2.3 --repo example/tool --pattern tool",
            "python3 -m pip download example-tool",
            "pwsh -Command Invoke-WebRequest https:''//example.invalid/tool",
        )
        for command in commands:
            with self.subTest(command=command):
                result = run_download_lines(command)

                self.assertEqual(1, result.returncode)
                self.assertIn("canonical", result.stderr)

    def test_rejects_network_downloads_outside_canonical_shell_scripts(self):
        cases = {
            "urllib": ("download.py", "import urllib.request\nurllib.request.urlopen('https://example.invalid/tool')\n"),
            "requests": ("download.py", "import requests\nrequests.get('https://example.invalid/tool')\n"),
            "httpx": ("download.py", "import httpx\nhttpx.get('https://example.invalid/tool')\n"),
            "aiohttp": ("download.py", "import aiohttp\naiohttp.ClientSession()\n"),
            "subprocess": ("download.py", "import subprocess\nsubprocess.run(['curl', 'example.invalid/tool'])\n"),
            "workflow": ("ci.yml", "steps:\n  - run: wget example.invalid/tool\n"),
            "gh run": ("ci.yml", "steps:\n  - run: gh run download 1234\n"),
            "gh repo clone": ("ci.yml", "steps:\n  - run: gh repo clone example/tool\n"),
            "obfuscated gh repo clone": ("ci.yml", "steps:\n  - run: g\\h r''epo c\\lone example/tool\n"),
            "git clone": ("Dockerfile.tools", "RUN git clone example.invalid/tool\n"),
            "suffix Dockerfile": ("tools.Dockerfile", "RUN git clone example.invalid/tool\n"),
            "pip install": ("Dockerfile", "RUN pip install example-tool\n"),
            "Dockerfile": ("Dockerfile", "RUN curl example.invalid/tool -o tool\n"),
            "PowerShell": ("download.ps1", "Invoke-WebRequest example.invalid/tool -OutFile tool\n"),
            "HttpClient": ("download.ps1", "$client = [System.Net.Http.HttpClient]::new()\n"),
            "WebClient": ("download.ps1", "(New-Object Net.WebClient).OpenRead('example.invalid/tool')\n"),
        }
        for name, (filename, content) in cases.items():
            with self.subTest(name=name):
                result = run_repository_file(filename, content)

                self.assertEqual(1, result.returncode)
                self.assertIn("shell scripts", result.stderr)

    def test_rejects_noncanonical_yaml_uses_keys(self):
        action = inventory_item(
            name="actions/checkout",
            digest_or_sha="a" * 40,
            source="https://github.com/actions/checkout/releases/tag/v1.2.3",
        )
        declarations = (
            '  - "uses": actions/checkout@v7\n',
            "  - 'uses': actions/checkout@v7\n",
            '  - "u\\u0073es": actions/checkout@v7\n',
            '  - {!!str "u\\x73es": actions/checkout@v7}\n',
            "  - {uses: actions/checkout@v7}\n",
            "  ? uses\n  : actions/checkout@v7\n",
        )
        for declaration in declarations:
            with self.subTest(declaration=declaration):
                result = run_repository_file("ci.yml", f"steps:\n{declaration}", action)

                self.assertEqual(1, result.returncode)
                self.assertIn("canonical", result.stderr)

    def test_rejects_download_declarations_outside_shell_scripts(self):
        files = {
            "ci.yml": (
                "steps:\n"
                "  - run: >\n"
                f"      curl -fsSL {ARTIFACT_URL} -o tool\n"
                f"      echo '{CHECKSUM}  tool' | sha256sum -c -\n"
            ),
            "Dockerfile": (
                "RUN <<EOF\n"
                f"curl -fsSL {ARTIFACT_URL} -o tool\n"
                f"echo '{CHECKSUM}  tool' | sha256sum -c -\n"
                "EOF\n"
            ),
        }
        for filename, content in files.items():
            with self.subTest(filename=filename), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                (root / filename).write_text(content, encoding="utf-8")
                write_inventory(
                    root,
                    inventory_item(
                        kind="download",
                        digest_or_sha=f"sha256:{CHECKSUM}",
                        artifact_url=ARTIFACT_URL,
                    ),
                )

                result = run_checker(root)

                self.assertEqual(1, result.returncode)
                self.assertIn("shell scripts", result.stderr)

    def test_rejects_non_lf_separators_between_download_and_check(self):
        separators = ("\r\n", "\r", "\v", "\f", "\x85", "\u2028", "\u2029")
        for separator in separators:
            with self.subTest(separator=ascii(separator)):
                result = run_download_lines(
                    f"curl -fsSL {ARTIFACT_URL} -o tool{separator}"
                    f"echo '{CHECKSUM}  tool' | sha256sum -c -",
                )

                self.assertEqual(1, result.returncode)
                self.assertIn("canonical", result.stderr)

    def test_rejects_noncanonical_sha256_prefix(self):
        digest = f"SHA256:{CHECKSUM}"
        result = run_download_lines(
            f"/usr/bin/curl -q -fsSL {ARTIFACT_URL} -o tool",
            f"/usr/bin/printf '{digest.removeprefix('SHA256:')}  tool\\n' | /usr/bin/sha256sum -c -",
            items=(
                inventory_item(
                    kind="download",
                    digest_or_sha=digest,
                    artifact_url=ARTIFACT_URL,
                ),
            ),
        )

        self.assertEqual(1, result.returncode)
        self.assertIn("sha256 checksum", result.stderr)

    def test_rejects_historical_unsafe_download_destinations(self):
        destinations = (
            "tool$(pwd)",
            "tool`pwd`",
            "tool&&true",
            "tool>/dev/null",
            "tool*",
            "../tool",
        )
        for destination in destinations:
            with self.subTest(destination=destination):
                result = run_download_lines(
                    f"/usr/bin/curl -q -fsSL {ARTIFACT_URL} -o {destination}",
                    f"/usr/bin/printf '{CHECKSUM}  {destination}\\n' | /usr/bin/sha256sum -c -",
                )

                self.assertEqual(1, result.returncode)
                self.assertIn("canonical", result.stderr)

    def test_rejects_each_shell_metacharacter_in_download_destination(self):
        metacharacters = ("$", "`", ";", "&", "|", ">", "<", "*", "?", "[", "]", "(", ")", "\\", "'", '"')
        for metacharacter in metacharacters:
            with self.subTest(metacharacter=metacharacter):
                destination = f"tool{metacharacter}suffix"
                result = run_download_lines(
                    f"/usr/bin/curl -q -fsSL {ARTIFACT_URL} -o {destination}",
                    f"/usr/bin/printf '{CHECKSUM}  {destination}\\n' | /usr/bin/sha256sum -c -",
                )

                self.assertEqual(1, result.returncode)
                self.assertIn("canonical", result.stderr)

    def test_rejects_download_destination_with_spaces(self):
        result = run_download_lines(
            f"/usr/bin/curl -q -fsSL {ARTIFACT_URL} -o 'tool file'",
            f"/usr/bin/printf '{CHECKSUM}  tool\\n' | /usr/bin/sha256sum -c -",
        )

        self.assertEqual(1, result.returncode)
        self.assertIn("canonical", result.stderr)

    def test_rejects_download_with_noncanonical_option_order(self):
        result = run_download_lines(
            f"/usr/bin/curl -o tool -fsSL {ARTIFACT_URL}",
            f"/usr/bin/printf '{CHECKSUM}  tool\\n' | /usr/bin/sha256sum -c -",
        )

        self.assertEqual(1, result.returncode)
        self.assertIn("canonical", result.stderr)

    def test_rejects_unsafe_artifact_url(self):
        artifact_url = f"{ARTIFACT_URL}$(pwd)"
        result = run_download_lines(
            f"/usr/bin/curl -q -fsSL {artifact_url} -o tool",
            f"/usr/bin/printf '{CHECKSUM}  tool\\n' | /usr/bin/sha256sum -c -",
            items=(
                inventory_item(
                    kind="download",
                    digest_or_sha=f"sha256:{CHECKSUM}",
                    artifact_url=artifact_url,
                ),
            ),
        )

        self.assertEqual(1, result.returncode)
        self.assertIn("official artifact_url", result.stderr)

    def test_rejects_ambiguous_download_inventory(self):
        result = run_download_lines(
            f"/usr/bin/curl -q -fsSL {ARTIFACT_URL} -o tool",
            f"/usr/bin/printf '{CHECKSUM}  tool\\n' | /usr/bin/sha256sum -c -",
            items=(
                inventory_item(
                    name="example-tool-one",
                    kind="download",
                    digest_or_sha=f"sha256:{CHECKSUM}",
                    artifact_url=ARTIFACT_URL,
                ),
                inventory_item(
                    name="example-tool-two",
                    kind="download",
                    digest_or_sha="sha256:" + "e" * 64,
                    artifact_url=ARTIFACT_URL,
                ),
            ),
        )

        self.assertEqual(1, result.returncode)
        self.assertIn("ambiguous", result.stderr)

    def test_rejects_case_alias_download_inventory(self):
        alias_url = ARTIFACT_URL.replace("example/tool", "Example/Tool")
        errors = checker.validate_inventory(
            [
                inventory_item(
                    name="example-tool-one",
                    kind="download",
                    digest_or_sha=f"sha256:{CHECKSUM}",
                    artifact_url=ARTIFACT_URL,
                ),
                inventory_item(
                    name="example-tool-two",
                    kind="download",
                    digest_or_sha="sha256:" + "e" * 64,
                    source="https://github.com/Example/Tool/releases/tag/v1.2.3",
                    artifact_url=alias_url,
                ),
            ],
            datetime(2026, 8, 11, tzinfo=timezone.utc),
        )

        self.assertTrue(any("ambiguous download artifact_url" in error for error in errors))

    def test_rejects_download_artifact_from_a_different_source_release(self):
        mismatches = (
            "https://github.com/other/tool/releases/download/v1.2.3/tool",
            "https://github.com/example/other/releases/download/v1.2.3/tool",
            "https://github.com/example/tool/releases/download/v9.9.9/tool",
        )
        for artifact_url in mismatches:
            with self.subTest(artifact_url=artifact_url):
                errors = checker.validate_inventory(
                    [
                        inventory_item(
                            kind="download",
                            digest_or_sha=f"sha256:{CHECKSUM}",
                            artifact_url=artifact_url,
                        )
                    ],
                    datetime(2026, 8, 11, tzinfo=timezone.utc),
                )

                self.assertTrue(any("source release" in error for error in errors))

    def test_online_check_rejects_a_moved_action_tag(self):
        item = inventory_item(
            name="actions/checkout",
            digest_or_sha="a" * 40,
            source="https://github.com/actions/checkout/releases/tag/v1.2.3",
        )
        release = {
            "tag_name": "v1.2.3",
            "published_at": "2026-01-01T00:00:00Z",
            "draft": False,
            "prerelease": False,
        }
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

    def test_rejects_nuget_source_for_a_different_package_or_version(self):
        sources = (
            "https://api.nuget.org/v3/registration5-gz-semver2/other.package/1.2.3.json",
            "https://api.nuget.org/v3/registration5-gz-semver2/example.package/9.9.9.json",
        )
        for source in sources:
            with self.subTest(source=source):
                errors = checker.validate_inventory(
                    [
                        inventory_item(
                            name="Example.Package",
                            kind="nuget",
                            version="1.2.3",
                            digest_or_sha=NUGET_DIGEST,
                            source=source,
                        )
                    ],
                    datetime(2026, 8, 11, tzinfo=timezone.utc),
                )

                self.assertTrue(any("source" in error for error in errors))

    def test_rejects_noncanonical_or_wrong_length_nuget_hashes(self):
        digests = (
            "sha512-base64:expected",
            "sha512-base64:" + base64.b64encode(b"short").decode("ascii"),
            NUGET_DIGEST.rstrip("="),
        )
        for digest in digests:
            with self.subTest(digest=digest):
                errors = checker.validate_inventory(
                    [
                        inventory_item(
                            name="Example.Package",
                            kind="nuget",
                            digest_or_sha=digest,
                            source="https://api.nuget.org/v3/registration5-gz-semver2/example.package/1.2.3.json",
                        )
                    ],
                    datetime(2026, 8, 11, tzinfo=timezone.utc),
                )

                self.assertTrue(any("sha512-base64" in error for error in errors))

    def test_rejects_missing_required_sdk_and_package_declarations(self):
        for missing in ("global.json", "Directory.Packages.props"):
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                write_inventory(root, dotnet_inventory_item())
                write_required_declarations(root)
                (root / missing).unlink()

                result = run_checker(root)

                self.assertEqual(1, result.returncode)
                self.assertIn(f"{missing} is required", result.stderr)

    def test_rejects_duplicate_json_keys_and_unknown_inventory_fields(self):
        errors = checker.validate_inventory(
            [inventory_item(unexpected="value")],
            datetime(2026, 8, 11, tzinfo=timezone.utc),
        )
        self.assertTrue(any("unknown inventory fields" in error for error in errors))

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_inventory(root, dotnet_inventory_item())
            write_required_declarations(root)
            (root / "global.json").write_text(
                '{"sdk":{"version":"10.0.302","version":"9.9.9"}}\n',
                encoding="utf-8",
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("unable to read", result.stderr)

    def test_malformed_inventory_types_fail_closed_without_crashing(self):
        overrides = ({"kind": []}, {"source": 1}, {"version": []}, {"name": []})
        for override in overrides:
            with self.subTest(override=override):
                errors = checker.validate_inventory(
                    [inventory_item(**override)],
                    datetime(2026, 8, 11, tzinfo=timezone.utc),
                )

                self.assertTrue(errors)

    def test_parses_package_version_attributes_independent_of_order(self):
        package = inventory_item(
            name="Example.Package",
            kind="nuget",
            version="1.2.3",
            digest_or_sha=NUGET_DIGEST,
            source="https://api.nuget.org/v3/registration5-gz-semver2/example.package/1.2.3.json",
        )
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_inventory(root, dotnet_inventory_item(), package)
            write_required_declarations(root)
            (root / "Directory.Packages.props").write_text(
                '<Project><ItemGroup><PackageVersion Version="9.9.9" Include="Example.Package" /></ItemGroup></Project>\n',
                encoding="utf-8",
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("Example.Package differs", result.stderr)

    def test_accepts_matching_package_version_with_reversed_attributes(self):
        package = inventory_item(
            name="Example.Package",
            kind="nuget",
            version="1.2.3",
            digest_or_sha=NUGET_DIGEST,
            source="https://api.nuget.org/v3/registration5-gz-semver2/example.package/1.2.3.json",
        )
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_inventory(root, dotnet_inventory_item(), package)
            write_required_declarations(root)
            (root / "Directory.Packages.props").write_text(
                '<Project><ItemGroup><PackageVersion Version="1.2.3" Include="Example.Package" /></ItemGroup></Project>\n',
                encoding="utf-8",
            )

            result = run_checker(root)

            self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_namespaced_or_conditional_package_versions(self):
        package = inventory_item(
            name="Example.Package",
            kind="nuget",
            version="1.2.3",
            digest_or_sha=NUGET_DIGEST,
            source="https://api.nuget.org/v3/registration5-gz-semver2/example.package/1.2.3.json",
        )
        documents = {
            "foreign namespace": '<Project xmlns:x="urn:evil"><ItemGroup><x:PackageVersion Include="Example.Package" Version="1.2.3" /></ItemGroup></Project>\n',
            "item condition": '<Project><ItemGroup><PackageVersion Include="Example.Package" Version="1.2.3" Condition="false" /></ItemGroup></Project>\n',
            "group condition": '<Project><ItemGroup Condition="false"><PackageVersion Include="Example.Package" Version="1.2.3" /></ItemGroup></Project>\n',
        }
        for name, document in documents.items():
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                write_inventory(root, dotnet_inventory_item(), package)
                write_required_declarations(root)
                (root / "Directory.Packages.props").write_text(document, encoding="utf-8")

                result = run_checker(root)

                self.assertEqual(1, result.returncode)
                self.assertIn("Directory.Packages.props", result.stderr)

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

    def test_inventory_records_the_peeled_helm_release_commit(self):
        inventory = json.loads(
            (REPOSITORY / "config" / "supply-chain.json").read_text(encoding="utf-8")
        )["inventory"]
        helm = next(item for item in inventory if item["name"] == "helm")

        self.assertEqual(
            "43e8b7feece8beb0fcba47059ec9b522fd929a64", helm["digest_or_sha"]
        )

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
            write_download_fixture(
                root,
                inventory_item(
                    kind="download",
                    digest_or_sha=f"sha256:{expected}",
                    artifact_url=artifact_url,
                ),
                f"/usr/bin/curl -q -fsSL {artifact_url} -o tool",
                f"/usr/bin/printf '{'e' * 64}  tool\\n' | /usr/bin/sha256sum -c -",
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("does not bind", result.stderr)

    def test_rejects_download_checksum_command_without_check_mode(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            write_download_fixture(
                root,
                inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url),
                f"/usr/bin/curl -q -fsSL {artifact_url} -o tool",
                f"/usr/bin/printf '{checksum}  tool\\n'; /usr/bin/sha256sum tool",
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("does not bind", result.stderr)

    def test_rejects_commented_checksum_check(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            write_download_fixture(
                root,
                inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url),
                f"/usr/bin/curl -q -fsSL {artifact_url} -o tool",
                f"/usr/bin/printf '{checksum}  tool\\n' # /usr/bin/sha256sum -c -",
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("does not bind", result.stderr)

    def test_rejects_checksum_file_form_as_noncanonical(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            write_download_fixture(
                root,
                inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url),
                f"/usr/bin/curl -q -fsSL {artifact_url} -o tool",
                f"/usr/bin/printf '{checksum}  tool\\n' > tool.sha256",
                "/usr/bin/sha256sum -c tool.sha256",
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("does not bind", result.stderr)

    def test_rejects_checksum_check_from_dev_null(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            write_download_fixture(
                root,
                inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url),
                f"/usr/bin/curl -q -fsSL {artifact_url} -o tool",
                f"/usr/bin/printf '{checksum}  tool\\n'; /usr/bin/sha256sum -c - </dev/null",
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("does not bind", result.stderr)

    def test_rejects_stderr_redirected_checksum_file(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            write_download_fixture(
                root,
                inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url),
                f"/usr/bin/curl -q -fsSL {artifact_url} -o tool",
                f"/usr/bin/printf '{checksum}  tool\\n' 2> tool.sha256",
                "/usr/bin/sha256sum -c tool.sha256",
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("does not bind", result.stderr)

    def test_accepts_checksum_pipeline_to_standard_input(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            write_download_fixture(
                root,
                inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url),
                f"/usr/bin/curl -q -fsSL {artifact_url} -o tool",
                f"/usr/bin/printf '{checksum}  tool\\n' | /usr/bin/sha256sum -c -",
            )

            result = run_checker(root)

            self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_control_sequence_before_checksum_pipeline(self):
        for operator in (";", "&&", "||"):
            with self.subTest(operator=operator), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                checksum = "d" * 64
                artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
                write_download_fixture(
                    root,
                    inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url),
                    f"/usr/bin/curl -q -fsSL {artifact_url} -o tool",
                    f"/usr/bin/printf '{checksum}  tool\\n' {operator} true | /usr/bin/sha256sum -c -",
                )

                result = run_checker(root)

                self.assertEqual(1, result.returncode)
                self.assertIn("does not bind", result.stderr)

    def test_rejects_control_sequence_before_checksum_file(self):
        for operator in (";", "&&", "||"):
            with self.subTest(operator=operator), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                checksum = "d" * 64
                artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
                write_download_fixture(
                    root,
                    inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url),
                    f"/usr/bin/curl -q -fsSL {artifact_url} -o tool",
                    f"/usr/bin/printf '{checksum}  tool\\n' {operator} true > tool.sha256",
                    "/usr/bin/sha256sum -c tool.sha256",
                )

                result = run_checker(root)

                self.assertEqual(1, result.returncode)
                self.assertIn("does not bind", result.stderr)

    def test_rejects_extra_pipe_before_checksum_pipeline(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            write_download_fixture(
                root,
                inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url),
                f"/usr/bin/curl -q -fsSL {artifact_url} -o tool",
                f"/usr/bin/printf '{checksum}  tool\\n' | true | /usr/bin/sha256sum -c -",
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("does not bind", result.stderr)

    def test_rejects_extra_pipe_before_checksum_file(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            write_download_fixture(
                root,
                inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url),
                f"/usr/bin/curl -q -fsSL {artifact_url} -o tool",
                f"/usr/bin/printf '{checksum}  tool\\n' | true > tool.sha256",
                "/usr/bin/sha256sum -c tool.sha256",
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("does not bind", result.stderr)

    def test_rejects_escaped_pipe_before_checksum_pipeline(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            write_download_fixture(
                root,
                inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url),
                f"/usr/bin/curl -q -fsSL {artifact_url} -o tool",
                f"/usr/bin/printf '{checksum}  tool\\n' \\| true | /usr/bin/sha256sum -c -",
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("does not bind", result.stderr)

    def test_rejects_checksum_file_check_with_control_suffix(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            checksum = "d" * 64
            artifact_url = "https://github.com/example/tool/releases/download/v1.2.3/tool"
            write_download_fixture(
                root,
                inventory_item(kind="download", digest_or_sha=f"sha256:{checksum}", artifact_url=artifact_url),
                f"/usr/bin/curl -q -fsSL {artifact_url} -o tool",
                f"/usr/bin/printf '{checksum}  tool\\n' > tool.sha256",
                "/usr/bin/sha256sum -c tool.sha256 && true",
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("does not bind", result.stderr)

    def test_online_requires_typed_github_release_fields(self):
        item = inventory_item(kind="github-release")
        complete = {
            "tag_name": "v1.2.3",
            "published_at": "2026-01-01T00:00:00Z",
            "draft": False,
            "prerelease": False,
        }
        variants = {
            "missing published_at": {key: value for key, value in complete.items() if key != "published_at"},
            "missing draft": {key: value for key, value in complete.items() if key != "draft"},
            "missing prerelease": {key: value for key, value in complete.items() if key != "prerelease"},
            "draft integer": {**complete, "draft": 0},
            "prerelease string": {**complete, "prerelease": "false"},
            "published_at integer": {**complete, "published_at": 0},
        }
        for name, release in variants.items():
            with self.subTest(name=name), patch.object(
                checker,
                "fetch_json",
                side_effect=[(release, {}), ({"sha": item["digest_or_sha"]}, {})],
            ):
                errors = checker.validate_online(
                    [item], datetime(2026, 8, 11, tzinfo=timezone.utc)
                )

                self.assertTrue(any("required" in error or "boolean" in error for error in errors))

    def test_online_rejects_github_release_timestamp_drift(self):
        item = inventory_item(kind="github-release")
        release = {
            "tag_name": "v1.2.3",
            "published_at": "2026-01-02T00:00:00Z",
            "draft": False,
            "prerelease": False,
        }

        with patch.object(
            checker,
            "fetch_json",
            side_effect=[(release, {}), ({"sha": item["digest_or_sha"]}, {})],
        ):
            errors = checker.validate_online(
                [item], datetime(2026, 8, 11, tzinfo=timezone.utc)
            )

        self.assertTrue(any("release timestamp" in error for error in errors))

    def test_online_requires_complete_nuget_identity_status_hash_and_timestamp(self):
        item = inventory_item(
            name="Example.Package",
            kind="nuget",
            version="1.2.3",
            digest_or_sha=NUGET_DIGEST,
            released_at="2026-01-01T00:00:00Z",
            source="https://api.nuget.org/v3/registration5-gz-semver2/example.package/1.2.3.json",
        )
        catalog = {
            "id": "Example.Package",
            "version": "1.2.3",
            "listed": True,
            "published": "2026-01-01T00:00:00Z",
            "packageHash": NUGET_HASH,
            "packageHashAlgorithm": "SHA512",
        }
        variants = {
            "registration listed missing": ({"published": "2026-01-01T00:00:00Z", "catalogEntry": catalog}, "listed"),
            "catalog id missing": ({"listed": True, "published": "2026-01-01T00:00:00Z", "catalogEntry": {key: value for key, value in catalog.items() if key != "id"}}, "identity"),
            "catalog id differs": ({"listed": True, "published": "2026-01-01T00:00:00Z", "catalogEntry": {**catalog, "id": "Other.Package"}}, "identity"),
            "catalog listed missing": ({"listed": True, "published": "2026-01-01T00:00:00Z", "catalogEntry": {key: value for key, value in catalog.items() if key != "listed"}}, "listed"),
            "catalog hash missing": ({"listed": True, "published": "2026-01-01T00:00:00Z", "catalogEntry": {key: value for key, value in catalog.items() if key != "packageHash"}}, "checksum"),
            "catalog timestamp missing": ({"listed": True, "published": "2026-01-01T00:00:00Z", "catalogEntry": {key: value for key, value in catalog.items() if key != "published"}}, "timestamp"),
        }
        for name, (payload, diagnostic) in variants.items():
            with self.subTest(name=name), patch.object(checker, "fetch_json", return_value=(payload, {})):
                errors = checker.validate_online(
                    [item], datetime(2026, 8, 11, tzinfo=timezone.utc)
                )

                self.assertTrue(any(diagnostic in error for error in errors), errors)

    def test_online_accepts_a_complete_nuget_record(self):
        item = inventory_item(
            name="Example.Package",
            kind="nuget",
            version="1.2.3",
            digest_or_sha=NUGET_DIGEST,
            released_at="2026-01-01T00:00:00Z",
            source="https://api.nuget.org/v3/registration5-gz-semver2/example.package/1.2.3.json",
        )
        payload = {
            "listed": True,
            "published": "2026-01-01T00:00:00Z",
            "catalogEntry": {
                "id": "example.package",
                "version": "1.2.3",
                "listed": True,
                "published": "2026-01-01T00:00:00Z",
                "packageHash": NUGET_HASH,
                "packageHashAlgorithm": "SHA512",
            },
        }

        with patch.object(checker, "fetch_json", return_value=(payload, {})):
            errors = checker.validate_online(
                [item], datetime(2026, 8, 11, tzinfo=timezone.utc)
            )

        self.assertEqual([], errors)

    def test_online_validates_container_date_against_dotnet_metadata(self):
        item = inventory_item(
            name="mcr.microsoft.com/dotnet/sdk",
            kind="container",
            version="10.0.302-noble-aot",
            digest_or_sha="sha256:" + "a" * 64,
            released_at="2026-07-14T00:00:00Z",
            source="https://mcr.microsoft.com/v2/dotnet/sdk/manifests/10.0.302-noble-aot",
        )
        payload = {
            "releases": [
                {
                    "release-date": "2026-07-15",
                    "release-version": "10.0.10",
                    "sdk": {"version": "10.0.302"},
                    "runtime": {"version": "10.0.10"},
                }
            ]
        }

        with patch.object(checker, "fetch_manifest_digest", return_value=item["digest_or_sha"]), patch.object(
            checker, "fetch_json", return_value=(payload, {})
        ):
            errors = checker.validate_online(
                [item], datetime(2026, 8, 11, tzinfo=timezone.utc)
            )

        self.assertTrue(any("release date" in error for error in errors))

    def test_online_fetches_dotnet_metadata_once_for_all_containers(self):
        sdk = inventory_item(
            name="mcr.microsoft.com/dotnet/sdk",
            kind="container",
            version="10.0.302-noble-aot",
            digest_or_sha="sha256:" + "a" * 64,
            released_at="2026-07-14T00:00:00Z",
            source="https://mcr.microsoft.com/v2/dotnet/sdk/manifests/10.0.302-noble-aot",
        )
        runtime = inventory_item(
            name="mcr.microsoft.com/dotnet/runtime-deps",
            kind="container",
            version="10.0.10-noble-chiseled-extra",
            digest_or_sha="sha256:" + "b" * 64,
            released_at="2026-07-14T00:00:00Z",
            source="https://mcr.microsoft.com/v2/dotnet/runtime-deps/manifests/10.0.10-noble-chiseled-extra",
        )
        payload = {
            "releases": [
                {
                    "release-date": "2026-07-14",
                    "release-version": "10.0.10",
                    "sdk": {"version": "10.0.302"},
                    "runtime": {"version": "10.0.10"},
                }
            ]
        }

        with patch.object(
            checker, "fetch_manifest_digest", side_effect=[sdk["digest_or_sha"], runtime["digest_or_sha"]]
        ), patch.object(checker, "fetch_json", return_value=(payload, {})) as fetch:
            errors = checker.validate_online(
                [sdk, runtime], datetime(2026, 8, 11, tzinfo=timezone.utc)
            )

        self.assertEqual([], errors)
        fetch.assert_called_once_with(checker.DOTNET_RELEASES)

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
            digest_or_sha=NUGET_DIGEST,
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
            "draft": False,
            "prerelease": False,
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

        with patch.object(checker, "fetch_manifest_digest", return_value=None), patch.object(
            checker, "fetch_json", return_value=({"releases": []}, {})
        ):
            errors = checker.validate_online([item], datetime(2026, 8, 11, tzinfo=timezone.utc))

        self.assertTrue(any("did not provide Docker-Content-Digest" in error for error in errors))


if __name__ == "__main__":
    unittest.main()
