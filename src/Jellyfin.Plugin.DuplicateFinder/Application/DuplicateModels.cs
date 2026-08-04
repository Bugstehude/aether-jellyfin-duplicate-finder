namespace Jellyfin.Plugin.DuplicateFinder.Application;

/// <summary>One file found to share its content hash with at least one other file.</summary>
/// <param name="ItemId">The Jellyfin library item id this file belongs to.</param>
/// <param name="ItemName">Display name, for the review list — a path alone is hard to place among many items.</param>
/// <param name="Path">Absolute file path on the server.</param>
/// <param name="SizeBytes">File size in bytes.</param>
/// <param name="Width">Video width in pixels, 0 if unknown.</param>
/// <param name="Height">Video height in pixels, 0 if unknown.</param>
/// <param name="DurationSeconds">Runtime in seconds, 0 if unknown.</param>
/// <param name="Codec">Video codec name, empty if unknown.</param>
public sealed record DuplicateCandidate(
    Guid ItemId,
    string ItemName,
    string Path,
    long SizeBytes,
    int Width,
    int Height,
    double DurationSeconds,
    string Codec);

/// <summary>
/// A set of files sharing the same content hash (Tier 1 — exact byte-for-byte duplicates).
/// Near-duplicate detection (same recording, different encode) is a planned Tier 2, not yet
/// implemented — see the README.
/// </summary>
/// <param name="Sha256">The shared file hash, hex-encoded.</param>
/// <param name="Files">Every file sharing this hash — always 2 or more.</param>
public sealed record DuplicateGroup(string Sha256, IReadOnlyList<DuplicateCandidate> Files);

/// <summary>
/// A file's cached hash results, keyed by path — see <see cref="FileHashCache"/>. Reused across
/// scans as long as the file's size and last-write time haven't changed, so an unmodified file
/// is never re-read once it's been through a full scan.
/// </summary>
/// <param name="Path">Absolute file path — the cache key.</param>
/// <param name="SizeBytes">File size at the time these hashes were computed.</param>
/// <param name="LastWriteTimeUtcTicks">The file's last-write time (UTC, ticks) at that time.</param>
/// <param name="QuickHash">Hash of the first bytes only (see <see cref="DuplicateScanner"/>'s quick-hash pass), or null if never computed.</param>
/// <param name="FullHash">Full SHA-256 of the whole file, or null if never computed (most files never need this — only those that already share a size and quick hash with another file).</param>
public sealed record CachedFileHash(
    string Path,
    long SizeBytes,
    long LastWriteTimeUtcTicks,
    string? QuickHash,
    string? FullHash);

/// <summary>Status of an in-progress or last-completed scan, polled by the config page.</summary>
public sealed class ScanStatus
{
    /// <summary>Whether a scan is currently running.</summary>
    public bool Running { get; set; }

    /// <summary>Files hashed so far in the current/last run.</summary>
    public int Checked { get; set; }

    /// <summary>Total files to hash in the current/last run.</summary>
    public int Total { get; set; }

    /// <summary>When the last completed run finished, if any.</summary>
    public DateTimeOffset? LastFinishedAt { get; set; }

    /// <summary>Duplicate groups found in the last completed run.</summary>
    public int LastGroupCount { get; set; }

    /// <summary>Set if the last run failed with an unexpected error.</summary>
    public string? LastError { get; set; }
}
