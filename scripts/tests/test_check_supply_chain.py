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
NUGET_LOCK_HASH = base64.b64encode(b"l" * 64).decode("ascii")
ILCOMPILER_LOCK_HASH = "tnG8ntt/Bk6odvHREnGLMo3PEiihy5iSlIFVp0JbIo00GKtNRt2k73eKZbPqR5yaJNIa3z8R86YLwbxfqpb17g=="
ILLINK_LOCK_HASH = "f5VCIE7AJpd5YvzNTeMGVzQIgyE9tX+AreTYwQF+REbu+DZo/2Ae+jNSwhPEYrVz6RRkd7y8ubXjk6Nn6Ka+Cg=="
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
        released_at="2026-07-14",
        source=checker.DOTNET_RELEASES,
        artifact_url="https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.302/dotnet-sdk-10.0.302-linux-x64.tar.gz",
    )


def nuget_inventory_item(name="Example.Package", version="1.2.3", lock_hash=NUGET_LOCK_HASH):
    return inventory_item(
        name=name,
        kind="nuget",
        version=version,
        digest_or_sha=NUGET_DIGEST,
        source=(
            "https://api.nuget.org/v3/registration5-gz-semver2/"
            f"{name.casefold()}/{version}.json"
        ),
        lock_content_hash=lock_hash,
    )


def sdk_aot_inventory_items():
    return (
        inventory_item(
            name="Microsoft.DotNet.ILCompiler",
            kind="nuget",
            version="10.0.10",
            digest_or_sha="sha512-base64:Ne9wklPZQTe7T49oaGGqsdkiNgMApx9BPV4+pqw2DMp0KPCvxUJ1x2NIYNKjUJjdpAQIdV4HuOUJlNqyh4nNfA==",
            lock_content_hash=ILCOMPILER_LOCK_HASH,
            released_at="2026-07-14T17:00:57.217Z",
            source="https://api.nuget.org/v3/registration5-gz-semver2/microsoft.dotnet.ilcompiler/10.0.10.json",
            nuget_role="sdk-aot-base-rid",
        ),
        inventory_item(
            name="Microsoft.NET.ILLink.Tasks",
            kind="nuget",
            version="10.0.10",
            digest_or_sha="sha512-base64:gE8O7DrRAI3Qir3ySzvdRl7DzVf8XrFfI0vbUXl2GHim3dMPdVol9DxwNh/Tzq9ymok1KU+2wu2qrF5jWNv1pQ==",
            lock_content_hash=ILLINK_LOCK_HASH,
            released_at="2026-07-14T17:00:44.047Z",
            source="https://api.nuget.org/v3/registration5-gz-semver2/microsoft.net.illink.tasks/10.0.10.json",
            nuget_role="sdk-aot-base",
        ),
    )


def write_required_declarations(root, packages=()):
    (root / "global.json").write_text(
        json.dumps(
            {
                "sdk": {
                    "version": "10.0.302",
                    "rollForward": "disable",
                    "allowPrerelease": False,
                }
            }
        ),
        encoding="utf-8",
    )
    package_lines = "\n".join(
        f'    <PackageVersion Include="{name}" Version="{version}" />'
        for name, version in packages
    )
    (root / "Directory.Packages.props").write_text(
        "<Project>\n"
        "  <PropertyGroup>\n"
        "    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n"
        "    <CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled>\n"
        "  </PropertyGroup>\n"
        f"  <ItemGroup>\n{package_lines}\n  </ItemGroup>\n"
        "</Project>\n",
        encoding="utf-8",
    )
    (root / "NuGet.Config").write_text(
        '<?xml version="1.0" encoding="utf-8"?>\n'
        "<configuration>\n"
        "  <packageSources>\n"
        "    <clear />\n"
        '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />\n'
        "  </packageSources>\n"
        "</configuration>\n",
        encoding="utf-8",
    )


def write_nuget_project(root, packages, lock_dependencies=None):
    project = root / "App" / "App.csproj"
    project.parent.mkdir(parents=True)
    package_references = "\n".join(
        f'    <PackageReference Include="{name}" />' for name, _, _ in packages
    )
    project.write_text(
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n"
        "  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>\n"
        f"  <ItemGroup>\n{package_references}\n  </ItemGroup>\n"
        "</Project>\n",
        encoding="utf-8",
    )
    if lock_dependencies is None:
        lock_dependencies = {
            name: {
                "type": "Direct",
                "requested": f"[{version}, )",
                "resolved": version,
                "contentHash": lock_hash,
            }
            for name, version, lock_hash in packages
        }
    (project.parent / "packages.lock.json").write_text(
        json.dumps(
            {
                "version": 2,
                "dependencies": {"net10.0": lock_dependencies},
            }
        ),
        encoding="utf-8",
    )


