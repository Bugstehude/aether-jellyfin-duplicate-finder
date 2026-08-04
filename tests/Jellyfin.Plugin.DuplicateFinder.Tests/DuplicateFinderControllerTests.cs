using Jellyfin.Plugin.DuplicateFinder.Api;
using Jellyfin.Plugin.DuplicateFinder.Application;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Jellyfin.Plugin.DuplicateFinder.Tests;

/// <summary>
/// Tests for the one endpoint that can permanently destroy data. Focused on the two
/// server-side guards that don't trust the client: refusing a request that would empty a
/// stored group, and never letting one item's failure abort bookkeeping for the rest.
/// </summary>
public sealed class DuplicateFinderControllerTests : IDisposable
{
    private readonly string _dataFolder = Path.Combine(Path.GetTempPath(), "aether-dupfinder-controller-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_dataFolder))
        {
            Directory.Delete(_dataFolder, recursive: true);
        }
    }

    private static DuplicateCandidate MakeFile(Guid itemId, string path) => new(
        itemId, path, path, 1, 0, 0, 0, string.Empty);

    private (DuplicateFinderController Controller, ILibraryManager LibraryManager, DuplicateResultStore Store) CreateController()
    {
        var libraryManager = Substitute.For<ILibraryManager>();
        var store = new DuplicateResultStore(_dataFolder);
        var scanner = new DuplicateScanner(libraryManager, store, new FileHashCache(_dataFolder), NullLogger<DuplicateScanner>.Instance);
        var controller = new DuplicateFinderController(libraryManager, scanner, store, NullLogger<DuplicateFinderController>.Instance);
        return (controller, libraryManager, store);
    }

    [Fact]
    public async Task DeleteRemovesItemAndPrunesItFromTheStoredGroup()
    {
        var (controller, libraryManager, store) = CreateController();
        var keepId = Guid.NewGuid();
        var deleteId = Guid.NewGuid();
        await store.SaveAsync(
            [new DuplicateGroup("hash1", [MakeFile(keepId, "/a"), MakeFile(deleteId, "/b")])],
            CancellationToken.None);
        var item = Substitute.For<BaseItem>();
        libraryManager.GetItemById(deleteId).Returns(item);

        var result = await controller.Delete(
            new DuplicateFinderController.DeleteRequest { ItemIds = [deleteId.ToString()] },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        libraryManager.Received(1).DeleteItem(item, Arg.Any<DeleteOptions>(), notifyParentItem: true);
        var remaining = await store.LoadAsync(CancellationToken.None);
        Assert.Empty(remaining); // the group had exactly 2 files; removing 1 drops it below the "duplicate" threshold
    }

    [Fact]
    public async Task DeleteRefusesToEmptyAGroupEntirely()
    {
        var (controller, libraryManager, store) = CreateController();
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        await store.SaveAsync(
            [new DuplicateGroup("hash1", [MakeFile(idA, "/a"), MakeFile(idB, "/b")])],
            CancellationToken.None);
        var itemA = Substitute.For<BaseItem>();
        var itemB = Substitute.For<BaseItem>();
        libraryManager.GetItemById(idA).Returns(itemA);
        libraryManager.GetItemById(idB).Returns(itemB);

        // Requesting BOTH files in a two-file group would leave nothing — must be refused.
        await controller.Delete(
            new DuplicateFinderController.DeleteRequest { ItemIds = [idA.ToString(), idB.ToString()] },
            CancellationToken.None);

        libraryManager.DidNotReceive().DeleteItem(Arg.Any<BaseItem>(), Arg.Any<DeleteOptions>(), Arg.Any<bool>());
        var remaining = await store.LoadAsync(CancellationToken.None);
        var group = Assert.Single(remaining);
        Assert.Equal(2, group.Files.Count); // untouched
    }

    [Fact]
    public async Task DeletingOneOfThreeInAGroupIsAllowed()
    {
        var (controller, libraryManager, store) = CreateController();
        var idA = Guid.NewGuid();
        var idB = Guid.NewGuid();
        var idC = Guid.NewGuid();
        await store.SaveAsync(
            [new DuplicateGroup("hash1", [MakeFile(idA, "/a"), MakeFile(idB, "/b"), MakeFile(idC, "/c")])],
            CancellationToken.None);
        var itemA = Substitute.For<BaseItem>();
        libraryManager.GetItemById(idA).Returns(itemA);

        await controller.Delete(
            new DuplicateFinderController.DeleteRequest { ItemIds = [idA.ToString()] },
            CancellationToken.None);

        libraryManager.Received(1).DeleteItem(itemA, Arg.Any<DeleteOptions>(), notifyParentItem: true);
        var remaining = await store.LoadAsync(CancellationToken.None);
        var group = Assert.Single(remaining);
        Assert.Equal(2, group.Files.Count);
    }

    [Fact]
    public async Task OneItemFailingToDeleteDoesNotStopTheRestOrSkipPruning()
    {
        var (controller, libraryManager, store) = CreateController();
        var idGood = Guid.NewGuid();
        var idBad = Guid.NewGuid();
        var idKeep = Guid.NewGuid();
        await store.SaveAsync(
            [new DuplicateGroup("hash1", [MakeFile(idGood, "/a"), MakeFile(idBad, "/b"), MakeFile(idKeep, "/c")])],
            CancellationToken.None);
        var itemGood = Substitute.For<BaseItem>();
        var itemBad = Substitute.For<BaseItem>();
        libraryManager.GetItemById(idGood).Returns(itemGood);
        libraryManager.GetItemById(idBad).Returns(itemBad);
        // Reference-equality matcher, not the bare `itemBad` argument: two BaseItem
        // substitutes with no Id assigned compare equal to each other under NSubstitute's
        // default argument matching (BaseItem.Equals is Id-based), so a plain `itemBad`
        // argument here would ALSO match calls carrying itemGood.
        libraryManager
            .When(lm => lm.DeleteItem(Arg.Is<BaseItem>(x => ReferenceEquals(x, itemBad)), Arg.Any<DeleteOptions>(), Arg.Any<bool>()))
            .Do(_ => throw new InvalidOperationException("simulated provider failure"));

        await controller.Delete(
            new DuplicateFinderController.DeleteRequest { ItemIds = [idGood.ToString(), idBad.ToString()] },
            CancellationToken.None);

        // Verified via the resulting store state below rather than a Received() call count:
        // BaseItem substitutes with no Id assigned compare equal to each other under
        // NSubstitute's default argument matching, which would make a DeleteItem(itemGood, ...)
        // assertion ambiguous between the two configured items here.
        var remaining = await store.LoadAsync(CancellationToken.None);
        var group = Assert.Single(remaining);
        // idGood was pruned (it succeeded); idBad and idKeep remain (idBad failed, idKeep was
        // never requested) — an arbitrary exception type on one item didn't lose this bookkeeping.
        Assert.Equal(2, group.Files.Count);
        Assert.DoesNotContain(group.Files, f => f.ItemId == idGood);
    }

    [Fact]
    public async Task InvalidIdIsReportedAndNeverReachesDeleteItem()
    {
        var (controller, libraryManager, _) = CreateController();

        var result = await controller.Delete(
            new DuplicateFinderController.DeleteRequest { ItemIds = ["not-a-guid"] },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        libraryManager.DidNotReceive().DeleteItem(Arg.Any<BaseItem>(), Arg.Any<DeleteOptions>(), Arg.Any<bool>());
    }
}
