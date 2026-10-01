using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
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
    /// The same metadata as an NFO file next to the stub. Jellyfin's library scans refresh their own copies
    /// of items and re-read local metadata, so the NFO is what keeps the title and status from reverting to
    /// the folder name. Deliberately has no &lt;uniqueid&gt;/&lt;tmdbid&gt; etc. (see <see cref="Apply"/>).
    /// </summary>
    public static string ToNfo(TrackedEntry entry, DisplayState state, string overview)
    {
        var settings = new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false };
        using var text = new Utf8StringWriter();
        using (var xml = XmlWriter.Create(text, settings))
        {
            xml.WriteStartDocument(standalone: true);
            xml.WriteStartElement("movie");
            xml.WriteElementString("title", entry.DisplayName);
            xml.WriteElementString("sorttitle", SortName(entry));
            xml.WriteElementString("plot", overview);
            xml.WriteElementString("tagline", StatusText.Tagline(state));
            if (entry.Year is int year)
            {
                xml.WriteElementString("year", year.ToString(CultureInfo.InvariantCulture));
            }

            xml.WriteElementString("dateadded", (entry.RequestedAt ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            xml.WriteElementString("lockdata", "true");
            xml.WriteElementString("lockedfields", string.Join('|', Enum.GetNames<MetadataField>()));
            xml.WriteElementString("tag", Tag);
            xml.WriteEndElement();
            xml.WriteEndDocument();
        }

        return text.ToString();
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

    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter()
            : base(CultureInfo.InvariantCulture)
        {
        }

        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
