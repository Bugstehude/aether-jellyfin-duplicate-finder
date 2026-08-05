#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="${CONFIGURATION:-Release}"
python3 "$root/tools/verify-release-metadata.py"
version="$(sed -n 's/^version: "\([^"]*\)"/\1/p' "$root/build.yaml")"
publish_dir="$root/artifacts/plugin"
package_dir="$root/artifacts/package"
staging_dir="$package_dir/staging"
archive="$package_dir/aether-duplicate-finder-${version}.zip"

if [[ -z "$version" ]]; then
  echo "Unable to read plugin version from build.yaml" >&2
  exit 1
fi
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "Plugin version must contain four numeric components: $version" >&2
  exit 1
fi

# Deliberately NOT --no-build: this script must never trust an out-of-band build to already
# match $configuration. It used to (--no-restore --no-build), and a local `dotnet build` run
# with the default Debug config left bin/Release stale — package-plugin.sh silently packaged
# an OLD DLL (right version number in build.yaml, wrong code inside) into a real release
# (v0.2.1.0 shipped 0.2.0.0's code, missing all 13 safety fixes from the code review) before
# this was caught. `dotnet publish` here always (re)builds Release itself, so the packaged
# DLL can never drift from source again.
dotnet publish "$root/src/Jellyfin.Plugin.DuplicateFinder/Jellyfin.Plugin.DuplicateFinder.csproj" \
  --configuration "$configuration" \
  --output "$publish_dir"

mkdir -p "$staging_dir" "$package_dir"
find "$staging_dir" -mindepth 1 -maxdepth 1 -delete
cp "$publish_dir/Jellyfin.Plugin.DuplicateFinder.dll" "$staging_dir/"
chmod 0644 "$staging_dir/Jellyfin.Plugin.DuplicateFinder.dll"
TZ=UTC touch -t 198001010000 "$staging_dir/Jellyfin.Plugin.DuplicateFinder.dll"
rm -f "$archive" "$archive.sha256"
(
  cd "$staging_dir"
  TZ=UTC zip -X -q "$archive" Jellyfin.Plugin.DuplicateFinder.dll
)

if command -v sha256sum >/dev/null 2>&1; then
  (cd "$package_dir" && sha256sum "$(basename "$archive")" > "$(basename "$archive").sha256")
else
  (cd "$package_dir" && shasum -a 256 "$(basename "$archive")" > "$(basename "$archive").sha256")
fi

python3 "$root/tools/generate-sbom.py" "$package_dir/aether-duplicate-finder-${version}.cdx.json"
python3 "$root/tools/generate-repository-manifest.py" \
  "$archive" \
  "$package_dir/manifest.json" \
  --build "$root/build.yaml"

echo "$archive"
