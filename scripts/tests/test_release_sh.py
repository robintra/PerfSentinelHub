import datetime
import hashlib
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parents[2]
RELEASE = REPOSITORY / "scripts" / "release.sh"
CHECKER = REPOSITORY / "scripts" / "check-version.py"
LAB_GATE = REPOSITORY / "release-gate" / "check-lab-validation.sh"


class ReleaseScriptTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue(RELEASE.is_file(), "scripts/release.sh is missing")
        self.assertTrue(CHECKER.is_file(), "scripts/check-version.py is missing")
        self.assertTrue(LAB_GATE.is_file(), "release-gate/check-lab-validation.sh is missing")
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary_directory.name)
        self.repository = self.root / "repository"
        self.remote = self.root / "origin.git"
        self.signing_key = self.root / "release-signing-key"
        self.allowed_signers = self.root / "allowed_signers"
        self.environment = os.environ.copy()
        self.environment.update({"GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1"})
        self.create_repository()

    def tearDown(self):
        if hasattr(self, "temporary_directory"):
            self.temporary_directory.cleanup()

    def test_dry_run_is_side_effect_free(self):
        before = self.snapshot()

        result = self.run_release("v0.1.0", "--dry-run")

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("no repository or remote mutation", result.stdout)
        self.assertEqual(before, self.snapshot())

    def test_rejects_a_version_the_lab_never_validated(self):
        self.commit_ledger()

        result = self.run_release("v0.1.0", "--dry-run")

        self.assertEqual(1, result.returncode)
        self.assertIn("check-lab-validation.sh refused v0.1.0", result.stderr)

    def test_skip_lab_bypasses_the_only_skippable_gate_loudly(self):
        self.commit_ledger()
        before = self.snapshot()

        result = self.run_release("v0.1.0", "--dry-run", "--skip-lab")

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("lab-validation gate bypassed by operator", result.stderr)
        self.assertEqual(before, self.snapshot())

    def test_rejects_prerelease_tag(self):
        result = self.run_release("v0.1.0-beta.1", "--dry-run")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("stable tag", result.stderr)

    def test_rejects_dirty_tree(self):
        (self.repository / "README.md").write_text("dirty\n", encoding="utf-8")

        result = self.run_release("v0.1.0", "--dry-run")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("working tree", result.stderr)

    def test_rejects_wrong_branch(self):
        self.git("switch", "-c", "release-candidate")

        result = self.run_release("v0.1.0", "--dry-run")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("main", result.stderr)

    def test_rejects_missing_or_untrusted_signing_identity(self):
        self.git("config", "--unset", "user.signingkey")
        missing = self.run_release("v0.1.0", "--dry-run")
        self.assertNotEqual(0, missing.returncode)
        self.assertIn("signing identity", missing.stderr)

        self.git("config", "user.signingkey", str(self.signing_key))
        self.allowed_signers.write_text("release@test invalid-key\n", encoding="utf-8")
        untrusted = self.run_release("v0.1.0", "--dry-run")
        self.assertNotEqual(0, untrusted.returncode)
        self.assertIn("signing identity", untrusted.stderr)

    def test_rejects_local_main_ahead_of_remote(self):
        (self.repository / "ahead.txt").write_text("ahead\n", encoding="utf-8")
        self.git("add", "ahead.txt")
        self.git("commit", "-m", "local ahead")

        result = self.run_release("v0.1.0", "--dry-run")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("synchronized", result.stderr)

    def test_rejects_local_main_behind_remote(self):
        other = self.root / "other"
        self.run_command("git", "clone", "-q", str(self.remote), str(other), cwd=self.root)
        self.git_at(other, "config", "user.name", "Other")
        self.git_at(other, "config", "user.email", "other@example.invalid")
        (other / "remote.txt").write_text("remote\n", encoding="utf-8")
        self.git_at(other, "add", "remote.txt")
        self.git_at(other, "commit", "-m", "remote ahead")
        self.git_at(other, "push", "origin", "main")

        result = self.run_release("v0.1.0", "--dry-run")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("synchronized", result.stderr)

    def test_rejects_existing_local_tag(self):
        self.git("tag", "v0.1.0")

        result = self.run_release("v0.1.0", "--dry-run")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("already exists locally", result.stderr)

    def test_rejects_existing_remote_tag(self):
        self.git("tag", "v0.1.0")
        self.git("push", "origin", "v0.1.0")
        self.git("tag", "-d", "v0.1.0")

        result = self.run_release("v0.1.0", "--dry-run")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("already exists on origin", result.stderr)

    def test_rejects_version_drift(self):
        chart = self.repository / "deploy/helm/perf-sentinel-hub/Chart.yaml"
        chart.write_text(chart.read_text(encoding="utf-8").replace("version: 0.1.0", "version: 0.1.1", 1), encoding="utf-8")
        self.git("add", str(chart.relative_to(self.repository)))
        self.git("commit", "-m", "drift version")
        self.git("push", "origin", "main")

        result = self.run_release("v0.1.0", "--dry-run")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("chart version", result.stderr)

    def test_requires_exact_confirmation(self):
        result = self.run_release("v0.1.0", input_text="yes\n")

        self.assertNotEqual(0, result.returncode)
        self.assertIn("confirmation", result.stderr)
        self.assertFalse(self.local_tag_exists())
        self.assertFalse(self.remote_tag_exists())

    def test_creates_and_pushes_one_verified_signed_tag_after_confirmation(self):
        result = self.run_release("v0.1.0", input_text="v0.1.0\n")

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue(self.local_tag_exists())
        self.assertTrue(self.remote_tag_exists())
        verified = self.git_result("verify-tag", "v0.1.0")
        self.assertEqual(0, verified.returncode, verified.stderr)
        self.assertEqual(self.git_output("rev-parse", "main"), self.git_output("rev-list", "-n", "1", "v0.1.0"))
        remote_refs = self.git_at_output(self.remote, "for-each-ref", "--format=%(refname)", "refs/tags")
        self.assertEqual("refs/tags/v0.1.0", remote_refs)

    def create_repository(self):
        (self.repository / "scripts").mkdir(parents=True)
        (self.repository / "PerfSentinelHub").mkdir()
        (self.repository / "deploy/helm/perf-sentinel-hub").mkdir(parents=True)
        shutil.copy2(RELEASE, self.repository / "scripts/release.sh")
        shutil.copy2(CHECKER, self.repository / "scripts/check-version.py")
        (self.repository / "release-gate").mkdir()
        shutil.copy2(LAB_GATE, self.repository / "release-gate/check-lab-validation.sh")
        self.write_ledger("v0.1.0")
        (self.repository / "PerfSentinelHub/PerfSentinelHub.csproj").write_text(
            "<Project><PropertyGroup><Version>0.1.0</Version></PropertyGroup></Project>\n",
            encoding="utf-8",
        )
        (self.repository / "deploy/helm/perf-sentinel-hub/Chart.yaml").write_text(
            'apiVersion: v2\nname: perf-sentinel-hub\ntype: application\nversion: 0.1.0\nappVersion: "0.1.0"\n',
            encoding="utf-8",
        )
        (self.repository / "CHANGELOG.md").write_text(
            "# Changelog\n\n## [0.1.0] - 2026-08-12\n\n- Initial release.\n",
            encoding="utf-8",
        )
        (self.repository / "Dockerfile").write_text(
            'FROM scratch\nLABEL org.opencontainers.image.version="0.1.0"\n',
            encoding="utf-8",
        )
        (self.repository / "README.md").write_text("fixture\n", encoding="utf-8")
        (self.repository / "Makefile").write_text(
            "release-check:\n\tpython3 scripts/check-version.py v$(VERSION)\n",
            encoding="utf-8",
        )

        self.run_command("ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", str(self.signing_key), cwd=self.root)
        public_key = (self.signing_key.with_suffix(".pub")).read_text(encoding="ascii").strip()
        self.allowed_signers.write_text(f"release@test {public_key}\n", encoding="ascii")

        self.run_command("git", "init", "-q", "-b", "main", str(self.repository), cwd=self.root)
        self.git("config", "user.name", "Release Test")
        self.git("config", "user.email", "release@test")
        self.git("config", "gpg.format", "ssh")
        self.git("config", "user.signingkey", str(self.signing_key))
        self.git("config", "gpg.ssh.allowedSignersFile", str(self.allowed_signers))
        self.git("add", ".")
        self.git("commit", "-m", "fixture")
        self.run_command("git", "init", "-q", "--bare", "--initial-branch=main", str(self.remote), cwd=self.root)
        self.git("remote", "add", "origin", str(self.remote))
        self.git("push", "-u", "origin", "main")

    def write_ledger(self, *versions):
        today = datetime.date.today().isoformat()
        lines = "".join(f"{version}\tabc1234\t{today}\tPASS\n" for version in versions)
        (self.repository / "release-gate/lab-validations.txt").write_text(
            "# fixture ledger\n" + lines, encoding="utf-8"
        )

    def commit_ledger(self, *versions):
        self.write_ledger(*versions)
        self.git("add", "release-gate/lab-validations.txt")
        self.git("commit", "-m", "ledger")
        self.git("push", "-q", str(self.remote), "main")

    def run_release(self, *arguments, input_text=None):
        return subprocess.run(
            [str(self.repository / "scripts/release.sh"), *arguments],
            cwd=self.repository,
            env=self.environment,
            input=input_text,
            text=True,
            capture_output=True,
            check=False,
        )

    def snapshot(self):
        files = {}
        for path in sorted(self.repository.rglob("*")):
            if path.is_file() and ".git" not in path.parts:
                files[str(path.relative_to(self.repository))] = hashlib.sha256(path.read_bytes()).hexdigest()
        return {
            "files": files,
            "status": self.git_output("status", "--porcelain=v1", "--untracked-files=all"),
            "local_refs": self.git_output("for-each-ref", "--format=%(refname) %(objectname)"),
            "remote_refs": self.git_at_output(self.remote, "for-each-ref", "--format=%(refname) %(objectname)"),
        }

    def local_tag_exists(self):
        return self.git_result("show-ref", "--verify", "--quiet", "refs/tags/v0.1.0").returncode == 0

    def remote_tag_exists(self):
        return self.git_at_result(self.remote, "show-ref", "--verify", "--quiet", "refs/tags/v0.1.0").returncode == 0

    def git(self, *arguments):
        result = self.git_result(*arguments)
        self.assertEqual(0, result.returncode, result.stderr)
        return result

    def git_output(self, *arguments):
        return self.git(*arguments).stdout.strip()

    def git_result(self, *arguments):
        return self.run_command("git", *arguments, cwd=self.repository, check=False)

    def git_at(self, directory, *arguments):
        result = self.git_at_result(directory, *arguments)
        self.assertEqual(0, result.returncode, result.stderr)
        return result

    def git_at_output(self, directory, *arguments):
        return self.git_at(directory, *arguments).stdout.strip()

    def git_at_result(self, directory, *arguments):
        return self.run_command("git", "-C", str(directory), *arguments, cwd=self.root, check=False)

    def run_command(self, *arguments, cwd, check=True):
        result = subprocess.run(
            arguments,
            cwd=cwd,
            env=self.environment,
            text=True,
            capture_output=True,
            check=False,
        )
        if check:
            self.assertEqual(0, result.returncode, result.stderr)
        return result


if __name__ == "__main__":
    unittest.main()
