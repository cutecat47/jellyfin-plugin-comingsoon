using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ComingSoon.Tracking;

/// <summary>
/// Decides when an item's metadata should be rewritten: only when what the user sees changes,
/// and at most once per <see cref="MinInterval"/> per item. A change held back by the interval
/// is pushed on a later poll (the comparison is always against what was last pushed).
/// </summary>
public sealed class UpdateThrottle
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);

    private readonly Dictionary<string, (DisplayState State, DateTimeOffset At)> _pushed = new(StringComparer.Ordinal);

    public bool ShouldPush(string key, DisplayState state, DateTimeOffset now)
    {
        if (!_pushed.TryGetValue(key, out var last))
        {
            return true;
        }

        return last.State != state && now - last.At >= MinInterval;
    }

    public void MarkPushed(string key, DisplayState state, DateTimeOffset now) => _pushed[key] = (state, now);

    public void Forget(string key) => _pushed.Remove(key);

    public bool TryGetLast(string key, out DisplayState? state)
    {
        var found = _pushed.TryGetValue(key, out var last);
        state = found ? last.State : null;
        return found;
    }
}
