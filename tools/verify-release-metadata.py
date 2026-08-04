#!/usr/bin/env python3
"""Fail when the package and assembly release versions drift apart."""

from __future__ import annotations

import re
from datetime import datetime
from pathlib import Path


ROOT = Path(__file__).resolve().parent.parent
BUILD_MANIFEST = ROOT / "build.yaml"
BUILD_PROPS = ROOT / "Directory.Build.props"


def read_version(path: Path, pattern: str, label: str) -> str:
    match = re.search(pattern, path.read_text(encoding="utf-8"), re.MULTILINE)
    if match is None:
        raise SystemExit(f"Unable to read {label} from {path.relative_to(ROOT)}")
    return match.group(1)


def main() -> None:
    package_version = read_version(
        BUILD_MANIFEST,
        r'^version:\s*"([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)"\s*$',
        "package version",
    )
    assembly_version = read_version(
        BUILD_PROPS,
        r"<VersionPrefix>\s*([0-9]+\.[0-9]+\.[0-9]+\.[0-9]+)\s*</VersionPrefix>",
        "assembly version",
    )
    timestamp = read_version(
        BUILD_MANIFEST,
        r'^timestamp:\s*"([^"]+)"\s*$',
        "release timestamp",
    )

    if package_version != assembly_version:
        raise SystemExit(
            "Release version mismatch: "
            f"build.yaml={package_version}, Directory.Build.props={assembly_version}"
        )

    try:
        parsed_timestamp = datetime.fromisoformat(timestamp.replace("Z", "+00:00"))
    except ValueError as error:
        raise SystemExit(f"Release timestamp is not ISO 8601: {timestamp}") from error
    if parsed_timestamp.tzinfo is None:
        raise SystemExit(f"Release timestamp must include a timezone: {timestamp}")

    print(f"Release metadata is consistent: {package_version} at {timestamp}")


if __name__ == "__main__":
    main()
