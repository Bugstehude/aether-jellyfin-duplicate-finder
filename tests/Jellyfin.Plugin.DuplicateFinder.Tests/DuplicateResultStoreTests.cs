using Jellyfin.Plugin.DuplicateFinder.Application;

namespace Jellyfin.Plugin.DuplicateFinder.Tests;

public sealed class DuplicateResultStoreTests : IDisposable
{
    private readonly string _dataFolder = Path.Combine(Path.GetTempPath(), "aether-dupfinder-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dataFolder))
        {
            Directory.Delete(_dataFolder, recursive: true);
        }
    }

    [Fact]
    public async Task LoadWithNoFileYetReturnsEmptyList()
    {
        using var store = new DuplicateResultStore(_dataFolder);

        var result = await store.LoadAsync(CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SaveThenLoadRoundTripsGroups()
    {
        using var store = new DuplicateResultStore(_dataFolder);
        var candidate = new DuplicateCandidate(
            ItemId: Guid.NewGuid(),
            ItemName: "Movie (2020)",
            Path: "/media/movies/movie.mkv",
            SizeBytes: 123456789,
            Width: 1920,
            Height: 1080,
            DurationSeconds: 5400,
            Codec: "hevc");
        var group = new DuplicateGroup("deadbeef", [candidate, candidate with { Path = "/media/movies2/movie.mkv" }]);

        await store.SaveAsync([group], CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        var loadedGroup = Assert.Single(loaded);
        Assert.Equal("deadbeef", loadedGroup.Sha256);
        Assert.Equal(2, loadedGroup.Files.Count);
        Assert.Equal(candidate.ItemId, loadedGroup.Files[0].ItemId);
        Assert.Equal("/media/movies2/movie.mkv", loadedGroup.Files[1].Path);
    }

    [Fact]
    public async Task SavingTwiceOverwritesRatherThanAppends()
    {
        using var store = new DuplicateResultStore(_dataFolder);
        var groupA = new DuplicateGroup("aaa", [MakeCandidate(), MakeCandidate()]);
        var groupB = new DuplicateGroup("bbb", [MakeCandidate(), MakeCandidate()]);

        await store.SaveAsync([groupA], CancellationToken.None);
        await store.SaveAsync([groupB], CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        var loadedGroup = Assert.Single(loaded);
        Assert.Equal("bbb", loadedGroup.Sha256);
    }

    private static DuplicateCandidate MakeCandidate() => new(
        Guid.NewGuid(), "item", "/x", 1, 0, 0, 0, string.Empty);
}
