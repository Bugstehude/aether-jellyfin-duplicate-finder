# AETHER Duplicate Finder

A Jellyfin plugin that finds duplicate video files by **actual file content**, not just
metadata — the community duplicate finders that already exist for Jellyfin all match on
IMDB id or title+year, which misfires whenever metadata is wrong, missing, or ambiguous, and
say nothing about files that were never matched to metadata at all.

Nothing is ever deleted automatically. A scan only produces a list of candidate groups for an
administrator to review in the plugin's own config page; deletion is always an explicit,
per-item, confirmed action.

## Detection

- **Tier 1 (implemented):** SHA-256 hash of each video file. Two files with the same hash are
  certainly byte-for-byte the same file — no false positives are possible at this tier.
- **Tier 2 (planned, not yet implemented):** same recording at a different quality/encode,
  where the file hash necessarily differs. Planned approach: duration match as a cheap first
  filter, then a perceptual frame hash (ffmpeg frame extraction + pHash) for the remaining
  candidates, reusing the same worker-subprocess pattern the
  [AETHER Analysis plugin](https://github.com/Bugstehude/aether-jellyfin-analysis-plugin)
  already uses.

Tier 1 itself is a three-phase pipeline so the expensive full-file hash only ever runs where it
actually might matter, and never twice on an unmodified file across scans:

1. **Group by file size** (free — no I/O). Two files can only be identical if they're the same
   size, so a file with a unique size needs no further work at all.
2. **Quick hash** — only the first 64 KiB — for files that already share a size. Identical
   files are identical everywhere, including their first bytes, so this can only eliminate
   genuinely different files, never miss a real duplicate.
3. **Full SHA-256** over the whole file, only for the (size, quick hash) group that's left —
   normally a tiny fraction of the library.

Both hash phases are cached per file (path + size + last-write time) between scans, so a file
that hasn't changed since its last scan is never re-read at all.

## Scope

The config page lets you tick specific libraries, or click ▸ next to a library to drill into
and tick individual subfolders instead — the same folder-browsing UI the AETHER Analysis
plugin's "Libraries to analyze" picker uses (`ApiClient.getItems`, no server-side work needed:
Jellyfin's `AncestorIds` query filter treats a subfolder id exactly like a library id).

## Review and deletion

- **Scan now** button runs a background hash pass over the configured scope; progress is
  polled and shown live.
- Results are grouped by shared hash and shown with a size/resolution/duration/codec
  comparison, plus a suggested keep (currently: largest file in the group).
- Keyboard: **↑/↓** moves between files, **Space** marks/unmarks a file for deletion,
  **Enter** deletes everything currently marked (after a confirmation dialog), **Escape**
  clears all marks.
- Deletion calls Jellyfin's own `ILibraryManager.DeleteItem(..., DeleteFileLocation: true)` —
  the same internal API the Jellyfin dashboard's own delete button uses.

Every API endpoint requires the Jellyfin Administrator role.

## Building

```sh
dotnet restore --locked-mode
dotnet build --configuration Release
dotnet test --configuration Release
tools/package-plugin.sh   # produces artifacts/package/aether-duplicate-finder-<version>.zip
```

## Installing

Add this URL as a plugin repository in Jellyfin's dashboard:

```
https://github.com/Bugstehude/aether-jellyfin-duplicate-finder/releases/latest/download/manifest.json
```

## License

GPL-3.0-or-later — see [LICENSE](LICENSE).
