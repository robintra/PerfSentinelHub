import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parents[2]
CHECKER = REPOSITORY / "scripts" / "check-coverage.py"


def cobertura(*files):
    classes = []
    for filename, lines in files:
        entries = "".join(
            f'<line number="{number}" hits="{hits}" />' for number, hits in lines
        )
        classes.append(
            f'<class name="{filename}" filename="{filename}"><lines>{entries}</lines></class>'
        )
    return (
        '<?xml version="1.0" encoding="utf-8"?>'
        '<coverage><packages><package><classes>'
        f'{"".join(classes)}'
        '</classes></package></packages></coverage>'
    )


def write(path, content):
    path.write_text(content, encoding="utf-8")


def run_checker(*arguments):
    return subprocess.run(
        [sys.executable, str(CHECKER), *map(str, arguments)],
        cwd=REPOSITORY,
        text=True,
        capture_output=True,
        check=False,
    )


class CoverageCheckerTests(unittest.TestCase):
    def test_rejects_total_coverage_regression(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            baseline = root / "baseline.xml"
            current = root / "current.xml"
            write(baseline, cobertura(("src/App.cs", ((1, 1), (2, 1)))))
            write(current, cobertura(("src/App.cs", ((1, 1), (2, 0)))))

            result = run_checker(
                "--baseline-report", baseline, "--current-report", current
            )

            self.assertEqual(1, result.returncode)
            self.assertIn("total line coverage regressed: 50.00% < 100.00%", result.stderr)

    def test_rejects_79_99_percent_new_code_coverage(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            baseline = root / "baseline.xml"
            current = root / "current.xml"
            write(baseline, cobertura(("src/App.cs", ((1, 0),))))
            new_lines = tuple(
                (number, int(number <= 8_000)) for number in range(2, 10_002)
            )
            write(current, cobertura(("src/App.cs", ((1, 0), *new_lines))))

            result = run_checker(
                "--baseline-report", baseline, "--current-report", current
            )

            self.assertEqual(1, result.returncode)
            self.assertIn("new-code line coverage is 79.99%; required 80.00%", result.stderr)

    def test_rejects_empty_reports(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            baseline = root / "baseline.xml"
            current = root / "current.xml"
            write(baseline, cobertura())
            write(current, cobertura(("src/App.cs", ((1, 1),))))

            result = run_checker(
                "--baseline-report", baseline, "--current-report", current
            )

            self.assertEqual(1, result.returncode)
            self.assertIn("contains no line records", result.stderr)

    def test_rejects_malformed_xml(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            baseline = root / "baseline.xml"
            current = root / "current.xml"
            write(baseline, cobertura(("src/App.cs", ((1, 1),))))
            write(current, "<coverage><packages>")

            result = run_checker(
                "--baseline-report", baseline, "--current-report", current
            )

            self.assertEqual(1, result.returncode)
            self.assertIn("unable to parse Cobertura XML", result.stderr)

    def test_accepts_exactly_80_percent_new_code_coverage(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            baseline = root / "baseline.xml"
            current = root / "current.xml"
            write(baseline, cobertura(("src/App.cs", ((1, 0),))))
            write(
                current,
                cobertura(
                    ("src/App.cs", ((1, 0), (2, 1), (3, 1), (4, 1), (5, 1), (6, 0)))
                ),
            )

            result = run_checker(
                "--baseline-report", baseline, "--current-report", current
            )

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("new-code line coverage: 80.00%", result.stdout)

    def test_deleted_lines_are_not_counted_as_new_code(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            baseline = root / "baseline.xml"
            current = root / "current.xml"
            write(baseline, cobertura(("src/App.cs", ((1, 1), (2, 0)))))
            write(current, cobertura(("src/App.cs", ((1, 1),))))

            result = run_checker(
                "--baseline-report", baseline, "--current-report", current
            )

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("no new executable lines", result.stdout)

    def test_line_keys_normalize_cobertura_path_separators(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            baseline = root / "baseline.xml"
            current = root / "current.xml"
            write(baseline, cobertura((r"src\App.cs", ((1, 0),))))
            write(current, cobertura(("src/App.cs", ((1, 0),))))

            result = run_checker(
                "--baseline-report", baseline, "--current-report", current
            )

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("no new executable lines", result.stdout)

    def test_establishes_a_numeric_total_baseline_only(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report = root / "current.xml"
            baseline = root / "coverage-baseline.json"
            write(report, cobertura(("src/App.cs", ((1, 1), (2, 0)))))

            result = run_checker(
                "--establish-baseline", report, "--baseline-file", baseline
            )

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual({"total_line_coverage": 50.0}, json.loads(baseline.read_text()))

    def test_local_mode_checks_total_and_discloses_new_code_is_not_applicable(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report = root / "current.xml"
            baseline = root / "coverage-baseline.json"
            write(report, cobertura(("src/App.cs", ((1, 1), (2, 1)))))
            baseline.write_text('{"total_line_coverage": 80.0}\n', encoding="utf-8")

            result = run_checker(
                "--current-report", report, "--baseline-file", baseline
            )

            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("new-code gate not applicable", result.stdout)

    def test_established_recurring_percentage_does_not_regress_against_itself(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report = root / "current.xml"
            baseline = root / "coverage-baseline.json"
            write(report, cobertura(("src/App.cs", ((1, 1), (2, 1), (3, 0)))))

            established = run_checker(
                "--establish-baseline", report, "--baseline-file", baseline
            )
            checked = run_checker(
                "--current-report", report, "--baseline-file", baseline
            )

            self.assertEqual(0, established.returncode, established.stderr)
            self.assertEqual(0, checked.returncode, checked.stderr)


if __name__ == "__main__":
    unittest.main()
