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