def write_aot_project(
    root,
    compiler_version="10.0.10",
    compiler_hash=ILCOMPILER_LOCK_HASH,
    linker_version="10.0.10",
    linker_hash=ILLINK_LOCK_HASH,
):
    project = root / "App" / "App.csproj"
    project.parent.mkdir(parents=True)
    project.write_text(
        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
        "<TargetFramework>net10.0</TargetFramework>"
        "<PublishAot>true</PublishAot>"
        "<RuntimeIdentifiers>linux-x64</RuntimeIdentifiers>"
        "</PropertyGroup></Project>\n",
        encoding="utf-8",
    )

    def direct(version, content_hash):
        return {
            "type": "Direct",
            "requested": f"[{version}, )",
            "resolved": version,
            "contentHash": content_hash,
        }

    (project.parent / "packages.lock.json").write_text(
        json.dumps(
            {
                "version": 2,
                "dependencies": {
                    "net10.0": {
                        "Microsoft.DotNet.ILCompiler": direct(
                            compiler_version, compiler_hash
                        ),
                        "Microsoft.NET.ILLink.Tasks": direct(
                            linker_version, linker_hash
                        ),
                    },
                    "net10.0/linux-x64": {
                        "Microsoft.DotNet.ILCompiler": direct(
                            compiler_version, compiler_hash
                        )
                    },
                },
            }
        ),
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

    def test_inventory_uses_a_conservative_bound_for_date_only_sources(self):
        items = {
            "container": inventory_item(
                name="mcr.microsoft.com/dotnet/runtime-deps",
                kind="container",
                version="10.0.10-noble-chiseled-extra",
                digest_or_sha="sha256:" + "a" * 64,
                released_at="2026-08-01",
                source="https://mcr.microsoft.com/v2/dotnet/runtime-deps/manifests/10.0.10-noble-chiseled-extra",
            ),
            "SDK": {
                **dotnet_inventory_item(),
                "released_at": "2026-08-01",
            },
        }
        for kind, item in items.items():
            with self.subTest(kind=kind, age="73 hours"):
                errors = checker.validate_inventory(
                    [item], datetime(2026, 8, 4, 1, tzinfo=timezone.utc)
                )

                self.assertTrue(any("72 hours" in error for error in errors), errors)
            with self.subTest(kind=kind, age="96 hours"):
                errors = checker.validate_inventory(
                    [item], datetime(2026, 8, 5, tzinfo=timezone.utc)
                )

                self.assertFalse(any("72 hours" in error for error in errors), errors)

    def test_inventory_uses_released_at_precision_at_offline_boundaries(self):
        precise = inventory_item(
            name="mcr.microsoft.com/dotnet/runtime-deps",
            kind="container",
            version="10.0.10-noble-chiseled-extra",
            digest_or_sha="sha256:" + "a" * 64,
            released_at="2026-08-01T12:00:00Z",
            source="https://mcr.microsoft.com/v2/dotnet/runtime-deps/manifests/10.0.10-noble-chiseled-extra",
        )
        date_only = {**precise, "released_at": "2026-08-01"}
        cases = (
            (
                "precise just before 72 hours",
                precise,
                datetime(2026, 8, 4, 11, 59, 59, tzinfo=timezone.utc),
                True,
            ),
            (
                "precise exactly 72 hours",
                precise,
                datetime(2026, 8, 4, 12, tzinfo=timezone.utc),
                False,
            ),
            (
                "precise just after 72 hours",
                precise,
                datetime(2026, 8, 4, 12, 0, 1, tzinfo=timezone.utc),
                False,
            ),
            (
                "date only 73 hours after midnight",
                date_only,
                datetime(2026, 8, 4, 1, tzinfo=timezone.utc),
                True,
            ),
            (
                "date only exactly 96 hours after midnight",
                date_only,
                datetime(2026, 8, 5, tzinfo=timezone.utc),
                False,
            ),
        )
        for name, item, now, rejected in cases:
            with self.subTest(name=name):
                errors = checker.validate_inventory([item], now)

                self.assertEqual(
                    rejected,
                    any("72 hours" in error for error in errors),
                    errors,
                )
                if not rejected:
                    self.assertEqual([], errors)

    def test_rfc3339_offsets_identify_the_same_exact_instant(self):
        expected = checker.parse_timestamp("2026-01-01T00:00:00.1234561Z")

        for value in (
            "2025-12-31T19:00:00.123456100-05:00",
            "2026-01-01T01:00:00.1234561+01:00",
        ):
            with self.subTest(value=value):
                self.assertEqual(expected, checker.parse_timestamp(value))

    def test_rfc3339_fraction_beyond_microseconds_detects_drift(self):
        self.assertNotEqual(
            checker.parse_timestamp("2026-01-01T00:00:00.1234561Z"),
            checker.parse_timestamp("2026-01-01T00:00:00.1234569Z"),
        )

    def test_rejects_values_outside_the_official_rfc3339_subset(self):
        values = (
            None,
            True,
            1,
            [],
            {},
            "2026-01-01 00:00:00Z",
            "2026-01-01T00:00:00z",
            "2026-01-01T00:00:00+0100",
            "2026-01-01T00:00:00.1234567890Z",
        )
        for value in values:
            with self.subTest(value=value), self.assertRaises(ValueError):
                checker.parse_timestamp(value)

    def test_inventory_preserves_submicrosecond_stabilization_boundaries(self):
        cases = (
            (
                "one tenth of a microsecond early",
                "2026-08-01T12:00:00.1234561Z",
                datetime(2026, 8, 4, 12, 0, 0, 123456, tzinfo=timezone.utc),
                True,
            ),
            (
                "exact boundary through an equivalent offset",
                "2026-08-01T13:00:00.1234560+01:00",
                datetime(2026, 8, 4, 12, 0, 0, 123456, tzinfo=timezone.utc),
                False,
            ),
            (
                "one tenth of a microsecond after",
                "2026-08-01T12:00:00.1234569Z",
                datetime(2026, 8, 4, 12, 0, 0, 123457, tzinfo=timezone.utc),
                False,
            ),
        )
        for name, released_at, now, rejected in cases:
            with self.subTest(name=name):
                errors = checker.validate_inventory(
                    [inventory_item(released_at=released_at)], now
                )

                self.assertEqual(
                    rejected,
                    any("72 hours" in error for error in errors),
                    errors,
                )
                if not rejected:
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

    def test_requires_a_stable_global_json_sdk_policy(self):
        policies = {
            "missing roll forward": {"version": "10.0.302", "allowPrerelease": False},
            "floating roll forward": {
                "version": "10.0.302",
                "rollForward": "latestMajor",
                "allowPrerelease": False,
            },
            "missing prerelease policy": {
                "version": "10.0.302",
                "rollForward": "disable",
            },
            "prerelease enabled": {
                "version": "10.0.302",
                "rollForward": "disable",
                "allowPrerelease": True,
            },
            "string boolean": {
                "version": "10.0.302",
                "rollForward": "disable",
                "allowPrerelease": "false",
            },
        }
        for name, sdk in policies.items():
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                write_inventory(root, dotnet_inventory_item())
                write_required_declarations(root)
                (root / "global.json").write_text(
                    json.dumps({"sdk": sdk}), encoding="utf-8"
                )

                result = run_checker(root)

                self.assertEqual(1, result.returncode)
                self.assertIn("global.json", result.stderr)

    def test_requires_central_package_management_without_version_overrides(self):
        properties = {
            "missing central management": (
                "<CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled>"
            ),
            "central management disabled": (
                "<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>"
                "<CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled>"
            ),
            "missing override policy": (
                "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>"
            ),
            "overrides enabled": (
                "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>"
                "<CentralPackageVersionOverrideEnabled>true</CentralPackageVersionOverrideEnabled>"
            ),
        }
        for name, body in properties.items():
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                write_inventory(root, dotnet_inventory_item())
                write_required_declarations(root)
                (root / "Directory.Packages.props").write_text(
                    f"<Project><PropertyGroup>{body}</PropertyGroup><ItemGroup /></Project>\n",
                    encoding="utf-8",
                )

                result = run_checker(root)

                self.assertEqual(1, result.returncode)
                self.assertIn("Directory.Packages.props", result.stderr)

    def test_rejects_local_package_reference_versions_and_overrides(self):
        declarations = {
            "version attribute": '<PackageReference Include="Example.Package" Version="1.2.3" />',
            "version child": '<PackageReference Include="Example.Package"><Version>1.2.3</Version></PackageReference>',
            "override attribute": '<PackageReference Include="Example.Package" VersionOverride="1.2.3" />',
            "override child": '<PackageReference Include="Example.Package"><VersionOverride>1.2.3</VersionOverride></PackageReference>',
        }
        package = nuget_inventory_item()
        for name, declaration in declarations.items():
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                write_inventory(root, dotnet_inventory_item(), package)
                write_required_declarations(root, ((package["name"], package["version"]),))
                write_nuget_project(
                    root,
                    ((package["name"], package["version"], package["lock_content_hash"]),),
                )
                (root / "App" / "App.csproj").write_text(
                    "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                    "<TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                    f"<ItemGroup>{declaration}</ItemGroup></Project>\n",
                    encoding="utf-8",
                )

                result = run_checker(root)

                self.assertEqual(1, result.returncode)
                self.assertIn("PackageReference", result.stderr)

    def test_rejects_case_insensitive_msbuild_package_versions(self):
        package = nuget_inventory_item()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_inventory(root, dotnet_inventory_item(), package)
            write_required_declarations(root, ((package["name"], package["version"]),))
            write_nuget_project(
                root,
                ((package["name"], package["version"], package["lock_content_hash"]),),
            )
            (root / "App" / "App.csproj").write_text(
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>"
                "<TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup>"
                '<PackageReference Include="Example.Package" />'
                '<packagereference include="Example.Package" version="9.9.9" />'
                "</ItemGroup></Project>\n",
                encoding="utf-8",
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("versionless", result.stderr)

    def test_rejects_namespaced_msbuild_identity_attributes(self):
        package = nuget_inventory_item()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_inventory(root, dotnet_inventory_item(), package)
            write_required_declarations(root, ((package["name"], package["version"]),))
            write_nuget_project(
                root,
                ((package["name"], package["version"], package["lock_content_hash"]),),
            )
            (root / "App" / "App.csproj").write_text(
                '<Project Sdk="Microsoft.NET.Sdk" xmlns:x="urn:foreign">'
                "<PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                '<ItemGroup><PackageReference x:Include="Example.Package" /></ItemGroup>'
                "</Project>\n",
                encoding="utf-8",
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("canonical", result.stderr)

    def test_rejects_explicit_msbuild_imports_without_scanning_unimported_xml(self):
        package = nuget_inventory_item()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_inventory(root, dotnet_inventory_item(), package)
            write_required_declarations(root, ((package["name"], package["version"]),))
            write_nuget_project(
                root,
                ((package["name"], package["version"], package["lock_content_hash"]),),
            )
            (root / "restore-policy.xml").write_text(
                "<Project><PropertyGroup>"
                "<RestoreSources>https://mirror.example/v3/index.json</RestoreSources>"
                "</PropertyGroup></Project>\n",
                encoding="utf-8",
            )
            project = root / "App" / "App.csproj"
            project.write_text(
                project.read_text(encoding="utf-8").replace(
                    "</Project>", '<Import Project="../restore-policy.xml" /></Project>'
                ),
                encoding="utf-8",
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("Import", result.stderr)

    def test_rejects_package_references_and_restore_sources_from_props_files(self):
        package = nuget_inventory_item()
        declarations = {
            "shared package reference": ("Directory.Build.props", (
                '<ItemGroup><PackageReference Include="Example.Package" /></ItemGroup>'
            )),
            "restore source override": ("Directory.Build.props", (
                "<PropertyGroup><RestoreSources>https://mirror.example/v3/index.json</RestoreSources></PropertyGroup>"
            )),
            "restore config override": ("Directory.Build.props", (
                "<PropertyGroup><RestoreConfigFile>mirror.config</RestoreConfigFile></PropertyGroup>"
            )),
            "targets source override": ("Directory.Build.targets", (
                "<PropertyGroup><RestoreSources>https://mirror.example/v3/index.json</RestoreSources></PropertyGroup>"
            )),
            "central management override": ("Directory.Build.targets", (
                "<PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup>"
            )),
            "central version override": ("Directory.Build.props", (
                "<PropertyGroup><CentralPackageVersionOverrideEnabled>true</CentralPackageVersionOverrideEnabled></PropertyGroup>"
            )),
        }
        for name, (filename, declaration) in declarations.items():
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                write_inventory(root, dotnet_inventory_item(), package)
                write_required_declarations(root, ((package["name"], package["version"]),))
                write_nuget_project(
                    root,
                    ((package["name"], package["version"], package["lock_content_hash"]),),
                )
                (root / filename).write_text(
                    f"<Project>{declaration}</Project>\n", encoding="utf-8"
                )

                result = run_checker(root)

                self.assertEqual(1, result.returncode)
                self.assertIn(filename, result.stderr)

    def test_requires_a_single_canonical_nuget_source(self):
        documents = {
            "missing": None,
            "mirror": (
                "<configuration><packageSources><clear />"
                '<add key="mirror" value="https://mirror.example/v3/index.json" />'
                "</packageSources></configuration>"
            ),
            "extra source": (
                "<configuration><packageSources><clear />"
                '<add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />'
                '<add key="mirror" value="https://mirror.example/v3/index.json" />'
                "</packageSources></configuration>"
            ),
            "source mapping": (
                "<configuration><packageSources><clear />"
                '<add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />'
                "</packageSources><packageSourceMapping /></configuration>"
            ),
            "fallback folder": (
                "<configuration><packageSources><clear />"
                '<add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />'
                "</packageSources><fallbackPackageFolders /></configuration>"
            ),
        }
        for name, document in documents.items():
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                write_inventory(root, dotnet_inventory_item())
                write_required_declarations(root)
                config = root / "NuGet.Config"
                if document is None:
                    config.unlink()
                else:
                    config.write_text(document, encoding="utf-8")

                result = run_checker(root)

                self.assertEqual(1, result.returncode)
                self.assertIn("NuGet.Config", result.stderr)

    def test_rejects_nested_nuget_configuration(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_inventory(root, dotnet_inventory_item())
            write_required_declarations(root)
            nested = root / "App" / "NuGet.Config"
            nested.parent.mkdir()
            nested.write_text(
                "<configuration><packageSources><clear />"
                '<add key="mirror" value="https://mirror.example/v3/index.json" />'
                "</packageSources></configuration>",
                encoding="utf-8",
            )

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("NuGet.Config", result.stderr)

    def test_rejects_missing_or_drifting_direct_lock_entries(self):
        package = nuget_inventory_item()
        valid = {
            package["name"]: {
                "type": "Direct",
                "requested": f"[{package['version']}, )",
                "resolved": package["version"],
                "contentHash": package["lock_content_hash"],
            }
        }
        variants = {
            "missing direct": {},
            "wrong role": {
                package["name"]: {**valid[package["name"]], "type": "Transitive"}
            },
            "requested drift": {
                package["name"]: {**valid[package["name"]], "requested": "[9.9.9, )"}
            },
            "resolved drift": {
                package["name"]: {**valid[package["name"]], "resolved": "9.9.9"}
            },
            "hash drift": {
                package["name"]: {
                    **valid[package["name"]],
                    "contentHash": base64.b64encode(b"x" * 64).decode("ascii"),
                }
            },
        }
        for name, dependencies in variants.items():
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                write_inventory(root, dotnet_inventory_item(), package)
                write_required_declarations(root, ((package["name"], package["version"]),))
                write_nuget_project(
                    root,
                    ((package["name"], package["version"], package["lock_content_hash"]),),
                    dependencies,
                )

                result = run_checker(root)

                self.assertEqual(1, result.returncode)
                self.assertIn("packages.lock.json", result.stderr)

    def test_rejects_missing_lock_and_extra_direct_lock_dependency(self):
        package = nuget_inventory_item()
        cases = ("missing lock", "extra direct")
        for case in cases:
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                write_inventory(root, dotnet_inventory_item(), package)
                write_required_declarations(root, ((package["name"], package["version"]),))
                write_nuget_project(
                    root,
                    ((package["name"], package["version"], package["lock_content_hash"]),),
                )
                lock_path = root / "App" / "packages.lock.json"
                if case == "missing lock":
                    lock_path.unlink()
                else:
                    payload = json.loads(lock_path.read_text(encoding="utf-8"))
                    payload["dependencies"]["net10.0"]["Other.Package"] = {
                        "type": "Direct",
                        "requested": "[4.5.6, )",
                        "resolved": "4.5.6",
                        "contentHash": base64.b64encode(b"o" * 64).decode("ascii"),
                    }
                    lock_path.write_text(json.dumps(payload), encoding="utf-8")

                result = run_checker(root)

                self.assertEqual(1, result.returncode)
                self.assertIn("packages.lock.json", result.stderr)

    def test_accepts_locked_direct_and_uninventoried_transitive_packages(self):
        package = nuget_inventory_item()
        dependencies = {
            package["name"]: {
                "type": "Direct",
                "requested": f"[{package['version']}, )",
                "resolved": package["version"],
                "contentHash": package["lock_content_hash"],
            },
            "Other.Transitive": {
                "type": "Transitive",
                "resolved": "4.5.6",
                "contentHash": base64.b64encode(b"t" * 64).decode("ascii"),
            },
        }
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_inventory(root, dotnet_inventory_item(), package)
            write_required_declarations(root, ((package["name"], package["version"]),))
            write_nuget_project(
                root,
                ((package["name"], package["version"], package["lock_content_hash"]),),
                dependencies,
            )

            result = run_checker(root)

            self.assertEqual(0, result.returncode, result.stderr)

    def test_requires_inventory_for_sdk_aot_direct_locks(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_inventory(root, dotnet_inventory_item())
            write_required_declarations(root)
            write_aot_project(root)

            result = run_checker(root)

            self.assertEqual(1, result.returncode)
            self.assertIn("unexpected direct dependency", result.stderr)

    def test_accepts_inventory_bound_sdk_aot_direct_locks(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_inventory(root, dotnet_inventory_item(), *sdk_aot_inventory_items())
            write_required_declarations(root)
            write_aot_project(root)

            result = run_checker(root)

            self.assertEqual(0, result.returncode, result.stderr)

    def test_rejects_sdk_aot_lock_version_and_hash_drift(self):
        drift = {
            "version": {"compiler_version": "9.9.9"},
            "hash": {
                "compiler_hash": base64.b64encode(b"x" * 64).decode("ascii")
            },
        }
        for name, overrides in drift.items():
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                write_inventory(
                    root, dotnet_inventory_item(), *sdk_aot_inventory_items()
                )
                write_required_declarations(root)
                write_aot_project(root, **overrides)

                result = run_checker(root)

                self.assertEqual(1, result.returncode)
                self.assertIn("packages.lock.json", result.stderr)

    def test_rejects_sdk_aot_inventory_version_and_hash_drift(self):
        for field, value in (
            ("version", "9.9.9"),
            ("lock_content_hash", base64.b64encode(b"x" * 64).decode("ascii")),
        ):
            with self.subTest(field=field), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                compiler, linker = sdk_aot_inventory_items()
                compiler[field] = value
                if field == "version":
                    compiler["source"] = (
                        "https://api.nuget.org/v3/registration5-gz-semver2/"
                        "microsoft.dotnet.ilcompiler/9.9.9.json"
                    )
                write_inventory(root, dotnet_inventory_item(), compiler, linker)
                write_required_declarations(root)
                write_aot_project(root)

                result = run_checker(root)

                self.assertEqual(1, result.returncode)
                self.assertIn("packages.lock.json", result.stderr)

    def test_malformed_nuget_roles_fail_closed_without_crashing(self):
        for role in ([], {}, 1, True, None, "SDK-AOT-BASE"):
            with self.subTest(role=role), tempfile.TemporaryDirectory() as directory:
                item = sdk_aot_inventory_items()[0]
                item["nuget_role"] = role
                root = Path(directory)
                write_inventory(root, dotnet_inventory_item(), item)
                write_required_declarations(root)

                result = run_checker(root)

                self.assertEqual(1, result.returncode)
                self.assertIn("unknown NuGet role", result.stderr)
                self.assertNotIn("Traceback", result.stderr)

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
            self.assertIn("global.json", result.stderr)

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
        package = nuget_inventory_item()
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_inventory(root, dotnet_inventory_item(), package)
            write_required_declarations(root, ((package["name"], package["version"]),))
            write_nuget_project(
                root,
                ((package["name"], package["version"], package["lock_content_hash"]),),
            )
            (root / "Directory.Packages.props").write_text(
                "<Project><PropertyGroup>"
                "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>"
                "<CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled>"
                "</PropertyGroup><ItemGroup>"
                '<PackageVersion Version="1.2.3" Include="Example.Package" />'
                "</ItemGroup></Project>\n",
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

    def test_online_rejects_github_timestamp_drift_within_a_second(self):
        item = inventory_item(
            kind="github-release", released_at="2026-01-01T00:00:00.100Z"
        )
        release = {
            "tag_name": "v1.2.3",
            "published_at": "2026-01-01T00:00:00.900Z",
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

        self.assertTrue(any("release timestamp" in error for error in errors), errors)

    def test_online_rejects_github_timestamp_drift_beyond_microseconds(self):
        item = inventory_item(
            kind="github-release", released_at="2026-01-01T00:00:00.1234561Z"
        )
        release = {
            "tag_name": "v1.2.3",
            "published_at": "2026-01-01T00:00:00.1234569Z",
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

        self.assertTrue(any("release timestamp" in error for error in errors), errors)

    def test_online_accepts_equivalent_rfc3339_offset_beyond_microseconds(self):
        item = inventory_item(
            kind="github-release", released_at="2026-01-01T00:00:00.1234561Z"
        )
        release = {
            "tag_name": "v1.2.3",
            "published_at": "2025-12-31T19:00:00.123456100-05:00",
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

        self.assertEqual([], errors)

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

    def test_online_rejects_nuget_timestamp_drift_within_a_second(self):
        item = inventory_item(
            name="Example.Package",
            kind="nuget",
            version="1.2.3",
            digest_or_sha=NUGET_DIGEST,
            released_at="2026-01-01T00:00:00.100Z",
            source="https://api.nuget.org/v3/registration5-gz-semver2/example.package/1.2.3.json",
        )
        payload = {
            "listed": True,
            "published": "2026-01-01T00:00:00.900Z",
            "catalogEntry": {
                "id": "example.package",
                "version": "1.2.3",
                "listed": True,
                "published": "2026-01-01T00:00:00.900Z",
                "packageHash": NUGET_HASH,
                "packageHashAlgorithm": "SHA512",
            },
        }

        with patch.object(checker, "fetch_json", return_value=(payload, {})):
            errors = checker.validate_online(
                [item], datetime(2026, 8, 11, tzinfo=timezone.utc)
            )

        self.assertTrue(any("release timestamp" in error for error in errors), errors)

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

    def test_online_rejects_container_release_precision_drift(self):
        item = inventory_item(
            name="mcr.microsoft.com/dotnet/runtime-deps",
            kind="container",
            version="10.0.10-noble-chiseled-extra",
            digest_or_sha="sha256:" + "a" * 64,
            released_at="2026-08-01T00:00:00Z",
            source="https://mcr.microsoft.com/v2/dotnet/runtime-deps/manifests/10.0.10-noble-chiseled-extra",
        )
        payload = {
            "releases": [
                {
                    "release-date": "2026-08-01",
                    "release-version": "10.0.10",
                    "runtime": {"version": "10.0.10"},
                }
            ]
        }

        with patch.object(
            checker, "fetch_manifest_digest", return_value=item["digest_or_sha"]
        ), patch.object(checker, "fetch_json", return_value=(payload, {})):
            errors = checker.validate_online(
                [item], datetime(2026, 8, 11, tzinfo=timezone.utc)
            )

        self.assertTrue(any("release date" in error for error in errors), errors)

    def test_online_rejects_precise_container_timestamp_drift_within_a_second(self):
        item = inventory_item(
            name="mcr.microsoft.com/dotnet/runtime-deps",
            kind="container",
            version="10.0.10-noble-chiseled-extra",
            digest_or_sha="sha256:" + "a" * 64,
            released_at="2026-08-01T12:34:56.100Z",
            source="https://mcr.microsoft.com/v2/dotnet/runtime-deps/manifests/10.0.10-noble-chiseled-extra",
        )
        payload = {
            "releases": [
                {
                    "release-date": "2026-08-01T12:34:56.900Z",
                    "release-version": "10.0.10",
                    "runtime": {"version": "10.0.10"},
                }
            ]
        }

        with patch.object(
            checker, "fetch_manifest_digest", return_value=item["digest_or_sha"]
        ), patch.object(checker, "fetch_json", return_value=(payload, {})):
            errors = checker.validate_online(
                [item], datetime(2026, 8, 11, tzinfo=timezone.utc)
            )

        self.assertTrue(any("release date" in error for error in errors), errors)

    def test_online_uses_a_conservative_stabilization_bound_for_date_only_metadata(self):
        item = inventory_item(
            name="mcr.microsoft.com/dotnet/runtime-deps",
            kind="container",
            version="10.0.10-noble-chiseled-extra",
            digest_or_sha="sha256:" + "a" * 64,
            released_at="2026-08-01",
            source="https://mcr.microsoft.com/v2/dotnet/runtime-deps/manifests/10.0.10-noble-chiseled-extra",
        )
        payload = {
            "releases": [
                {
                    "release-date": "2026-08-01",
                    "release-version": "10.0.10",
                    "runtime": {"version": "10.0.10"},
                }
            ]
        }
        expectations = {
            "73 hours after midnight": (
                datetime(2026, 8, 4, 1, tzinfo=timezone.utc),
                True,
            ),
            "96 hours after midnight": (
                datetime(2026, 8, 5, tzinfo=timezone.utc),
                False,
            ),
        }
        for name, (now, rejected) in expectations.items():
            with self.subTest(name=name), patch.object(
                checker, "fetch_manifest_digest", return_value=item["digest_or_sha"]
            ), patch.object(checker, "fetch_json", return_value=(payload, {})):
                errors = checker.validate_online([item], now)

                self.assertEqual(
                    rejected,
                    any("newer than 72 hours" in error for error in errors),
                    errors,
                )

    def test_online_uses_an_exact_stabilization_bound_for_precise_container_metadata(self):
        item = inventory_item(
            name="mcr.microsoft.com/dotnet/runtime-deps",
            kind="container",
            version="10.0.10-noble-chiseled-extra",
            digest_or_sha="sha256:" + "a" * 64,
            released_at="2026-08-01T12:34:56Z",
            source="https://mcr.microsoft.com/v2/dotnet/runtime-deps/manifests/10.0.10-noble-chiseled-extra",
        )
        payload = {
            "releases": [
                {
                    "release-date": "2026-08-01T12:34:56Z",
                    "release-version": "10.0.10",
                    "runtime": {"version": "10.0.10"},
                }
            ]
        }
        expectations = {
            "one second early": (
                datetime(2026, 8, 4, 12, 34, 55, tzinfo=timezone.utc),
                True,
            ),
            "exactly 72 hours": (
                datetime(2026, 8, 4, 12, 34, 56, tzinfo=timezone.utc),
                False,
            ),
        }
        for name, (now, rejected) in expectations.items():
            with self.subTest(name=name), patch.object(
                checker, "fetch_manifest_digest", return_value=item["digest_or_sha"]
            ), patch.object(checker, "fetch_json", return_value=(payload, {})):
                errors = checker.validate_online([item], now)

                self.assertEqual(
                    rejected,
                    any("newer than 72 hours" in error for error in errors),
                    errors,
                )

    def test_online_fetches_dotnet_metadata_once_for_all_containers(self):
        sdk = inventory_item(
            name="mcr.microsoft.com/dotnet/sdk",
            kind="container",
            version="10.0.302-noble-aot",
            digest_or_sha="sha256:" + "a" * 64,
            released_at="2026-07-14",
            source="https://mcr.microsoft.com/v2/dotnet/sdk/manifests/10.0.302-noble-aot",
        )
        runtime = inventory_item(
            name="mcr.microsoft.com/dotnet/runtime-deps",
            kind="container",
            version="10.0.10-noble-chiseled-extra",
            digest_or_sha="sha256:" + "b" * 64,
            released_at="2026-07-14",
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
            released_at="2026-07-14",
            source="https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json",
            artifact_url="https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.302/dotnet-sdk-10.0.302-linux-x64.tar.gz",
        )
        payload = {
            "releases": [{"release-date": "2026-07-14", "sdk": {"version": "10.0.302", "files": [{"url": item["artifact_url"], "hash": "b" * 128}]}}]
        }

        with patch.object(checker, "fetch_json", return_value=(payload, {})):
            errors = checker.validate_online([item], datetime(2026, 8, 11, tzinfo=timezone.utc))

        self.assertTrue(any(".NET metadata digest" in error for error in errors))

    def test_online_applies_the_date_only_bound_to_dotnet_sdk_metadata(self):
        item = {
            **dotnet_inventory_item(),
            "released_at": "2026-08-01",
        }
        payload = {
            "releases": [
                {
                    "release-date": "2026-08-01",
                    "sdk": {
                        "version": item["version"],
                        "files": [
                            {
                                "url": item["artifact_url"],
                                "hash": item["digest_or_sha"].removeprefix("sha512:"),
                            }
                        ],
                    },
                }
            ]
        }
        for name, now, rejected in (
            ("73 hours after midnight", datetime(2026, 8, 4, 1, tzinfo=timezone.utc), True),
            ("96 hours after midnight", datetime(2026, 8, 5, tzinfo=timezone.utc), False),
        ):
            with self.subTest(name=name), patch.object(
                checker, "fetch_json", return_value=(payload, {})
            ):
                errors = checker.validate_online([item], now)

                self.assertEqual(
                    rejected,
                    any("newer than 72 hours" in error for error in errors),
                    errors,
                )

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
