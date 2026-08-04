#!/usr/bin/env python3
"""Generate a deterministic CycloneDX SBOM from the production NuGet lockfile."""

from __future__ import annotations

import base64
import json
import re
import sys
from pathlib import Path
from urllib.parse import quote


ROOT = Path(__file__).resolve().parent.parent
LOCKFILE = ROOT / "src/Jellyfin.Plugin.DuplicateFinder/packages.lock.json"
BUILD_MANIFEST = ROOT / "build.yaml"


def main() -> None:
    output = Path(sys.argv[1]) if len(sys.argv) > 1 else ROOT / "artifacts/package/aether-duplicate-finder.cdx.json"
    version_match = re.search(r'^version: "([0-9.]+)"$', BUILD_MANIFEST.read_text(), re.MULTILINE)
    if version_match is None:
        raise SystemExit("Unable to read plugin version from build.yaml")

    lock = json.loads(LOCKFILE.read_text())
    dependencies = lock["dependencies"]["net9.0"]
    components: list[dict[str, object]] = []
    for name, package in sorted(dependencies.items(), key=lambda item: item[0].lower()):
        resolved = package.get("resolved")
        content_hash = package.get("contentHash")
        if not isinstance(resolved, str) or not isinstance(content_hash, str):
            continue
        component: dict[str, object] = {
            "type": "library",
            "bom-ref": f"pkg:nuget/{quote(name)}@{quote(resolved)}",
            "name": name,
            "version": resolved,
            "purl": f"pkg:nuget/{quote(name)}@{quote(resolved)}",
            "scope": "required",
            "properties": [
                {"name": "aether:asset-boundary", "value": "host-provided; not embedded in plugin archive"}
            ],
        }
        try:
            component["hashes"] = [
                {"alg": "SHA-512", "content": base64.b64decode(content_hash).hex()}
            ]
        except (ValueError, TypeError):
            pass
        components.append(component)

    version = version_match.group(1)
    document = {
        "bomFormat": "CycloneDX",
        "specVersion": "1.5",
        "version": 1,
        "metadata": {
            "component": {
                "type": "application",
                "bom-ref": f"pkg:generic/aether-duplicate-finder@{version}",
                "name": "AETHER Duplicate Finder for Jellyfin",
                "version": version,
            },
            "properties": [
                {"name": "aether:target-jellyfin", "value": "10.11.11"},
                {
                    "name": "aether:archive-contents",
                    "value": "Jellyfin.Plugin.DuplicateFinder.dll",
                },
            ],
        },
        "components": components,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(document, indent=2, sort_keys=True) + "\n")


if __name__ == "__main__":
    main()
