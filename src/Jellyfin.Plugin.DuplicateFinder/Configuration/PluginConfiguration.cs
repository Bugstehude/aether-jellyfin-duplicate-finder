using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.DuplicateFinder.Configuration;

/// <summary>Persisted plugin settings.</summary>
public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Library or folder item ids to scan for duplicates. Empty = every library. An entry
    /// may be a top-level library id OR a subfolder item id — Jellyfin's <c>AncestorIds</c>
    /// query filter resolves both identically against the full item tree, so no distinction
    /// is needed here (same approach as AETHER Analysis's <c>AnalysisLibraryIds</c>).
    /// </summary>
    public string[] ScanScopeIds { get; set; } = [];
}
