using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeldResearch.BrowserProtocol;

/// <summary>Release metadata for one desktop runtime package. Signed as exact UTF-8 JSON bytes.</summary>
public sealed record DesktopUpdateManifest(string Product, string Runtime, string Version, DateTimeOffset VersionDate,
    long Length, string Sha256);

/// <summary>Base64 manifest bytes and their base64 ECDSA P-256/SHA-256 (IEEE P1363) signature.</summary>
public sealed record SignedDesktopUpdate(string Manifest, string Signature);

public static class DesktopUpdates
{
    public const string Product = "MeldResearch.Desktop";
    /// <summary>The local backend exits with this code once a verified update is staged; the launcher then restarts it.</summary>
    public const int UpdateExitCode = 75;
    public const long MaxPackageBytes = 512L * 1024 * 1024;
    public const int MaxEnvelopeBytes = 16 * 1024;
    public static readonly IReadOnlyList<string> Runtimes = ["win-x64", "osx-arm64", "osx-x64"];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4
    };

    public static string ManifestFileName(string runtime) => $"meldresearch-{runtime}.update.json";
    public static string PackageFileName(string runtime) => $"meldresearch-{runtime}.zip";
    public static bool IsRuntime(string? runtime) => runtime is not null && Runtimes.Contains(runtime);
    public static bool IsVersion(string? version) => version is { Length: 64 } && version.All(IsLowerHex);
    /// <summary>The per-version installation folder below the application root.</summary>
    public static string FolderName(string version) => IsVersion(version) ? version[..12]
        : throw new ArgumentException("A 64-character lowercase hexadecimal release version is required.", nameof(version));

    public static SignedDesktopUpdate ReadEnvelope(ReadOnlySpan<byte> json)
    {
        if (json.Length is 0 || json.Length > MaxEnvelopeBytes) throw Invalid("The update manifest has an invalid size.");
        try
        {
            return JsonSerializer.Deserialize<SignedDesktopUpdate>(json, Json) is { Manifest.Length: > 0, Signature.Length: > 0 } envelope
                ? envelope : throw Invalid("The update manifest is incomplete.");
        }
        catch (JsonException) { throw Invalid("The update manifest is not valid JSON."); }
    }

    /// <summary>Verifies the signature with a SubjectPublicKeyInfo key, then validates the manifest it covers.</summary>
    public static DesktopUpdateManifest Verify(SignedDesktopUpdate envelope, string publicKey)
    {
        byte[] manifest, signature, key;
        try
        {
            manifest = Convert.FromBase64String(envelope.Manifest);
            signature = Convert.FromBase64String(envelope.Signature);
            key = Convert.FromBase64String(publicKey);
        }
        catch (FormatException) { throw Invalid("The update manifest encoding is invalid."); }
        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(key, out int read);
            if (read != key.Length || ecdsa.KeySize != 256) throw Invalid("The update signing key is not an ECDSA P-256 key.");
        }
        catch (CryptographicException) { throw Invalid("The update signing key is invalid."); }
        if (signature.Length != 64 ||
            !ecdsa.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw Invalid("The update signature is invalid.");
        return Parse(manifest);
    }

    /// <summary>Reads a manifest without signature verification. Only for server-side routing, never for installation.</summary>
    public static DesktopUpdateManifest ReadUnverified(SignedDesktopUpdate envelope)
    {
        try { return Parse(Convert.FromBase64String(envelope.Manifest)); }
        catch (FormatException) { throw Invalid("The update manifest encoding is invalid."); }
    }

    private static DesktopUpdateManifest Parse(byte[] bytes)
    {
        DesktopUpdateManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<DesktopUpdateManifest>(bytes, Json); }
        catch (JsonException) { throw Invalid("The signed update manifest is not valid JSON."); }
        if (manifest is null || manifest.Product != Product || !IsRuntime(manifest.Runtime) || !IsVersion(manifest.Version) ||
            manifest.Length is <= 0 or > MaxPackageBytes || manifest.Sha256 is not { Length: 64 } || !manifest.Sha256.All(IsLowerHex))
            throw Invalid("The signed update manifest is invalid.");
        return manifest;
    }

    private static bool IsLowerHex(char character) => character is >= '0' and <= '9' or >= 'a' and <= 'f';
    private static InvalidDataException Invalid(string message) => new(message);
}
