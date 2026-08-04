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
/// Tier 2 (same recording, different encode/quality — where file hashes differ) is a planned
/// follow-up: duration match plus a perceptual frame hash via the same worker-subprocess
/// pattern the AETHER Analysis plugin already uses for ffmpeg. Not implemented yet.
/// </summary>
public sealed class DuplicateScanner(
    ILibraryManager libraryManager,
    DuplicateResultStore store,
    ILogger<DuplicateScanner> logger) : IDisposable
{
    // Serializes scans against each other: a second "Scan now" click while one is already
    // running would otherwise hash the same files twice for nothing.
    private readonly SemaphoreSlim _gate = new(1, 1);

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
    /// Hashes every eligible local video file and groups those that share a hash. Runs to
    /// completion before returning — callers that want progress should poll <see cref="Status"/>
    /// from a separate request while this task is in flight.
    /// </summary>
    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            // Already running — the caller's "Scan now" click is redundant, not an error.
            return;
        }

        try
        {
            var items = SelectItems();
            var candidates = items.SelectMany(BuildCandidates).ToList();
            Status.Running = true;
            Status.Checked = 0;
            Status.Total = candidates.Count;
            Status.LastError = null;

            var byHash = new Dictionary<string, List<DuplicateCandidate>>(StringComparer.Ordinal);
            foreach (var (item, path) in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string hash;
                try
                {
                    hash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
                }
                catch (IOException exception)
                {
                    logger.LogWarning(exception, "Skipping unreadable file during duplicate scan: {Path}", path);
                    Status.Checked++;
                    continue;
                }

                var candidate = ToCandidate(item, path);
                if (!byHash.TryGetValue(hash, out var list))
                {
                    list = [];
                    byHash[hash] = list;
                }

                list.Add(candidate);
                Status.Checked++;
            }

            var groups = GroupByHash(byHash);

            await store.SaveAsync(groups, cancellationToken).ConfigureAwait(false);
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
            _gate.Release();
        }
    }

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

    private static DuplicateCandidate ToCandidate(BaseItem item, string path)
    {
        var videoStream = item.GetMediaStreams()
            .FirstOrDefault(stream => stream.Type == MediaBrowser.Model.Entities.MediaStreamType.Video);
        long sizeBytes;
        try
        {
            sizeBytes = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            sizeBytes = 0;
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
    public void Dispose() => _gate.Dispose();
}
