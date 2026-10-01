using System;
using System.Globalization;

namespace Jellyfin.Plugin.ComingSoon.Tracking;

public enum EtaBucket
{
    Unknown,
    UnderAnHour,
    FewHours,
    Today,
    Tomorrow,
    FewDays,
    OverAWeek
}

/// <summary>
/// What a user actually sees for an entry. Two equal states render identically, so metadata
/// is only rewritten when this changes.
/// </summary>
public sealed record DisplayState(TrackedStatus Status, int? Percent, EtaBucket Eta, string? Detail, DateOnly? ReleaseDate);

/// <summary>Friendly ETA buckets and the status text written into item overviews.</summary>
public static class StatusText
{
    private const string Dash = " — ";

    public static EtaBucket BucketEta(TimeSpan? left, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (left is not { } t || t < TimeSpan.Zero)
        {
            return EtaBucket.Unknown;
        }

        if (t < TimeSpan.FromHours(1))
        {
            return EtaBucket.UnderAnHour;
        }

        if (t < TimeSpan.FromHours(6))
        {
            return EtaBucket.FewHours;
        }

        var today = TimeZoneInfo.ConvertTime(now, zone).Date;
        var done = TimeZoneInfo.ConvertTime(now + t, zone).Date;
        if (done == today)
        {
            return EtaBucket.Today;
        }

        if (done == today.AddDays(1))
        {
            return EtaBucket.Tomorrow;
        }

        return t < TimeSpan.FromDays(7) ? EtaBucket.FewDays : EtaBucket.OverAWeek;
    }

    public static string EtaPhrase(EtaBucket bucket) => bucket switch
    {
        EtaBucket.UnderAnHour => "under an hour left",
        EtaBucket.FewHours => "a few hours left",
        EtaBucket.Today => "ready later today",
        EtaBucket.Tomorrow => "ready tomorrow",
        EtaBucket.FewDays => "a few days left",
        EtaBucket.OverAWeek => "over a week left",
        _ => "time left unknown",
    };

    /// <summary>Floors progress to the configured step so it never overstates (62% at step 5 shows 60%).</summary>
    public static int? StepPercent(double? percent, int step)
    {
        if (percent is not { } p || double.IsNaN(p))
        {
            return null;
        }

        step = Math.Clamp(step, 1, 100);
        var clamped = Math.Clamp(p, 0, 100);
        return (int)(Math.Floor(clamped / step) * step);
    }

    public static DisplayState ToDisplayState(TrackedEntry entry, int percentStep, DateTimeOffset now, TimeZoneInfo zone)
    {
        var showsProgress = entry.Status is TrackedStatus.Downloading or TrackedStatus.Stalled;
        return new DisplayState(
            entry.Status,
            showsProgress ? StepPercent(entry.Percent, percentStep) : null,
            entry.Status == TrackedStatus.Downloading ? BucketEta(entry.TimeLeft, now, zone) : EtaBucket.Unknown,
            entry.Detail,
            entry.Status == TrackedStatus.WaitingForRelease && entry.ReleaseDate is { } r ? LocalDate(r, zone) : null);
    }

