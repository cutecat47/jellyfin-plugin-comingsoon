using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ComingSoon.Tracking;

namespace Jellyfin.Plugin.ComingSoon.Library;

/// <summary>What was last written for a stub; stored next to it so restarts pick up where they left off.</summary>
public sealed record StubRecord
{
    /// <summary>Bump to make every existing stub re-render after an upgrade (2 = posters with progress overlay).</summary>
    public const int CurrentSchema = 2;

    public int SchemaVersion { get; init; } = CurrentSchema;

    public required TrackedEntry Entry { get; init; }

    public string? Overview { get; init; }

    /// <summary>Gets a value indicating whether the current <see cref="Entry"/> has been written to the Jellyfin item.</summary>
    public bool MetadataApplied { get; init; }

    public string? AppliedPosterUrl { get; init; }

    public string? AppliedBackdropUrl { get; init; }
}

/// <summary>
/// The stub folder on disk: one sub-folder per entry holding a tiny placeholder video and a hidden
/// sidecar (<c>.comingsoon.json</c>; Jellyfin ignores dot-files).
/// </summary>
public sealed partial class StubStore
{
    public const string SidecarName = ".comingsoon.json";
    public const string FolderPrefix = "cs-";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Func<byte[]> _video;

    public StubStore(string root, Func<byte[]> video)
    {
        Root = Path.GetFullPath(root);
        _video = video;
    }

    public string Root { get; }

    /// <summary>
    /// Folder name for a key. Must never contain anything Jellyfin (or Seerr, via Jellyfin) could read as a
    /// provider id - "tmdb", "tvdb", "imdb", "[...]", "{...}" or "tt1234567" - or Seerr would treat the stub
    /// as the real, available media.
    /// </summary>
    public static string FolderName(string key)
    {
        var name = key
            .Replace("movie-tmdb", "movie-", StringComparison.Ordinal)
            .Replace("tv-tvdb", "tv-", StringComparison.Ordinal)
            .Replace("tv-tmdb", "tvt-", StringComparison.Ordinal);
        name = Unsafe().Replace(name.ToLowerInvariant(), "-");
        return FolderPrefix + name.Trim('-');
    }

    public string FolderFor(string key) => Path.Combine(Root, FolderName(key));

    public string MediaPathFor(string key) => Path.Combine(FolderFor(key), FolderName(key) + ".mp4");

    /// <summary>Creates the folder and placeholder video if missing. Returns true when something was created.</summary>
    public bool EnsureStub(string key)
    {
        var media = MediaPathFor(key);
        if (File.Exists(media))
        {
            return false;
        }

        Directory.CreateDirectory(FolderFor(key));
        File.WriteAllBytes(media, _video());
        return true;
    }

    public void Save(StubRecord record)
    {
        var folder = FolderFor(record.Entry.Key);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, SidecarName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Reads every stub's sidecar. Unreadable ones are reported, not thrown.</summary>
    public IReadOnlyList<StubRecord> LoadAll(ICollection<string>? problems = null)
    {
        var records = new List<StubRecord>();
        if (!Directory.Exists(Root))
        {
            return records;
        }

        foreach (var dir in Directory.EnumerateDirectories(Root, FolderPrefix + "*"))
        {
            var sidecar = Path.Combine(dir, SidecarName);
            try
            {
                var record = JsonSerializer.Deserialize<StubRecord>(File.ReadAllText(sidecar), JsonOptions);
                if (record?.Entry?.Key is { Length: > 0 } key && string.Equals(FolderFor(key), dir, StringComparison.Ordinal))
                {
                    records.Add(record);
                }
                else
                {
                    problems?.Add($"{dir}: sidecar doesn't match its folder");
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
            {
                problems?.Add($"{dir}: {ex.Message}");
            }
        }

        return records;
    }

    public void Delete(string key)
    {
        var folder = FolderFor(key);
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex Unsafe();
}
