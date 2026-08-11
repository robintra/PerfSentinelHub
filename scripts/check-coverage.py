#!/usr/bin/env python3
"""Enforce total and new-code line coverage from bounded Cobertura reports."""

from __future__ import annotations

import argparse
import json
import posixpath
import re
import sys
import xml.etree.ElementTree as ElementTree
from decimal import Decimal
from pathlib import Path


REPOSITORY = Path(__file__).resolve().parents[1]
DEFAULT_BASELINE = REPOSITORY / "config" / "coverage-baseline.json"
MAX_REPORT_BYTES = 64 * 1024 * 1024
MAX_LINE_RECORDS = 2_000_000
NEW_CODE_PERCENT = 80
POSITIVE_INTEGER = re.compile(r"^[1-9][0-9]{0,9}$")
NONNEGATIVE_INTEGER = re.compile(r"^(?:0|[1-9][0-9]{0,9})$")


class CoverageError(ValueError):
    pass


def local_name(tag: object) -> str:
    return tag.rsplit("}", 1)[-1] if isinstance(tag, str) else ""


def normalized_filename(value: object) -> str:
    if not isinstance(value, str) or not value or len(value) > 4_096:
        raise CoverageError("class filename is missing or invalid")
    normalized = posixpath.normpath(value.replace("\\", "/"))
    if normalized in {"", ".", ".."} or normalized.startswith("../"):
        raise CoverageError("class filename is not a stable path")
    return normalized


def reject_unsafe_xml(path: Path) -> None:
    try:
        size = path.stat().st_size
        if size <= 0 or size > MAX_REPORT_BYTES:
            raise CoverageError(
                f"{path}: report size must be between 1 and {MAX_REPORT_BYTES} bytes"
            )
        tail = b""
        with path.open("rb") as report:
            while chunk := report.read(64 * 1024):
                candidate = (tail + chunk).upper()
                if b"<!DOCTYPE" in candidate or b"<!ENTITY" in candidate:
                    raise CoverageError(f"{path}: DTD and entity declarations are forbidden")
                tail = candidate[-16:]
    except OSError as error:
        raise CoverageError(f"{path}: unable to read coverage report") from error


def read_report(path: Path) -> dict[tuple[str, int], bool]:
    reject_unsafe_xml(path)
    lines: dict[tuple[str, int], bool] = {}
    records = 0
    try:
        for _, element in ElementTree.iterparse(path, events=("end",)):
            if local_name(element.tag) != "class":
                continue
            filename = normalized_filename(element.get("filename"))
            for line in element.iter():
                if local_name(line.tag) != "line":
                    continue
                number_text = line.get("number")
                hits_text = line.get("hits")
                if not isinstance(number_text, str) or not POSITIVE_INTEGER.fullmatch(
                    number_text
                ):
                    raise CoverageError(f"{path}: line number is missing or invalid")
                if not isinstance(hits_text, str) or not NONNEGATIVE_INTEGER.fullmatch(
                    hits_text
                ):
                    raise CoverageError(f"{path}: line hits are missing or invalid")
                records += 1
                if records > MAX_LINE_RECORDS:
                    raise CoverageError(
                        f"{path}: report exceeds {MAX_LINE_RECORDS} line records"
                    )
                key = (filename, int(number_text))
                lines[key] = lines.get(key, False) or int(hits_text) > 0
            element.clear()
    except ElementTree.ParseError as error:
        raise CoverageError(f"{path}: unable to parse Cobertura XML") from error
    except OSError as error:
        raise CoverageError(f"{path}: unable to read coverage report") from error
    if not lines:
        raise CoverageError(f"{path}: report contains no line records")
    return lines


def percentage(lines: dict[tuple[str, int], bool]) -> Decimal:
    return Decimal(sum(lines.values())) * 100 / Decimal(len(lines))


def display(value: Decimal) -> str:
    return f"{value:.2f}%"


def load_numeric_baseline(path: Path) -> Decimal:
    def reject_constant(value: str):
        raise ValueError(f"non-finite number {value}")

    try:
        payload = json.loads(
            path.read_text(encoding="utf-8"),
            parse_float=Decimal,
            parse_int=Decimal,
            parse_constant=reject_constant,
        )
        if not isinstance(payload, dict) or set(payload) != {"total_line_coverage"}:
            raise ValueError("baseline root is not canonical")
        value = payload["total_line_coverage"]
        if not isinstance(value, Decimal) or value < 0 or value > 100:
            raise ValueError("baseline value is not a percentage")
        return value
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError) as error:
        raise CoverageError(f"{path}: unable to parse numeric coverage baseline") from error


def establish_baseline(report_path: Path, baseline_path: Path) -> None:
    total = percentage(read_report(report_path))
    baseline_path.write_text(
        f'{{\n  "total_line_coverage": {total:f}\n}}\n', encoding="utf-8"
    )
    print(f"established total line coverage baseline: {display(total)}")


def compare_reports(baseline_path: Path, current_path: Path) -> None:
    baseline = read_report(baseline_path)
    current = read_report(current_path)
    baseline_total = percentage(baseline)
    current_total = percentage(current)
    if current_total < baseline_total:
        raise CoverageError(
            f"total line coverage regressed: {display(current_total)} < {display(baseline_total)}"
        )

    new_lines = {key: covered for key, covered in current.items() if key not in baseline}
    if not new_lines:
        print(
            f"total line coverage: {display(current_total)} (baseline {display(baseline_total)}); "
            "new-code line coverage: not applicable (no new executable lines)"
        )
        return
    new_total = percentage(new_lines)
    if new_total < NEW_CODE_PERCENT:
        raise CoverageError(
            f"new-code line coverage is {display(new_total)}; required {NEW_CODE_PERCENT:.2f}%"
        )
    print(
        f"total line coverage: {display(current_total)} (baseline {display(baseline_total)}); "
        f"new-code line coverage: {display(new_total)}"
    )


def compare_numeric_baseline(baseline_path: Path, current_path: Path) -> None:
    baseline_total = load_numeric_baseline(baseline_path)
    current_total = percentage(read_report(current_path))
    if current_total < baseline_total:
        raise CoverageError(
            f"total line coverage regressed: {display(current_total)} < {display(baseline_total)}"
        )
    print(f"total line coverage: {display(current_total)} (baseline {display(baseline_total)})")
    print("new-code gate not applicable: --baseline-report not provided")


def parse_arguments() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--establish-baseline", type=Path, metavar="REPORT")
    mode.add_argument("--current-report", type=Path, metavar="REPORT")
    parser.add_argument("--baseline-report", type=Path, metavar="REPORT")
    parser.add_argument("--baseline-file", type=Path, default=DEFAULT_BASELINE)
    arguments = parser.parse_args()
    if arguments.establish_baseline and arguments.baseline_report:
        parser.error("--baseline-report requires --current-report")
    return arguments


def main() -> int:
    arguments = parse_arguments()
    try:
        if arguments.establish_baseline:
            establish_baseline(arguments.establish_baseline, arguments.baseline_file)
        elif arguments.baseline_report:
            compare_reports(arguments.baseline_report, arguments.current_report)
        else:
            compare_numeric_baseline(arguments.baseline_file, arguments.current_report)
    except (CoverageError, OSError) as error:
        print(f"coverage check failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
