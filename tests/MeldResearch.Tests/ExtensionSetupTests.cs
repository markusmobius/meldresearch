using MeldResearch.LocalBackend.Middleware;
using MeldResearch.LocalBackend.Services;
using Microsoft.Extensions.Configuration;

namespace MeldResearch.Tests;

public sealed class ExtensionSetupTests
{
    [Fact]
    public void OpensOnlyChromeOrEdgeExtensionsPages()
    {
        var service = new ExtensionSetupService(null!, new ConfigurationBuilder().Build());
        foreach (string browser in new[] { "firefox", "chrome://settings", "", "CHROME" })
            Assert.Equal("invalid_browser", Assert.Throws<ApiFailure>(() => service.OpenExtensionsPage(browser)).Code);
    }

    [Theory]
    [InlineData("chrome", "chrome")]
    [InlineData("edge", "msedge")]
    public void LooksForTheBrowserInItsStandardInstallLocations(string browser, string executable)
    {
        var candidates = ExtensionSetupService.BrowserExecutables(browser).ToArray();
        if (OperatingSystem.IsWindows())
        {
            Assert.NotEmpty(candidates);
            Assert.All(candidates, path => Assert.Equal(executable + ".exe", Path.GetFileName(path)));
        }
        else if (OperatingSystem.IsMacOS())
            Assert.Single(candidates);
    }
}
