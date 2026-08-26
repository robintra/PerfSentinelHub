#!/usr/bin/env python3
"""Normalise a filtered Cobertura report for the coverage gate.

ReportGenerator writes a byte order mark and a DOCTYPE declaration.
check-coverage.py rejects both: a coverage report must be strict UTF-8
without a BOM, and must carry no DTD or entity declaration.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path

DOCTYPE = re.compile(r"<!DOCTYPE[^>]*>\s*")


def main() -> int:
    if len(sys.argv) != 3:
        print("usage: normalize-coverage.py SOURCE DESTINATION", file=sys.stderr)
        return 2
    source = Path(sys.argv[1])
    destination = Path(sys.argv[2])
    text = source.read_text(encoding="utf-8-sig")
    destination.write_text(DOCTYPE.sub("", text, count=1), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
