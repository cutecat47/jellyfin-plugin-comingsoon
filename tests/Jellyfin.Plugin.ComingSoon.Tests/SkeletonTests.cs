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
    public void ConfigPage_IsEmbedded()
    {
        var asm = typeof(Plugin).Assembly;
        Assert.Contains("Jellyfin.Plugin.ComingSoon.Configuration.configPage.html", asm.GetManifestResourceNames());
    }
}
