using Jellyfin.Plugin.ComingSoon.Configuration;
using Xunit;

namespace Jellyfin.Plugin.ComingSoon.Tests;

public class SkeletonTests
{
    [Fact]
    public void ConfigurationDefaults_MatchSpec()
    {
        var config = new PluginConfiguration();

        Assert.Equal(5, config.PollIntervalSeconds);
        Assert.Equal("/config/coming-soon", config.StubFolderPath);
        Assert.Equal(5, config.PercentStep);
        Assert.False(config.VerboseLogging);
        Assert.Equal("Coming Soon", config.LibraryName);
    }

    [Fact]
    public void Manifest_AllowsAutomaticUpdates()
    {
        // Jellyfin copies autoUpdate from the packaged meta.json and hides every newer version when it's false.
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "build.yaml")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        using var meta = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(dir!.FullName, "meta.json")));
        Assert.True(meta.RootElement.GetProperty("autoUpdate").GetBoolean());
        Assert.Equal(Plugin.PluginId.ToString(), meta.RootElement.GetProperty("guid").GetString());
        Assert.Equal("12.1.0.0", meta.RootElement.GetProperty("targetAbi").GetString());
    }

    [Fact]
    public void ConfigPage_IsEmbedded()
    {
        var asm = typeof(Plugin).Assembly;
        Assert.Contains("Jellyfin.Plugin.ComingSoon.Configuration.configPage.html", asm.GetManifestResourceNames());
    }
}