    /// <summary>
    /// Calendar date of a release. Midnight-UTC values are date-only data (TMDB/Radarr release dates)
    /// and keep their date; real timestamps (Sonarr air times) are shown in the server's time zone.
    /// </summary>
    public static DateOnly LocalDate(DateTimeOffset value, TimeZoneInfo zone)
    {
        var utc = value.ToUniversalTime();
        return utc.TimeOfDay == TimeSpan.Zero
            ? DateOnly.FromDateTime(utc.DateTime)
            : DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone).DateTime);
    }

    public static string StatusLabel(TrackedStatus status) => status switch
    {
        TrackedStatus.Searching => "Searching",
        TrackedStatus.WaitingForRelease => "Waiting for release",
        TrackedStatus.Queued => "Queued",
        TrackedStatus.Downloading => "Downloading",
        TrackedStatus.Stalled => "Stalled",
        TrackedStatus.Importing => "Importing",
        _ => status.ToString(),
    };

    /// <summary>The item overview, e.g. "Downloading — 60% — a few hours left".</summary>
    public static string Overview(DisplayState s)
    {
        var text = StatusLabel(s.Status);
        switch (s.Status)
        {
            case TrackedStatus.Downloading:
                text += Pct(s.Percent) + Dash + EtaPhrase(s.Eta);
                break;
            case TrackedStatus.Stalled:
                text += Pct(s.Percent) + Dash + (s.Detail ?? "needs attention in Sonarr/Radarr");
                return text;
            case TrackedStatus.WaitingForRelease when s.ReleaseDate is { } date:
                text += Dash + "expected " + date.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
                break;
            case TrackedStatus.Queued:
                text += Dash + (s.Detail is null ? "waiting to start" : s.Detail.ToLowerInvariant());
                return text;
            case TrackedStatus.Importing:
                text += Dash + "almost ready";
                break;
            case TrackedStatus.Searching:
                text += Dash + "looking for a release";
                break;
        }

        if (s.Detail is not null && s.Status is not TrackedStatus.Stalled and not TrackedStatus.Queued
            && !(s.Status == TrackedStatus.Importing && s.Detail.StartsWith("Waiting for library", StringComparison.Ordinal)))
        {
            text += " (" + s.Detail + ")";
        }

        return text;
    }

    /// <summary>Text and bar fill for the poster overlay.</summary>
    public static (string Headline, string Subline, double? Progress) PosterLines(DisplayState s)
    {
        static string Cap(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
        double? fill = s.Percent is int p ? p / 100.0 : null;
        return s.Status switch
        {
            TrackedStatus.Downloading => ("Downloading", Cap(EtaPhrase(s.Eta)), fill ?? 0),
            TrackedStatus.Stalled => ("Stalled", s.Detail ?? "Needs attention", fill),
            TrackedStatus.Importing => ("Importing", "Almost ready", 1.0),
            TrackedStatus.Queued => ("Queued", s.Detail is null ? "Waiting to start" : Cap(s.Detail.ToLowerInvariant()), null),
            TrackedStatus.WaitingForRelease when s.ReleaseDate is { } d => ("Not released yet", "Expected " + d.ToString("d MMM yyyy", CultureInfo.InvariantCulture), null),
            TrackedStatus.WaitingForRelease => ("Not released yet", "Waiting for a release date", null),
            _ => ("Searching", s.Detail is null ? "Looking for a release" : Cap(s.Detail), null),
        };
    }

    /// <summary>A one-line status for the tagline, e.g. "Downloading 60%" or "Waiting for release".</summary>
    public static string Tagline(DisplayState s) => s.Percent is int p && s.Status is TrackedStatus.Downloading or TrackedStatus.Stalled
        ? StatusLabel(s.Status) + " " + p.ToString(CultureInfo.InvariantCulture) + "%"
        : StatusLabel(s.Status);

    /// <summary>The short message shown when someone presses play, e.g. "Still downloading — a few hours left".</summary>
    public static string PlaybackMessage(DisplayState s) => s.Status switch
    {
        TrackedStatus.Downloading => "Still downloading" + Dash + EtaPhrase(s.Eta),
        TrackedStatus.Importing => "Almost ready" + Dash + "importing now",
        TrackedStatus.WaitingForRelease when s.ReleaseDate is { } d => "Not released yet" + Dash + "expected " + d.ToString("d MMM yyyy", CultureInfo.InvariantCulture),
        TrackedStatus.WaitingForRelease => "Not released yet",
        TrackedStatus.Stalled => "Download stalled" + Dash + "check Sonarr/Radarr",
        TrackedStatus.Queued => "Queued" + Dash + "download hasn't started yet",
        _ => "Not available yet" + Dash + "still searching for a release",
    };

    private static string Pct(int? p) => p is null ? string.Empty : Dash + p.Value.ToString(CultureInfo.InvariantCulture) + "%";
}
