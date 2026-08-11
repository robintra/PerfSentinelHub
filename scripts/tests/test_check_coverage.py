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
    valid = 0
    covered = 0
    for filename, lines in files:
        valid += len(lines)
        covered += sum(hits > 0 for _, hits in lines)
        entries = "".join(
            f'<line number="{number}" hits="{hits}" />' for number, hits in lines
        )
        classes.append(
            f'<class name="{filename}" filename="{filename}"><lines>{entries}</lines></class>'
        )
    return (
        '<?xml version="1.0" encoding="utf-8"?>'
        f'<coverage lines-covered="{covered}" lines-valid="{valid}">'
        '<packages><package><classes>'
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

    def test_rejects_utf16_dtd_without_expanding_its_entity(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report = root / "utf16.xml"
            baseline = root / "coverage-baseline.json"
            report.write_bytes(
                (
                    '<?xml version="1.0" encoding="utf-16"?>'
                    '<!DOCTYPE coverage [<!ENTITY probe "EXPANDED">]>'
                    '<coverage lines-covered="1" lines-valid="1"><packages><package>'
                    '<classes><class name="App" filename="&probe;"><lines>'
                    '<line number="1" hits="1" />'
                    '</lines></class></classes></package></packages></coverage>'
                ).encode("utf-16")
            )
            baseline.write_text('{"total_line_coverage": 0}\n', encoding="utf-8")

            result = run_checker(
                "--current-report", report, "--baseline-file", baseline
            )

            self.assertEqual(1, result.returncode)
            self.assertIn("strict UTF-8", result.stderr)
            self.assertNotIn("EXPANDED", result.stderr)
            self.assertNotIn("Traceback", result.stderr)

    def test_rejects_utf8_dtd_entity_whitespace_variants(self):
        declarations = (
            '<!DOCTYPE coverage [<!ENTITY probe "expanded">]>',
            '<!  DOCTYPE coverage [\n  <!  ENTITY probe "expanded">\n]>',
        )
        for declaration in declarations:
            with self.subTest(declaration=declaration), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                report = root / "current.xml"
                baseline = root / "coverage-baseline.json"
                write(
                    report,
                    '<?xml version="1.0" encoding="utf-8"?>'
                    f'{declaration}'
                    '<coverage lines-covered="1" lines-valid="1"><packages><package>'
                    '<classes><class name="App" filename="src/App.cs"><lines>'
                    '<line number="1" hits="1" />'
                    '</lines></class></classes></package></packages></coverage>',
                )
                baseline.write_text('{"total_line_coverage": 0}\n', encoding="utf-8")

                result = run_checker(
                    "--current-report", report, "--baseline-file", baseline
                )

                self.assertEqual(1, result.returncode)
                self.assertIn("DTD and entity declarations are forbidden", result.stderr)
                self.assertNotIn("Traceback", result.stderr)

    def test_rejects_non_cobertura_root_and_missing_package_structure(self):
        reports = (
            (
                "wrong root",
                '<not-cobertura lines-covered="1" lines-valid="1"><packages><package>'
                '<classes><class name="App" filename="src/App.cs"><lines>'
                '<line number="1" hits="1" />'
                '</lines></class></classes></package></packages></not-cobertura>',
            ),
            (
                "missing packages",
                '<coverage lines-covered="1" lines-valid="1">'
                '<class name="App" filename="src/App.cs"><line number="1" hits="1" />'
                '</class></coverage>',
            ),
        )
        for name, body in reports:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                report = root / "current.xml"
                baseline = root / "coverage-baseline.json"
                write(report, '<?xml version="1.0" encoding="utf-8"?>' + body)
                baseline.write_text('{"total_line_coverage": 0}\n', encoding="utf-8")

                result = run_checker(
                    "--current-report", report, "--baseline-file", baseline
                )

                self.assertEqual(1, result.returncode)
                self.assertIn("Cobertura", result.stderr)

    def test_rejects_duplicate_numeric_baseline_key(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report = root / "current.xml"
            baseline = root / "coverage-baseline.json"
            write(report, cobertura(("src/App.cs", ((1, 1),))))
            baseline.write_text(
                '{"total_line_coverage": 100, "total_line_coverage": 0}\n',
                encoding="utf-8",
            )

            result = run_checker(
                "--current-report", report, "--baseline-file", baseline
            )

            self.assertEqual(1, result.returncode)
            self.assertIn("duplicate JSON key", result.stderr)
            self.assertNotIn("Traceback", result.stderr)

    def test_rejects_utf8_bom_and_invalid_encoding(self):
        payloads = (
            ("UTF-8 BOM", b"\xef\xbb\xbf" + cobertura(("src/App.cs", ((1, 1),))).encode()),
            ("invalid UTF-8", b"\xff" + cobertura(("src/App.cs", ((1, 1),))).encode()),
        )
        for name, payload in payloads:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                report = root / "current.xml"
                baseline = root / "coverage-baseline.json"
                report.write_bytes(payload)
                baseline.write_text('{"total_line_coverage": 0}\n', encoding="utf-8")

                result = run_checker(
                    "--current-report", report, "--baseline-file", baseline
                )

                self.assertEqual(1, result.returncode)
                self.assertIn("strict UTF-8 without BOM", result.stderr)
                self.assertNotIn("Traceback", result.stderr)

    def test_rejects_incoherent_cobertura_root_line_counts(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report = root / "current.xml"
            baseline = root / "coverage-baseline.json"
            write(
                report,
                cobertura(("src/App.cs", ((1, 1),))).replace(
                    'lines-covered="1" lines-valid="1"',
                    'lines-covered="0" lines-valid="2"',
                ),
            )
            baseline.write_text('{"total_line_coverage": 0}\n', encoding="utf-8")

            result = run_checker(
                "--current-report", report, "--baseline-file", baseline
            )

            self.assertEqual(1, result.returncode)
            self.assertIn("line counts differ", result.stderr)

    def test_accepts_coverlet_generated_cobertura_shape(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            report = root / "current.xml"
            baseline = root / "coverage-baseline.json"
            write(
                report,
                '<?xml version="1.0" encoding="utf-8"?>\n'
                '<coverage line-rate="1" branch-rate="0" version="1.9" timestamp="1" '
                'lines-covered="1" lines-valid="1" branches-covered="0" branches-valid="0">'
                '<sources /><packages><package name="PerfSentinelHub" line-rate="1" '
                'branch-rate="0" complexity="1"><classes><class name="App" '
                'filename="/_/PerfSentinelHub/App.cs" line-rate="1" branch-rate="0" '
                'complexity="1"><methods><method name="Run" signature="()" line-rate="1" '
                'branch-rate="0" complexity="1"><lines><line number="10" hits="1" '
                'branch="False" /></lines></method></methods><lines><line number="10" '
                'hits="1" branch="False" /></lines></class></classes></package></packages>'
                '</coverage>',
            )
            baseline.write_text('{"total_line_coverage": 100}\n', encoding="utf-8")

            result = run_checker(
                "--current-report", report, "--baseline-file", baseline
            )

            self.assertEqual(0, result.returncode, result.stderr)


if __name__ == "__main__":
    unittest.main()
