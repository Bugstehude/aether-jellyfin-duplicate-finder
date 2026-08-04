using System.Text.Json;

namespace Jellyfin.Plugin.DuplicateFinder.Application;

/// <summary>
/// Persists the last scan's duplicate groups to a JSON file in the plugin's data folder, so a
/// server restart doesn't lose results the admin hasn't reviewed yet. Deliberately a flat JSON
/// file rather than a database — the whole result set is small (a few hundred groups at most)
/// and is always read/written as one unit, so a database would only add migration/EF-Core
/// machinery for no real benefit here.
/// </summary>
public sealed class DuplicateResultStore(string dataFolderPath) : IDisposable
{
    private readonly string _path = Path.Combine(dataFolderPath, "duplicate-groups.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>Reads the currently stored groups, or an empty list if none have been saved yet.</summary>
    public async Task<IReadOnlyList<DuplicateGroup>> LoadAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            await using var stream = File.OpenRead(_path);
            var groups = await JsonSerializer.DeserializeAsync<List<DuplicateGroup>>(
                stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            return groups ?? [];
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Overwrites the stored groups. Written to a temp file first and renamed into place, so a
    /// crash mid-write never leaves a half-written, unparseable result file behind.
    /// </summary>
    public async Task SaveAsync(IReadOnlyList<DuplicateGroup> groups, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(dataFolderPath);
            var tempPath = _path + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, groups, SerializerOptions, cancellationToken)
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
