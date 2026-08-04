using Jellyfin.Plugin.DuplicateFinder.Application;

namespace Jellyfin.Plugin.DuplicateFinder.Tests;

public sealed class DuplicateScannerTests
{
    [Fact]
    public void GroupByHashDropsHashesWithOnlyOneFile()
    {
        var byHash = new Dictionary<string, List<DuplicateCandidate>>
        {
            ["aaa"] = [MakeCandidate("a1")],
        };

        var groups = DuplicateScanner.GroupByHash(byHash);

        Assert.Empty(groups);
    }

    [Fact]
    public void GroupByHashKeepsHashesWithTwoOrMoreFiles()
    {
        var byHash = new Dictionary<string, List<DuplicateCandidate>>
        {
            ["aaa"] = [MakeCandidate("a1")],
            ["bbb"] = [MakeCandidate("b1"), MakeCandidate("b2"), MakeCandidate("b3")],
        };

        var groups = DuplicateScanner.GroupByHash(byHash);

        var group = Assert.Single(groups);
        Assert.Equal("bbb", group.Sha256);
        Assert.Equal(3, group.Files.Count);
    }

    [Fact]
    public void GroupByHashWithNoDuplicatesAtAllReturnsEmpty()
    {
        var byHash = new Dictionary<string, List<DuplicateCandidate>>
        {
            ["aaa"] = [MakeCandidate("a1")],
            ["bbb"] = [MakeCandidate("b1")],
        };

        var groups = DuplicateScanner.GroupByHash(byHash);

        Assert.Empty(groups);
    }

    private static DuplicateCandidate MakeCandidate(string path) => new(
        Guid.NewGuid(), path, path, 1, 0, 0, 0, string.Empty);
}
