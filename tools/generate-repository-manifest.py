#!/usr/bin/env python3
"""Generate the Jellyfin plugin repository manifest for the current build."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path


REPOSITORY = "Bugstehude/aether-jellyfin-duplicate-finder"


def read_scalar(build: str, key: str) -> str:
    match = re.search(rf'^{re.escape(key)}:\s*"([^"\n]+)"\s*$', build, re.MULTILINE)
    if match is None:
        raise ValueError(f"Missing quoted {key} in build.yaml")
    return match.group(1)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("archive", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--build", type=Path, default=Path("build.yaml"))
    args = parser.parse_args()

    build = args.build.read_text(encoding="utf-8")
    version = read_scalar(build, "version")
    archive_name = f"aether-duplicate-finder-{version}.zip"
    if args.archive.name != archive_name:
        raise ValueError(f"Expected archive named {archive_name}, got {args.archive.name}")

    checksum = hashlib.md5(args.archive.read_bytes(), usedforsecurity=False).hexdigest()
    manifest = [
        {
            "guid": read_scalar(build, "guid"),
            "name": read_scalar(build, "name"),
            "description": (
                "Finds duplicate video files by actual file content (exact hash first), scoped "
                "to admin-chosen libraries/subfolders, and lets an administrator review and "
                "delete them — nothing is ever deleted automatically."
            ),
            "overview": read_scalar(build, "overview"),
            "owner": read_scalar(build, "owner"),
            "category": read_scalar(build, "category"),
            "versions": [
                {
                    "version": version,
                    "changelog": read_scalar(build, "changelog"),
                    "targetAbi": read_scalar(build, "targetAbi"),
                    "sourceUrl": (
                        f"https://github.com/{REPOSITORY}/releases/download/"
                        f"v{version}/{archive_name}"
                    ),
                    "checksum": checksum,
                    "timestamp": read_scalar(build, "timestamp"),
                }
            ],
        }
    ]

    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
