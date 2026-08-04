using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Jellyfin.Plugin.DuplicateFinder.Application;

namespace Jellyfin.Plugin.DuplicateFinder.Tests;

/// <summary>
/// End-to-end tests for <see cref="DuplicateScanner.ScanAsync"/> itself — the three-phase
/// pipeline's wiring, progress accounting, and re-entrancy guard aren't exercised by the pure
/// logic tests in <see cref="DuplicateScannerTests"/> or the cache tests in
/// <see cref="DuplicateScannerHashingTests"/>, which each only cover one phase in isolation.
/// </summary>
public sealed class DuplicateScannerScanAsyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aether-dupfinder-scanasync-tests-" + Guid.NewGuid());
    private readonly string _dataFolder;

    public DuplicateScannerScanAsyncTests()
    {
        Directory.CreateDirectory(_dir);
        _dataFolder = Path.Combine(_dir, "data");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static BaseItem MakeItem(Guid id, string name, string path)
    {
        var item = Substitute.For<BaseItem>();
        item.Id = id;
        item.Name = name;
        item.GetMediaSources(enablePathSubstitution: false).Returns(new List<MediaSourceInfo>
        {
            new() { Path = path, IsRemote = false },
        });
        item.GetMediaStreams().Returns([]);
        return item;
    }

    [Fact]
    public async Task ScanAsyncGroupsFilesWithIdenticalContentAndLeavesUniqueFilesOut()
    {
        var sameContent = new byte[200_000];
        Random.Shared.NextBytes(sameContent);
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var idC = Guid.NewGuid();
        var itemA = MakeItem(idA, "dup-a", WriteFile("dup-a.mkv", sameContent));
        var itemB = MakeItem(idB, "dup-b", WriteFile("dup-b.mkv", sameContent));
        var itemC = MakeItem(idC, "unique", WriteFile("unique.mkv", "totally different, unique content"u8.ToArray()));

        var libraryManager = Substitute.For<ILibraryManager>();
        libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([itemA, itemB, itemC]);
        var store = new DuplicateResultStore(_dataFolder);
        using var scanner = new DuplicateScanner(libraryManager, store, new FileHashCache(_dataFolder), NullLogger<DuplicateScanner>.Instance);

        await scanner.ScanAsync(CancellationToken.None);

        Assert.False(scanner.Status.Running);
        Assert.Null(scanner.Status.LastError);
        Assert.Equal(3, scanner.Status.Total);
        Assert.Equal(3, scanner.Status.Checked);
        Assert.Equal(1, scanner.Status.LastGroupCount);
        Assert.NotNull(scanner.Status.LastFinishedAt);

        var groups = await store.LoadAsync(CancellationToken.None);
        var group = Assert.Single(groups);
        Assert.Equal(2, group.Files.Count);
        Assert.Contains(group.Files, f => f.ItemId == idA);
        Assert.Contains(group.Files, f => f.ItemId == idB);
        Assert.DoesNotContain(group.Files, f => f.ItemId == idC);
    }

    [Fact]
    public async Task ScanAsyncWithNoSharedSizesProducesNoGroups()
    {
        var itemA = MakeItem(Guid.NewGuid(), "a", WriteFile("a.mkv", new byte[10]));
        var itemB = MakeItem(Guid.NewGuid(), "b", WriteFile("b.mkv", new byte[20]));

        var libraryManager = Substitute.For<ILibraryManager>();
        libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([itemA, itemB]);
        var store = new DuplicateResultStore(_dataFolder);
        using var scanner = new DuplicateScanner(libraryManager, store, new FileHashCache(_dataFolder), NullLogger<DuplicateScanner>.Instance);

        await scanner.ScanAsync(CancellationToken.None);

        Assert.Equal(0, scanner.Status.LastGroupCount);
        var groups = await store.LoadAsync(CancellationToken.None);
        Assert.Empty(groups);
    }

    [Fact]
    public async Task RescanPrunesCacheEntriesForFilesNoLongerInScope()
    {
        // Same size (so both reach the quick-hash phase and get cached) but different content
        // (so they don't group as duplicates — content isn't the point of this test).
        var pathA = WriteFile("a.mkv", "aaaaaaaaaa"u8.ToArray());
        var pathB = WriteFile("b.mkv", "bbbbbbbbbb"u8.ToArray());
        var itemA = MakeItem(Guid.NewGuid(), "a", pathA);
        var itemB = MakeItem(Guid.NewGuid(), "b", pathB);
        var libraryManager = Substitute.For<ILibraryManager>();
        libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([itemA, itemB]);
        var store = new DuplicateResultStore(_dataFolder);
        var hashCache = new FileHashCache(_dataFolder);
        using (var scanner = new DuplicateScanner(libraryManager, store, hashCache, NullLogger<DuplicateScanner>.Instance))
        {
            await scanner.ScanAsync(CancellationToken.None);
        }

        var cacheAfterFirstScan = await hashCache.LoadAsync(CancellationToken.None);
        Assert.Contains(pathB, cacheAfterFirstScan.Keys);

        // "b" drops out of scope (deleted from the library) — a rescan must forget its cache
        // entry, not carry it forward forever.
        libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns([itemA]);
        using (var scanner = new DuplicateScanner(libraryManager, store, hashCache, NullLogger<DuplicateScanner>.Instance))
        {
            await scanner.ScanAsync(CancellationToken.None);
        }

        var cacheAfterSecondScan = await hashCache.LoadAsync(CancellationToken.None);
        Assert.Contains(pathA, cacheAfterSecondScan.Keys);
        Assert.DoesNotContain(pathB, cacheAfterSecondScan.Keys);
    }

    [Fact]
    public async Task ConcurrentScanCallWhileOneIsRunningIsIgnoredNotQueued()
    {
        var content = new byte[500_000];
        Random.Shared.NextBytes(content);
        var items = Enumerable.Range(0, 20)
            .Select(i => MakeItem(Guid.NewGuid(), $"f{i}", WriteFile($"f{i}.mkv", content)))
            .ToList<BaseItem>();

        var libraryManager = Substitute.For<ILibraryManager>();
        libraryManager.GetItemList(Arg.Any<InternalItemsQuery>()).Returns(items);
        var store = new DuplicateResultStore(_dataFolder);
        using var scanner = new DuplicateScanner(libraryManager, store, new FileHashCache(_dataFolder), NullLogger<DuplicateScanner>.Instance);

        var first = scanner.ScanAsync(CancellationToken.None);
        // Fired while the first scan is (almost certainly) still in flight — the gate must
        // make this a no-op rather than a second concurrent pass over the same files.
        var second = scanner.ScanAsync(CancellationToken.None);
        await Task.WhenAll(first, second);

        Assert.False(scanner.Status.Running);
        Assert.Equal(1, scanner.Status.LastGroupCount);
        var groups = await store.LoadAsync(CancellationToken.None);
        var group = Assert.Single(groups);
        Assert.Equal(20, group.Files.Count);
    }
}
