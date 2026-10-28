using System.Security.Cryptography;
using System.Text;

namespace MeldResearch.BrowserProtocol;

public static class NativeHandshake
{
    public const string NonceHeader = "X-Meld-Nonce";
    public const string ServerProofHeader = "X-Meld-Server-Proof";
    public const string AuthorizationPrefix = "MeldNative ";

    public static string CreateClientProof(string key, string nonce) => CreateProof(key, nonce, "client");
    public static string CreateServerProof(string key, string nonce) => CreateProof(key, nonce, "server");

    public static bool VerifyClientProof(string key, string? nonce, string? proof) =>
        IsHex(nonce) && Verify(CreateClientProof(key, nonce!), proof);

    public static bool VerifyServerProof(string key, string? nonce, string? proof) =>
        IsHex(nonce) && Verify(CreateServerProof(key, nonce!), proof);

    private static string CreateProof(string key, string nonce, string role)
    {
        if (!IsHex(key) || !IsHex(nonce))
            throw new ArgumentException("Native handshake values must be 32-byte hexadecimal strings.");
        // Different roles prevent a rogue listener from reflecting the client's proof back to it.
        byte[] payload = Encoding.ASCII.GetBytes($"meld-native-v2:{role}:{nonce.ToUpperInvariant()}");
        return Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(key), payload));
    }

    private static bool Verify(string expected, string? supplied) =>
        IsHex(supplied) && CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(expected), Convert.FromHexString(supplied!));

    private static bool IsHex(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
