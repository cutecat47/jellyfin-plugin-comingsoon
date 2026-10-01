using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.ComingSoon.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.ComingSoon;

/// <summary>
/// The "Coming Soon" plugin entry point.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Prefix on every log line so excerpts are easy to grep.</summary>
    public const string LogPrefix = "[ComingSoon]";

    public static readonly Guid PluginId = new("41434daf-8948-4e30-b7b9-cbeb0ff0ea32");

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets the current plugin instance (set once Jellyfin constructs the plugin).</summary>
    public static Plugin? Instance { get; private set; }

    public override string Name => "Coming Soon";

    public override Guid Id => PluginId;

    public override string Description => "Download progress library for Seerr, Sonarr and Radarr.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        ];
    }
}
