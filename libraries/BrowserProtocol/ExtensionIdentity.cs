using System.Security.Cryptography;
using System.Text.Json;

namespace MeldResearch.BrowserProtocol;

public static class ExtensionIdentity
{
    public static string Id { get; }
    public static string Version { get; }

    static ExtensionIdentity()
    {
        using var stream = typeof(ExtensionIdentity).Assembly.GetManifestResourceStream("MeldResearch.OverleafManifest.json")
            ?? throw new InvalidOperationException("The bundled extension manifest is missing.");
        using var manifest = JsonDocument.Parse(stream);
        string key = manifest.RootElement.GetProperty("key").GetString()
            ?? throw new InvalidDataException("The extension identity key is missing.");
        Version = manifest.RootElement.GetProperty("version").GetString()
            ?? throw new InvalidDataException("The extension version is missing.");
        byte[] hash = SHA256.HashData(Convert.FromBase64String(key));
        Span<char> encoded = stackalloc char[32];
        for (int index = 0; index < 16; index++)
        {
            encoded[index * 2] = (char)('a' + (hash[index] >> 4));
            encoded[index * 2 + 1] = (char)('a' + (hash[index] & 15));
        }
        Id = new string(encoded);
    }
}
