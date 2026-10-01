using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.ComingSoon.Tracking;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ComingSoon.Library;

/// <summary>Maps an entry onto a Jellyfin item's metadata fields.</summary>
public static class StubMetadata
{
    public const string Tag = "Coming Soon";

    private static readonly long MaxUnixSeconds = DateTimeOffset.MaxValue.ToUnixTimeSeconds();

    /// <summary>
    /// Sort name that puts the newest request first under the default "Name" sort, while the visible
    /// Name stays the plain title: an inverted request timestamp, then the title.
    /// </summary>
    public static string SortName(TrackedEntry entry)
    {
        var requested = (entry.RequestedAt ?? DateTimeOffset.UnixEpoch).ToUnixTimeSeconds();
        var inverted = MaxUnixSeconds - Math.Max(0, requested);
        return inverted.ToString("D12", CultureInfo.InvariantCulture) + " " + entry.DisplayName;
    }

    /// <summary>
    /// Writes title, status text, dates and sort order, strips provider ids (so Seerr never mistakes a stub
    /// for the real media) and locks every field so scans and metadata providers leave it alone.
    /// </summary>
    public static void Apply(BaseItem item, TrackedEntry entry, DisplayState state, string overview)
    {
        item.Name = entry.DisplayName;
        item.ForcedSortName = SortName(entry);
        item.Overview = overview;
        item.Tagline = StatusText.Tagline(state);
        item.DateCreated = (entry.RequestedAt ?? DateTimeOffset.UtcNow).UtcDateTime;
        item.ProductionYear = entry.Year;
        item.PremiereDate = null;
        item.OriginalTitle = null;
        item.ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        item.Genres = [];
        item.Studios = [];
        item.Tags = [Tag];
        item.IsLocked = true;
        item.LockedFields = Enum.GetValues<MetadataField>();
    }
}
