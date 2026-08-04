using Jellyfin.Plugin.DuplicateFinder.Application;

namespace Jellyfin.Plugin.DuplicateFinder.Tests;

public sealed class FileHashCacheTests : IDisposable
{
    private readonly string _dataFolder = Path.Combine(Path.GetTempPath(), "aether-dupfinder-hashcache-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dataFolder))
        {
            Directory.Delete(_dataFolder, recursive: true);
        }
    }

    [Fact]
    public async Task LoadWithNoFileYetReturnsEmptyCache()
    {
        using var cache = new FileHashCache(_dataFolder);

        var result = await cache.LoadAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SaveThenLoadRoundTripsEntriesKeyedByPath()
    {
        using var cache = new FileHashCache(_dataFolder);
        var entries = new Dictionary<string, CachedFileHash>
        {
            ["/media/a.mkv"] = new CachedFileHash("/media/a.mkv", 12345, 999, "quick1", "full1"),
            ["/media/b.mkv"] = new CachedFileHash("/media/b.mkv", 6789, 111, "quick2", null),
        };

        await cache.SaveAsync(entries, CancellationToken.None);
        var loaded = await cache.LoadAsync(CancellationToken.None);

        Assert.Equal(2, loaded.Count);
        Assert.Equal("full1", loaded["/media/a.mkv"].FullHash);
        Assert.Null(loaded["/media/b.mkv"].FullHash);
        Assert.Equal("quick2", loaded["/media/b.mkv"].QuickHash);
    }

    [Fact]
    public async Task SavingTwiceOverwritesRatherThanAppends()
    {
        using var cache = new FileHashCache(_dataFolder);
        var first = new Dictionary<string, CachedFileHash>
        {
            ["/media/a.mkv"] = new CachedFileHash("/media/a.mkv", 1, 1, "q", "f"),
        };
        var second = new Dictionary<string, CachedFileHash>
        {
            ["/media/b.mkv"] = new CachedFileHash("/media/b.mkv", 2, 2, "q2", "f2"),
        };

        await cache.SaveAsync(first, CancellationToken.None);
        await cache.SaveAsync(second, CancellationToken.None);
        var loaded = await cache.LoadAsync(CancellationToken.None);

        var entry = Assert.Single(loaded);
        Assert.Equal("/media/b.mkv", entry.Key);
    }
}
