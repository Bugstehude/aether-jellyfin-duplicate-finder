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
        // Task.Run, not a bare fire-and-forget call: ScanAsync's semaphore acquisition and its
        // initial library enumeration run SYNCHRONOUSLY on an uncontended gate (a completed
        // Task doesn't yield back to the caller until a genuinely asynchronous operation is
        // hit) — a bare `_ = scanner.ScanAsync(...)` therefore blocked THIS request thread
        // through the whole item enumeration on a large library, despite the intent below.
        // Task.Run moves that work onto a thread-pool thread so the response really is
        // immediate; the config page then polls /scan/status for progress rather than holding
        // one long HTTP request open.
        _ = Task.Run(() => scanner.ScanAsync(CancellationToken.None));
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
    ///
    /// Two safety checks that do NOT trust the client: (1) an id whose deletion would leave a
    /// stored group with zero remaining files is refused server-side — the client also guards
    /// against this before showing the confirmation dialog, but that guard is only a UX nicety,
    /// this is the real one; (2) a single item's failure never aborts the batch or skips
    /// pruning for items that DID succeed (a narrow exception filter here previously let an
    /// unexpected exception type escape and abandon bookkeeping for already-deleted items).
    /// </summary>
    [HttpPost("delete")]
    public async Task<ActionResult> Delete([FromBody] DeleteRequest request, CancellationToken cancellationToken)
    {
        var groups = await store.LoadAsync(cancellationToken).ConfigureAwait(false);

        var requested = request.ItemIds
            .Select(text => (Text: text, Id: Guid.TryParse(text, out var parsed) ? parsed : (Guid?)null))
            .ToList();
        var requestedIds = new HashSet<Guid>(requested.Where(r => r.Id.HasValue).Select(r => r.Id!.Value));

        // A group whose EVERY distinct item was requested would end up with nothing left —
        // block those ids rather than guess which one the admin meant to keep. Distinct by
        // item, not by file row: one item can legitimately have more than one media source, so
        // "every file in the group" and "every item in the group" are not the same count.
        var blockedIds = new HashSet<Guid>();
        foreach (var group in groups)
        {
            var groupItemIds = group.Files.Select(file => file.ItemId).Distinct().ToList();
            if (groupItemIds.Count > 0 && groupItemIds.All(requestedIds.Contains))
            {
                foreach (var id in groupItemIds)
                {
                    blockedIds.Add(id);
                }
            }
        }

        var results = new List<DeleteResult>();
        var deletedIds = new HashSet<Guid>();
        foreach (var (text, id) in requested)
        {
            if (id is null)
            {
                results.Add(new DeleteResult(text, false, "invalid-id"));
                continue;
            }

            if (blockedIds.Contains(id.Value))
            {
                results.Add(new DeleteResult(text, false, "would-empty-group"));
                continue;
            }

            var item = libraryManager.GetItemById(id.Value);
            if (item is null)
            {
                results.Add(new DeleteResult(text, false, "not-found"));
                continue;
            }

            try
            {
                libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true }, notifyParentItem: true);
                results.Add(new DeleteResult(text, true, null));
                deletedIds.Add(id.Value);
            }
            catch (Exception exception)
            {
                // Deliberately broad, not just IOException/UnauthorizedAccessException:
                // DeleteItem can throw other types depending on the underlying provider, and
                // one unreadable/locked file must never abort the whole batch or skip pruning
                // for items that already succeeded (see loop bookkeeping below).
                logger.LogError(exception, "Failed to delete duplicate item {ItemId}", id);
                results.Add(new DeleteResult(text, false, "delete-failed"));
            }
        }

        // Drop successfully-deleted ids from the stored groups so a page refresh doesn't show
        // them again. Compares the PARSED Guid, not the client's raw request string — Jellyfin
        // and DuplicateCandidate both serialize a Guid in "D" format by default so this rarely
        // mattered in practice via this plugin's own page, but a request carrying any other
        // valid Guid.TryParse-accepted format (e.g. "N", no dashes) previously never pruned.
        if (deletedIds.Count > 0)
        {
            var updated = groups
                .Select(group => group with
                {
                    Files = group.Files.Where(file => !deletedIds.Contains(file.ItemId)).ToList()
                })
                .Where(group => group.Files.Count > 1)
                .ToList();
            await store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
        }

        // Same lowercase-JSON reasoning as GetScanStatus/ToJson above: DeleteResult's own
        // PascalCase properties would otherwise reach the config page as "ItemId"/"Deleted"/
        // "Reason", which its lowercase JS reads (r.itemId/r.deleted/r.reason) would silently
        // never match — the per-file failure list would look empty even on a real failure.
        return Ok(new
        {
            results = results.Select(r => new { itemId = r.ItemId, deleted = r.Deleted, reason = r.Reason })
        });
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
