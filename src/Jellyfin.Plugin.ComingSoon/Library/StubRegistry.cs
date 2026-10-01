using System;
using System.Collections.Concurrent;
using System.IO;
using Jellyfin.Plugin.ComingSoon.Tracking;

namespace Jellyfin.Plugin.ComingSoon.Library;

/// <summary>What the playback guard needs to know about a stub.</summary>
public sealed record StubInfo(string Key, string DisplayName, DisplayState State);

/// <summary>
/// Shared, thread-safe view of the current stubs: written by the poller, read by the playback guard and
/// the PlaybackInfo filter (which run on request threads).
/// </summary>
public sealed class StubRegistry
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly ConcurrentDictionary<string, StubInfo> _byKey = new(StringComparer.Ordinal);
    private volatile string? _root;

    public string? Root => _root;

    public void SetRoot(string root) => _root = Normalize(root);

    public void Upsert(StubInfo info) => _byKey[info.Key] = info;

    public void Remove(string key) => _byKey.TryRemove(key, out _);

    /// <summary>True for any path inside the stub folder - the folder itself is the source of truth.</summary>
    public bool IsStubPath(string? path)
    {
        var root = _root;
        if (string.IsNullOrEmpty(path) || root is null)
        {
            return false;
        }

        var full = Normalize(path);
        return full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    /// <summary>Finds the stub for a media path (folder name is derived from the key).</summary>
    public StubInfo? Find(string? path)
    {
        if (!IsStubPath(path))
        {
            return null;
        }

        var folder = Path.GetFileName(Path.GetDirectoryName(Normalize(path!)));
        foreach (var info in _byKey.Values)
        {
            if (string.Equals(StubStore.FolderName(info.Key), folder, PathComparison))
            {
                return info;
            }
        }

        return null;
    }

    private static string Normalize(string path)
        => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
