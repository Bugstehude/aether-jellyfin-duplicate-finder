using System.Runtime.CompilerServices;

// Lets the test project exercise the scanner's internal caching helpers directly with real
// temp files, rather than only through the fully public pure-logic pieces (GroupBySize,
// GroupByHash) — the caching behavior is new, non-trivial, and worth a real test.
[assembly: InternalsVisibleTo("Jellyfin.Plugin.DuplicateFinder.Tests")]
