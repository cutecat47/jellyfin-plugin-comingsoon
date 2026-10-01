using System;
using System.IO;

namespace Jellyfin.Plugin.ComingSoon.Library;

/// <summary>
/// The bundled placeholder: 4 s, 640x360 H.264 baseline + silent AAC, black with "Still downloading".
/// If playback blocking ever fails, this is all that plays. Rendered by .github/workflows/stub-video.yml.
/// </summary>
public static class StubVideo
{
    public const string ResourceName = "Jellyfin.Plugin.ComingSoon.Resources.stub.mp4";

    private static readonly Lazy<byte[]> Data = new(Load);

    public static byte[] Bytes => Data.Value;

    private static byte[] Load()
    {
        using var stream = typeof(StubVideo).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException("Embedded stub video missing: " + ResourceName);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
