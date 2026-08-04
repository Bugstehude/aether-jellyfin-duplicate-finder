using Jellyfin.Plugin.DuplicateFinder.Application;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DuplicateFinder.Api;

/// <summary>
/// Admin-only API for the duplicate finder config page. Every endpoint requires the
/// Administrator role — this plugin can permanently delete media files, so nothing here is
/// reachable by a regular Jellyfin user.
/// </summary>
[ApiController]
[Authorize(Roles = AdministratorRole)]
[Route("DuplicateFinder/v1")]
public sealed class DuplicateFinderController(
    ILibraryManager libraryManager,
    DuplicateScanner scanner,
    DuplicateResultStore store,
    ILogger<DuplicateFinderController> logger) : ControllerBase
{
    private const string AdministratorRole = "Administrator";

    /// <summary>Starts a scan in the background if one isn't already running; returns immediately.</summary>
    [HttpPost("scan")]
    public ActionResult StartScan()
    {
        // Fire-and-forget by design: a full-library hash pass can take minutes, and the config
        // page polls /scan/status for progress rather than holding one long HTTP request open.
        _ = scanner.ScanAsync(CancellationToken.None);
        return Accepted();
    }

    /// <summary>Gets the current/last scan's progress.</summary>
    [HttpGet("scan/status")]
    public ActionResult GetScanStatus()
    {
        var status = scanner.Status;
        // Explicit lowercase-keyed anonymous object rather than serializing the ScanStatus
        // POCO directly: Jellyfin's server does NOT apply a camelCase naming policy, so a
        // plain `Ok(status)` would emit PascalCase keys ("Running", "LastFinishedAt", ...)
        // that silently don't match the config page's lowercase JS field reads — the exact
        // bug this fixes (status always read as undefined, page looked like nothing happened
        // even though the scan ran and completed correctly). Same convention the AETHER
        // Analysis plugin's activity endpoint already uses, for the same reason.
        return Ok(new
        {
            running = status.Running,
            checkedCount = status.Checked,
            total = status.Total,
            lastFinishedAt = status.LastFinishedAt,
            lastGroupCount = status.LastGroupCount,
            lastError = status.LastError
        });
    }

    /// <summary>Gets the duplicate groups found by the last completed scan.</summary>
    [HttpGet("groups")]
    public async Task<ActionResult> GetGroups(CancellationToken cancellationToken)
    {
        var groups = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        return Ok(groups.Select(ToJson));
    }

    private static object ToJson(DuplicateGroup group) => new
    {
        sha256 = group.Sha256,
        files = group.Files.Select(file => new
        {
            itemId = file.ItemId,
            itemName = file.ItemName,
            path = file.Path,
            sizeBytes = file.SizeBytes,
            width = file.Width,
            height = file.Height,
            durationSeconds = file.DurationSeconds,
            codec = file.Codec
        })
    };

    /// <summary>
    /// Permanently deletes the given items (identified by <see cref="DeleteRequest.ItemIds"/>)
    /// and removes them from the stored groups. Never called automatically — the config page
    /// only sends this after the admin has explicitly ticked items and confirmed.
    /// </summary>
    [HttpPost("delete")]
    public async Task<ActionResult> Delete([FromBody] DeleteRequest request, CancellationToken cancellationToken)
    {
        var results = new List<DeleteResult>();
        foreach (var idText in request.ItemIds)
        {
            if (!Guid.TryParse(idText, out var id))
            {
                results.Add(new DeleteResult(idText, false, "invalid-id"));
                continue;
            }

            var item = libraryManager.GetItemById(id);
            if (item is null)
            {
                results.Add(new DeleteResult(idText, false, "not-found"));
                continue;
            }

            try
            {
                libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, notifyParentItem: true);
                results.Add(new DeleteResult(idText, true, null));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogError(exception, "Failed to delete duplicate item {ItemId}", id);
                results.Add(new DeleteResult(idText, false, "delete-failed"));
            }
        }

        // Drop deleted ids from the stored groups so a page refresh doesn't show them again —
        // the deletion already happened, re-showing them would invite deleting a missing file twice.
        var deletedIds = new HashSet<string>(
            results.Where(r => r.Deleted).Select(r => r.ItemId),
            StringComparer.OrdinalIgnoreCase);
        var groups = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
        var updated = groups
            .Select(group => group with
            {
                Files = group.Files.Where(file => !deletedIds.Contains(file.ItemId.ToString())).ToList()
            })
            .Where(group => group.Files.Count > 1)
            .ToList();
        await store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);

        return Ok(new { results });
    }

    /// <summary>Request body for <see cref="Delete"/>.</summary>
    public sealed class DeleteRequest
    {
        /// <summary>Item ids (as strings) to permanently delete.</summary>
        public string[] ItemIds { get; set; } = [];
    }

    /// <summary>Outcome of one item's deletion attempt.</summary>
    public sealed record DeleteResult(string ItemId, bool Deleted, string? Reason);
}
