using MeldResearch.BrowserProtocol;
using MeldResearch.LocalBackend.Contracts;

namespace MeldResearch.LocalBackend.Services;

internal interface IExtensionSetupService
{
    ExtensionSetupResponse GetSetup();
}

internal sealed class ExtensionSetupService(IWebHostEnvironment environment, IConfiguration configuration) : IExtensionSetupService
{
    public ExtensionSetupResponse GetSetup()
    {
        string directory = Path.GetFullPath(configuration["ConnectorDirectory"] ??
            Path.Combine(environment.ContentRootPath, "browser", "overleaf"));
        return new(ExtensionIdentity.Id, ExtensionIdentity.Version, WireProtocol.Version,
            File.Exists(Path.Combine(directory, "manifest.json")) ? directory : null,
            "https://chromewebstore.google.com/detail/" + ExtensionIdentity.Id,
            configuration.GetValue<bool>("ExtensionStorePublished"));
    }
}
