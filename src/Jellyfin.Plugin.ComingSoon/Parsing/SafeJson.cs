using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace Jellyfin.Plugin.ComingSoon.Parsing;

/// <summary>
/// Forgiving readers over <see cref="JsonElement"/>: a missing property or a value of the
/// wrong type yields null instead of throwing, so one odd field never breaks a poll.
/// </summary>
internal static class SafeJson
{
    public static JsonElement? Prop(this JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (element.TryGetProperty(name, out var value))
        {
            return value;
        }

        // Fall back to a case-insensitive match (e.g. "sizeLeft" vs "sizeleft").
        foreach (var p in element.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value;
            }
        }

        return null;
    }

    public static JsonElement? Obj(this JsonElement element, string name)
    {
        var v = element.Prop(name);
        return v?.ValueKind == JsonValueKind.Object ? v : null;
    }

    public static IEnumerable<JsonElement> Array(this JsonElement element, string name)
    {
        var v = element.Prop(name);
        if (v?.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var item in v.Value.EnumerateArray())
        {
            yield return item;
        }
    }

    public static string? Str(this JsonElement element, string name)
    {
        var v = element.Prop(name);
        return v?.ValueKind switch
        {
            JsonValueKind.String => v.Value.GetString(),
            JsonValueKind.Number => v.Value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    public static double? Num(this JsonElement element, string name)
    {
        var v = element.Prop(name);
        if (v is null)
        {
            return null;
        }

        if (v.Value.ValueKind == JsonValueKind.Number && v.Value.TryGetDouble(out var d))
        {
            return double.IsFinite(d) ? d : null;
        }

        if (v.Value.ValueKind == JsonValueKind.String
            && double.TryParse(v.Value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d))
        {
            return double.IsFinite(d) ? d : null;
        }

        return null;
    }

    public static int? Int(this JsonElement element, string name)
    {
        var d = element.Num(name);
        if (d is null || d < int.MinValue || d > int.MaxValue)
        {
            return null;
        }

        return (int)d.Value;
    }

    /// <summary>Returns a positive id, or null for missing/zero/negative values.</summary>
    public static int? Id(this JsonElement element, string name)
    {
        var i = element.Int(name);
        return i > 0 ? i : null;
    }

    public static bool? Bool(this JsonElement element, string name)
    {
        var v = element.Prop(name);
        return v?.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(v.Value.GetString(), out var b) => b,
            _ => null
        };
    }

    public static DateTimeOffset? Date(this JsonElement element, string name)
    {
        var s = element.Str(name);
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        return DateTimeOffset.TryParse(
            s,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var d)
            ? d
            : null;
    }

    /// <summary>Parses .NET TimeSpan strings as written by Radarr/Sonarr ("01:02:03", "1.02:03:04").</summary>
    public static TimeSpan? Duration(this JsonElement element, string name)
    {
        var s = element.Str(name);
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        return TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var t) && t >= TimeSpan.Zero ? t : null;
    }
}
