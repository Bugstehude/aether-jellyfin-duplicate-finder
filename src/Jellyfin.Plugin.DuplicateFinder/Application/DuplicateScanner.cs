using System.Diagnostics;
using System.Security.Cryptography;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.DuplicateFinder.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DuplicateFinder.Application;

/// <summary>
/// Tier 1 duplicate detection: exact byte-for-byte copies, found via a SHA-256 file hash. Two
/// files with the same hash are certainly the same file — no false positives are possible at
/// this tier, unlike metadata-based matching (title/IMDB id), which is what the existing
/// community Jellyfin duplicate finders rely on and can misfire on.
///
/// Three-phase pipeline so the expensive full-file hash only ever runs on files that are
/// actually plausible candidates, and never twice on an unmodified file across scans:
///   1. Group by file SIZE (free — no I/O). Two files can only be identical if they're the
///      same size, so a file with a unique size needs no further work at all.
///   2. Within a size group, hash only the first 64 KiB ("quick hash"). Identical files are
///      identical everywhere, including their first bytes, so two files that are actually the
///      same MUST also match here — this can only eliminate genuinely different files, never
///      miss a real duplicate. Cheap to read even for huge video files.
///   3. Only within a (size, quick hash) group — now a real candidate set, typically tiny —
///      compute the full SHA-256 over the whole file.
/// Both hash phases are cached per file (path + size + last-write time), in
/// <see cref="FileHashCache"/>, so a file unmodified since its last scan is never re-read at
/// all on a later scan.
///
/// Tier 2 (same recording, different encode/quality — where file hashes differ) is a planned
/// follow-up: duration match plus a perceptual frame hash via the same worker-subprocess
/// pattern the AETHER Analysis plugin already uses for ffmpeg. Not implemented yet.
/// </summary>
public sealed class DuplicateScanner(
    ILibraryManager libraryManager,
    DuplicateResultStore store,
    FileHashCache hashCache,
    ILogger<DuplicateScanner> logger) : IDisposable
{
    /// <summary>How much of a file the quick-hash phase reads. Enough to reliably tell distinct
    /// files apart in practice, cheap even for a multi-gigabyte video.</summary>
    private const int QuickHashBytes = 65536;

    // Serializes scans against each other: a second "Scan now" click while one is already
    // running would otherwise hash the same files twice for nothing.
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Cancels any in-flight scan on Dispose (host/plugin shutdown) — ScanAsync is started
    // fire-and-forget from the controller, so without this a shutdown mid-scan would otherwise
    // leave a background task still reading files and, eventually, hitting the disposed gate.
    private readonly CancellationTokenSource _lifetimeCancellation = new();

    /// <summary>Current/last scan status, polled by the config page.</summary>
    public ScanStatus Status { get; } = new();

    /// <summary>Enumerates the local video files eligible for scanning, per the configured scope.</summary>
    public IReadOnlyList<BaseItem> SelectItems()
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video],
            Recursive = true,
            IsVirtualItem = false
        };

        var scopeIds = ParseGuids(Configuration.ScanScopeIds);
        if (scopeIds.Length > 0)
        {
            query.AncestorIds = scopeIds;
        }

        return libraryManager.GetItemList(query);
    }

    /// <summary>
    /// Runs the three-phase scan described in the class docs and groups files that turn out to
    /// share a full hash. Runs to completion before returning — callers that want progress
    /// should poll <see cref="Status"/> from a separate request while this task is in flight.
    /// </summary>
    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeCancellation.Token);
        var scanToken = linkedCancellation.Token;

        if (!await _gate.WaitAsync(0, scanToken).ConfigureAwait(false))
        {
            // Already running — the caller's "Scan now" click is redundant, not an error.
            return;
        }

        try
        {
            var items = SelectItems();
            var candidates = items.SelectMany(BuildCandidates)
                .Select(pair => ToCandidate(pair.Item, pair.Path))
                .Where(candidate => candidate is not null)
                .Select(candidate => candidate!)
                // Two items can report the SAME physical file (linked alternate versions, or
                // the same library root reachable through two libraries/bind mounts) — without
                // this, that one file hashes identically to itself and is reported as its own
                // duplicate.
                .DistinctBy(candidate => Path.GetFullPath(candidate.Path))
                .ToList();
            Status.Running = true;
            Status.Checked = 0;
            Status.Total = candidates.Count;
            Status.LastError = null;

            var cache = await hashCache.LoadAsync(scanToken).ConfigureAwait(false);
            var cacheChanged = false;
            var checkpointStopwatch = Stopwatch.StartNew();

            // Checkpoints the cache to disk roughly every 30s of hashing (only when something
            // actually changed) rather than solely at the very end — on a large library the
            // full scan can take long enough that a crash/host-shutdown partway through would
            // otherwise discard every hash computed so far, forcing a full re-hash next time.
            async Task CheckpointIfDueAsync()
            {
                if (!cacheChanged || checkpointStopwatch.Elapsed < TimeSpan.FromSeconds(30))
                {
                    return;
                }

                await hashCache.SaveAsync(cache, scanToken).ConfigureAwait(false);
                cacheChanged = false;
                checkpointStopwatch.Restart();
            }

            // Phase 1 (free): a file with a size shared by no other candidate cannot possibly
            // be a byte-for-byte duplicate of anything — resolved without touching the file.
            var sizeGroups = GroupBySize(candidates);
            var sizeSharedCandidates = new List<DuplicateCandidate>();
            foreach (var group in sizeGroups)
            {
                if (group.Count < 2)
                {
                    Status.Checked += group.Count;
                    continue;
                }

                sizeSharedCandidates.AddRange(group);
            }

            // Phase 2 (cheap): quick hash, only for files that already share a size.
            var quickGroups = new Dictionary<(long Size, string QuickHash), List<DuplicateCandidate>>();
            foreach (var candidate in sizeSharedCandidates)
            {
                scanToken.ThrowIfCancellationRequested();
                var quickHash = await GetOrComputeQuickHashAsync(candidate, cache, changed => cacheChanged |= changed, scanToken)
                    .ConfigureAwait(false);
                if (quickHash is null)
                {
                    logger.LogWarning("Skipping unreadable file during duplicate scan: {Path}", candidate.Path);
                    Status.Checked++;
                    continue;
                }

                var key = (candidate.SizeBytes, quickHash);
                if (!quickGroups.TryGetValue(key, out var list))
                {
                    list = [];
                    quickGroups[key] = list;
                }

                list.Add(candidate);
                await CheckpointIfDueAsync().ConfigureAwait(false);
            }

            // Phase 3 (expensive, only for real candidates): the full file hash.
            var byFullHash = new Dictionary<string, List<DuplicateCandidate>>(StringComparer.Ordinal);
            foreach (var group in quickGroups.Values)
            {
                if (group.Count < 2)
                {
                    Status.Checked += group.Count;
                    continue;
                }

                foreach (var candidate in group)
                {
                    scanToken.ThrowIfCancellationRequested();
                    var fullHash = await GetOrComputeFullHashAsync(candidate, cache, changed => cacheChanged |= changed, scanToken)
                        .ConfigureAwait(false);
                    Status.Checked++;
                    if (fullHash is null)
                    {
                        logger.LogWarning("Skipping unreadable file during duplicate scan: {Path}", candidate.Path);
                        continue;
                    }

                    if (!byFullHash.TryGetValue(fullHash, out var list))
                    {
                        list = [];
                        byFullHash[fullHash] = list;
                    }

                    list.Add(candidate);
                    await CheckpointIfDueAsync().ConfigureAwait(false);
                }
            }

            var groups = GroupByHash(byFullHash);

            // Drop entries for paths no longer among this scan's candidates (deleted, moved, or
            // no longer in the configured scope) — otherwise the cache grows forever and a stale
            // entry could theoretically be matched again if a future file ever reused the exact
            // same path, size, and last-write time.
            var currentPaths = new HashSet<string>(candidates.Select(candidate => candidate.Path), StringComparer.Ordinal);
            var prunedCount = cache.Keys.Count(path => !currentPaths.Contains(path));
            if (prunedCount > 0)
            {
                foreach (var staleKey in cache.Keys.Where(path => !currentPaths.Contains(path)).ToList())
                {
                    cache.Remove(staleKey);
                }

                cacheChanged = true;
            }

            if (cacheChanged)
            {
                await hashCache.SaveAsync(cache, scanToken).ConfigureAwait(false);
            }

            await store.SaveAsync(groups, scanToken).ConfigureAwait(false);
            Status.LastFinishedAt = DateTimeOffset.UtcNow;
            Status.LastGroupCount = groups.Count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Status.LastError = exception.Message;
            logger.LogError(exception, "Duplicate scan failed");
        }
        finally
        {
            Status.Running = false;
            try
            {
                _gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Dispose() (host/plugin shutdown) raced ahead of this background task's own
                // cleanup — nothing left to release into at that point, and nothing further
                // in this method runs after the finally block anyway.
            }
        }
    }

    /// <summary>
    /// Groups candidates by file size. Pure and independent of file I/O beyond the size each
    /// candidate already carries, so it's directly unit-testable.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<DuplicateCandidate>> GroupBySize(IReadOnlyList<DuplicateCandidate> candidates) =>
        candidates.GroupBy(c => c.SizeBytes).Select(g => (IReadOnlyList<DuplicateCandidate>)g.ToList()).ToList();

    /// <summary>
    /// Groups hashed candidates that share a hash, dropping singletons (a hash with only one
    /// file isn't a duplicate of anything). Pure and independent of file I/O or Jellyfin's
    /// library manager, so it's directly unit-testable.
    /// </summary>
    public static IReadOnlyList<DuplicateGroup> GroupByHash(IReadOnlyDictionary<string, List<DuplicateCandidate>> byHash) =>
        byHash
            .Where(pair => pair.Value.Count > 1)
            .Select(pair => new DuplicateGroup(pair.Key, pair.Value))
            .ToList();

    private static IEnumerable<(BaseItem Item, string Path)> BuildCandidates(BaseItem item) =>
        item.GetMediaSources(enablePathSubstitution: false)
            .Where(source => !source.IsRemote && !string.IsNullOrWhiteSpace(source.Path) && File.Exists(source.Path))
            .Select(source => (item, source.Path));

    /// <summary>
    /// Builds a candidate, or null if the file's size can't be read (permission error, it
    /// vanished between enumeration and here, ...). A stat failure used to fall back to
    /// SizeBytes=0, which silently pooled every such broken file into one bogus "same size"
    /// group together — now they're excluded from the scan entirely instead.
    /// </summary>
    private static DuplicateCandidate? ToCandidate(BaseItem item, string path)
    {
        var videoStream = item.GetMediaStreams()
            .FirstOrDefault(stream => stream.Type == MediaBrowser.Model.Entities.MediaStreamType.Video);
        long sizeBytes;
        try
        {
            sizeBytes = new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return new DuplicateCandidate(
            ItemId: item.Id,
            ItemName: item.Name ?? path,
            Path: path,
            SizeBytes: sizeBytes,
            Width: videoStream?.Width ?? 0,
            Height: videoStream?.Height ?? 0,
            DurationSeconds: item.RunTimeTicks.HasValue ? TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalSeconds : 0,
            Codec: videoStream?.Codec ?? string.Empty);
    }

    /// <summary>
    /// Returns the cache entry to build on: the existing one if it's still valid for this
    /// file's current size/last-write time, otherwise a fresh, empty one — a stale entry's old
    /// hashes belong to different file content and must never be reused.
    /// </summary>
    internal static CachedFileHash GetFreshOrNewEntry(
        string path, long sizeBytes, long lastWriteTimeUtcTicks, IReadOnlyDictionary<string, CachedFileHash> cache) =>
        cache.TryGetValue(path, out var entry) && entry.SizeBytes == sizeBytes && entry.LastWriteTimeUtcTicks == lastWriteTimeUtcTicks
            ? entry
            : new CachedFileHash(path, sizeBytes, lastWriteTimeUtcTicks, QuickHash: null, FullHash: null);

    /// <summary>
    /// <see cref="File.GetLastWriteTimeUtc(string)"/> does NOT throw for a missing file — it returns a
    /// sentinel (1601-01-01) — so an explicit existence check is required, or a file that
    /// vanished between candidate-building and hashing would silently get treated as "changed"
    /// (sentinel mtime ≠ cached mtime) rather than skipped outright.
    /// </summary>
    private static bool TryGetLastWriteTimeUtcTicks(string path, out long ticks)
    {
        if (!File.Exists(path))
        {
            ticks = 0;
            return false;
        }

        try
        {
            ticks = File.GetLastWriteTimeUtc(path).Ticks;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ticks = 0;
            return false;
        }
    }

    internal static async Task<string?> GetOrComputeQuickHashAsync(
        DuplicateCandidate candidate,
        Dictionary<string, CachedFileHash> cache,
        Action<bool> reportChanged,
        CancellationToken cancellationToken)
    {
        if (!TryGetLastWriteTimeUtcTicks(candidate.Path, out var mtimeTicks))
        {
            return null;
        }

        var entry = GetFreshOrNewEntry(candidate.Path, candidate.SizeBytes, mtimeTicks, cache);
        if (entry.QuickHash is not null)
        {
            cache[candidate.Path] = entry;
            return entry.QuickHash;
        }

        string hash;
        try
        {
            hash = await QuickHashFileAsync(candidate.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        CacheIfStillUnchanged(candidate, mtimeTicks, cache, entry with { QuickHash = hash }, reportChanged);
        return hash;
    }

    internal static async Task<string?> GetOrComputeFullHashAsync(
        DuplicateCandidate candidate,
        Dictionary<string, CachedFileHash> cache,
        Action<bool> reportChanged,
        CancellationToken cancellationToken)
    {
        if (!TryGetLastWriteTimeUtcTicks(candidate.Path, out var mtimeTicks))
        {
            return null;
        }

        var entry = GetFreshOrNewEntry(candidate.Path, candidate.SizeBytes, mtimeTicks, cache);
        if (entry.FullHash is not null)
        {
            cache[candidate.Path] = entry;
            return entry.FullHash;
        }

        string hash;
        try
        {
            hash = await HashFileAsync(candidate.Path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        CacheIfStillUnchanged(candidate, mtimeTicks, cache, entry with { FullHash = hash }, reportChanged);
        return hash;
    }

    /// <summary>
    /// Only writes the freshly-computed hash into the cache if the file's size/mtime are still
    /// what they were right before we started reading it — narrows (does not eliminate; nothing
    /// can, short of a filesystem-level lock) the window where a file modified DURING hashing
    /// would otherwise cache a hash that matches neither its old nor its new content. The
    /// hash just computed is still returned to the caller either way — it's valid for THIS
    /// scan's own grouping regardless, it just must not be trusted on a LATER scan.
    /// </summary>
    private static void CacheIfStillUnchanged(
        DuplicateCandidate candidate, long mtimeBeforeHashing, Dictionary<string, CachedFileHash> cache, CachedFileHash updated, Action<bool> reportChanged)
    {
        long currentSize;
        try
        {
            currentSize = new FileInfo(candidate.Path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (!TryGetLastWriteTimeUtcTicks(candidate.Path, out var mtimeAfterHashing)
            || mtimeAfterHashing != mtimeBeforeHashing
            || currentSize != candidate.SizeBytes)
        {
            return;
        }

        cache[candidate.Path] = updated;
        reportChanged(true);
    }

    private static async Task<string> QuickHashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 16, useAsync: true);
        var toRead = (int)Math.Min(QuickHashBytes, stream.Length);
        var buffer = new byte[toRead];
        var offset = 0;
        while (offset < toRead)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, toRead - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        var hash = SHA256.HashData(buffer.AsSpan(0, offset));
        return Convert.ToHexStringLower(hash);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1 << 20, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static Guid[] ParseGuids(string[]? values) => (values ?? [])
        .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
        .Where(id => id != Guid.Empty)
        .ToArray();

    private static PluginConfiguration Configuration =>
        Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public void Dispose()
    {
        // Cancel FIRST: an in-flight ScanAsync observes this at its next
        // ThrowIfCancellationRequested/await and unwinds through its own finally block (which
        // defensively tolerates the gate already being disposed by the time it gets there).
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        _gate.Dispose();
    }
}
