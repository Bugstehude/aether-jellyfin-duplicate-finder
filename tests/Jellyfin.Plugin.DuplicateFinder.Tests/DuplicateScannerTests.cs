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

    [Fact]
    public void GroupBySizeGroupsCandidatesSharingASize()
    {
        var candidates = new List<DuplicateCandidate>
        {
            MakeCandidate("a", sizeBytes: 100),
            MakeCandidate("b", sizeBytes: 100),
            MakeCandidate("c", sizeBytes: 200),
        };

        var groups = DuplicateScanner.GroupBySize(candidates);

        Assert.Equal(2, groups.Count);
        var sameSize = Assert.Single(groups, g => g.Count == 2);
        Assert.All(sameSize, c => Assert.Equal(100, c.SizeBytes));
        var uniqueSize = Assert.Single(groups, g => g.Count == 1);
        Assert.Equal(200, uniqueSize[0].SizeBytes);
    }

    [Fact]
    public void GroupBySizeWithAllDistinctSizesReturnsOnlySingletonGroups()
    {
        var candidates = new List<DuplicateCandidate>
        {
            MakeCandidate("a", sizeBytes: 100),
            MakeCandidate("b", sizeBytes: 200),
        };

        var groups = DuplicateScanner.GroupBySize(candidates);

        Assert.Equal(2, groups.Count);
        Assert.All(groups, g => Assert.Single(g));
    }

    private static DuplicateCandidate MakeCandidate(string path, long sizeBytes = 1) => new(
        Guid.NewGuid(), path, path, sizeBytes, 0, 0, 0, string.Empty);
}
