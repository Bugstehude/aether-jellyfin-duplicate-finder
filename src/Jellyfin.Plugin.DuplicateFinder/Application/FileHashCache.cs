using System.Text.Json;

namespace Jellyfin.Plugin.DuplicateFinder.Application;

/// <summary>
/// Persists per-file quick/full hash results across scans, keyed by absolute path, so an
/// unmodified file is never re-read on a later scan — see <see cref="DuplicateScanner"/> for how
/// the cache entries are validated (size + last-write time must still match) and used. Same
/// atomic-write-then-rename pattern as <see cref="DuplicateResultStore"/>, for the same reason
/// (a crash mid-write must never leave a half-written, unparseable cache file behind).
/// </summary>
public sealed class FileHashCache(string dataFolderPath) : IDisposable
{
    private readonly string _path = Path.Combine(dataFolderPath, "file-hash-cache.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>Reads the cache, or an empty one if nothing has been saved yet.</summary>
    public async Task<Dictionary<string, CachedFileHash>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            await using var stream = File.OpenRead(_path);
            var entries = await JsonSerializer.DeserializeAsync<List<CachedFileHash>>(
                stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            return (entries ?? []).ToDictionary(entry => entry.Path, StringComparer.Ordinal);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Overwrites the cache with the given entries.</summary>
    public async Task SaveAsync(IReadOnlyDictionary<string, CachedFileHash> entries, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(dataFolderPath);
            var tempPath = _path + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, entries.Values.ToList(), SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(tempPath, _path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
