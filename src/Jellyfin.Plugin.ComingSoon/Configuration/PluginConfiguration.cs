using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.ComingSoon.Configuration;

/// <summary>
/// Plugin settings, persisted by Jellyfin as XML under /config/plugins/configurations.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    public string SeerrUrl { get; set; } = string.Empty;

    public string SeerrApiKey { get; set; } = string.Empty;

    public string SonarrUrl { get; set; } = string.Empty;

    public string SonarrApiKey { get; set; } = string.Empty;

    public string RadarrUrl { get; set; } = string.Empty;

    public string RadarrApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets how often the queues are polled, in seconds.</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>Gets or sets the folder the "Coming Soon" library points at.</summary>
    public string StubFolderPath { get; set; } = "/config/coming-soon";

    /// <summary>Gets or sets the progress step (in percent) that triggers a metadata update.</summary>
    public int PercentStep { get; set; } = 5;

    public bool VerboseLogging { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether stubs get a playback position so clients draw a progress bar.
    /// Off by default; see README for the Continue Watching caveats.
    /// </summary>
    public bool ShowNativeProgressBar { get; set; }
}
