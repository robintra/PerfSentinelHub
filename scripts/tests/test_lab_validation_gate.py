"""The release gate that refuses a tag without a fresh lab validation.

release-gate/check-lab-validation.sh is a copy of perf-sentinel's, whose own
211-line suite lives upstream and runs in its CI. Duplicating that suite to
test an identical body would buy nothing. These cases cover what a copy can
break on its own: the ledger it defaults to, and the four verdicts an
operator relies on before tagging.
"""

import subprocess
import tempfile
import unittest
from datetime import datetime, timedelta, timezone
from pathlib import Path

REPOSITORY = Path(__file__).resolve().parents[2]
GATE = REPOSITORY / "release-gate" / "check-lab-validation.sh"
LEDGER = REPOSITORY / "release-gate" / "lab-validations.txt"
SHA = "abc1234"


def run(version, ledger=None):
    command = [str(GATE), "--version", version]
    if ledger is not None:
        command += ["--ledger", str(ledger)]
    return subprocess.run(command, capture_output=True, text=True, check=False)


def ledger_with(*lines):
    handle = tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False, encoding="utf-8")
    handle.write("# test ledger\n")
    for line in lines:
        handle.write(line + "\n")
    handle.close()
    return Path(handle.name)


def entry(version, when, verdict="PASS"):
    return f"{version}\t{SHA}\t{when.isoformat()}\t{verdict}"


def utc_today():
    """The ledger's dates are UTC on both sides, the recorder writes `date -u`
    and the gate reads `date -u`. Local time drifts a day ahead of it for a
    couple of hours a night, which the gate rightly refuses as a future date."""
    return datetime.now(timezone.utc).date()


class LabValidationGate(unittest.TestCase):
    def test_the_gate_is_executable_and_ships_with_the_repository(self):
        self.assertTrue(GATE.is_file(), f"{GATE} is missing")
        self.assertTrue(GATE.stat().st_mode & 0o111, f"{GATE} is not executable")
        self.assertTrue(LEDGER.is_file(), f"{LEDGER} is missing")

    def test_a_fresh_pass_opens_the_gate(self):
        ledger = ledger_with(entry("v0.1.0", utc_today()))
        self.assertEqual(run("v0.1.0", ledger).returncode, 0)

    def test_a_pass_older_than_the_window_is_refused(self):
        ledger = ledger_with(entry("v0.1.0", utc_today() - timedelta(days=31)))
        result = run("v0.1.0", ledger)
        self.assertEqual(result.returncode, 1)
        self.assertIn("v0.1.0", result.stderr)

    def test_a_fail_verdict_does_not_open_the_gate(self):
        ledger = ledger_with(entry("v0.1.0", utc_today(), "FAIL"))
        self.assertEqual(run("v0.1.0", ledger).returncode, 1)

    def test_another_version_passing_does_not_open_this_one(self):
        ledger = ledger_with(entry("v0.2.0", utc_today()))
        self.assertEqual(run("v0.1.0", ledger).returncode, 1)

    def test_the_shipped_ledger_is_the_default_and_is_readable(self):
        # No --ledger: the gate has to find the repository's own. Whatever the
        # verdict, it must not fail on a missing or unreadable file.
        result = run("v0.0.0-absent")
        self.assertNotIn("not found", result.stderr)


if __name__ == "__main__":
    unittest.main()
