using Jellyfin.Plugin.DuplicateFinder.Application;

namespace Jellyfin.Plugin.DuplicateFinder.Tests;

/// <summary>
/// Exercises the cache-aware quick/full hash helpers against real temp files — the caching
/// behavior (reuse on an unmodified file, invalidate on a modified one) is new, non-trivial,
/// and not covered by the pure-logic tests in <see cref="DuplicateScannerTests"/>.
/// </summary>
public sealed class DuplicateScannerHashingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aether-dupfinder-hashing-tests-" + Guid.NewGuid());

    public DuplicateScannerHashingTests()
    {
        Directory.CreateDirectory(_dir);
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

    private static DuplicateCandidate CandidateFor(string path) => new(
        Guid.NewGuid(), Path.GetFileName(path), path, new FileInfo(path).Length, 0, 0, 0, string.Empty);

    [Fact]
    public async Task IdenticalFilesProduceTheSameFullHash()
    {
        var content = "same content"u8.ToArray();
        var a = CandidateFor(WriteFile("a.mkv", content));
        var b = CandidateFor(WriteFile("b.mkv", content));
        var cache = new Dictionary<string, CachedFileHash>();
        var changed = false;

        var hashA = await DuplicateScanner.GetOrComputeFullHashAsync(a, cache, c => changed |= c, CancellationToken.None);
        var hashB = await DuplicateScanner.GetOrComputeFullHashAsync(b, cache, c => changed |= c, CancellationToken.None);

        Assert.NotNull(hashA);
        Assert.Equal(hashA, hashB);
        Assert.True(changed);
    }

    [Fact]
    public async Task DifferentFilesOfTheSameSizeProduceDifferentQuickHashes()
    {
        var a = CandidateFor(WriteFile("a.mkv", "aaaaaaaaaa"u8.ToArray()));
        var b = CandidateFor(WriteFile("b.mkv", "bbbbbbbbbb"u8.ToArray()));
        var cache = new Dictionary<string, CachedFileHash>();

        var hashA = await DuplicateScanner.GetOrComputeQuickHashAsync(a, cache, _ => { }, CancellationToken.None);
        var hashB = await DuplicateScanner.GetOrComputeQuickHashAsync(b, cache, _ => { }, CancellationToken.None);

        Assert.NotNull(hashA);
        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public async Task SecondCallForAnUnmodifiedFileReusesTheCacheWithoutReReading()
    {
        var path = WriteFile("a.mkv", "content"u8.ToArray());
        var candidate = CandidateFor(path);
        var cache = new Dictionary<string, CachedFileHash>();

        var firstChanged = false;
        var firstHash = await DuplicateScanner.GetOrComputeFullHashAsync(candidate, cache, c => firstChanged |= c, CancellationToken.None);
        Assert.True(firstChanged);

        // Overwrite with DIFFERENT content of the same length, then restore the exact original
        // last-write time — from the cache's point of view this file is "unchanged" (same size,
        // same mtime), even though its actual bytes now differ. If the second call took the
        // cache path, it returns the ORIGINAL hash despite that; if it actually re-read the
        // file, it would return a hash matching the new content instead — that's the proof.
        var originalWriteTimeUtc = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, "CHANGED"u8.ToArray());
        File.SetLastWriteTimeUtc(path, originalWriteTimeUtc);

        var secondChanged = false;
        var secondHash = await DuplicateScanner.GetOrComputeFullHashAsync(candidate, cache, c => secondChanged |= c, CancellationToken.None);

        Assert.Equal(firstHash, secondHash);
        Assert.False(secondChanged);
    }

    [Fact]
    public async Task ModifyingAFileAfterCachingInvalidatesTheOldHash()
    {
        var path = WriteFile("a.mkv", "original"u8.ToArray());
        var cache = new Dictionary<string, CachedFileHash>();
        var original = CandidateFor(path);
        var originalHash = await DuplicateScanner.GetOrComputeFullHashAsync(original, cache, _ => { }, CancellationToken.None);

        // A real filesystem write can land on the same last-write-time tick as the original on
        // fast systems/coarse clock resolution — force it forward so the change is guaranteed
        // to be observed, exactly like a genuinely later edit would be.
        File.WriteAllBytes(path, "changed content, different length"u8.ToArray());
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        var modified = CandidateFor(path);

        var changed = false;
        var newHash = await DuplicateScanner.GetOrComputeFullHashAsync(modified, cache, c => changed |= c, CancellationToken.None);

        Assert.NotEqual(originalHash, newHash);
        Assert.True(changed);
    }

    [Fact]
    public void GetFreshOrNewEntryReturnsExistingEntryWhenSizeAndTimeMatch()
    {
        var cache = new Dictionary<string, CachedFileHash>
        {
            ["/a"] = new CachedFileHash("/a", 100, 500, "quick", "full"),
        };

        var entry = DuplicateScanner.GetFreshOrNewEntry("/a", 100, 500, cache);

        Assert.Equal("quick", entry.QuickHash);
        Assert.Equal("full", entry.FullHash);
    }

    [Fact]
    public void GetFreshOrNewEntryDiscardsStaleHashesWhenSizeChanged()
    {
        var cache = new Dictionary<string, CachedFileHash>
        {
            ["/a"] = new CachedFileHash("/a", 100, 500, "quick", "full"),
        };

        var entry = DuplicateScanner.GetFreshOrNewEntry("/a", 999, 500, cache);

        Assert.Null(entry.QuickHash);
        Assert.Null(entry.FullHash);
    }

    [Fact]
    public void GetFreshOrNewEntryDiscardsStaleHashesWhenLastWriteTimeChanged()
    {
        var cache = new Dictionary<string, CachedFileHash>
        {
            ["/a"] = new CachedFileHash("/a", 100, 500, "quick", "full"),
        };

        var entry = DuplicateScanner.GetFreshOrNewEntry("/a", 100, 999, cache);

        Assert.Null(entry.QuickHash);
        Assert.Null(entry.FullHash);
    }
}
