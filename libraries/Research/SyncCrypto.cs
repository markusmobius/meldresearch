using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MeldResearch.Research;

/// <summary>
/// The plaintext key file of a user's encrypted sync (blob "key.json"). It holds no secret: the data key is stored only
/// encrypted, once under a key derived from the passphrase and optionally once under the recovery key. SealedRecoveryKey is
/// the recovery key encrypted under the data key, so any computer that is already unlocked can show it again.
/// </summary>
public sealed record SyncKeyFile(int Version, string Kdf, int Iterations, string Salt, string WrappedKey, string? RecoveryWrappedKey,
    DateTimeOffset CreatedAt, string? SealedRecoveryKey = null);

/// <summary>
/// End-to-end encryption for synced research (Meld's server and Azure see only ciphertext). A random 256-bit data key per
/// account encrypts everything with AES-256-GCM. The data key is wrapped by a key derived from the user's passphrase
/// (PBKDF2-SHA256) and, optionally, by a random recovery key the user writes down. Blob contents are compressed before
/// encryption and bound to the account and blob name, so blobs cannot be swapped. Project blob names are an HMAC of the
/// project id, so the stored names do not reveal which projects a user has.
/// </summary>
public static partial class SyncCrypto
{
    /// <summary>
    /// The account's key file and its profile and project blobs, named by research format (OVERLEAF.md §13): format 3 since
    /// the detail review and the second fresh start. Blobs of earlier formats (profile.bin, p/..., profile2.bin, p2/...) are
    /// ignored and deleted by the first client that uploads in the current format, so they cannot bring old research back.
    /// </summary>
    public const string KeyBlob = "key.json", ProfileBlob = "profile3.bin";
    public const int Iterations = 600_000, MinPassphraseLength = 10, MaxPassphraseLength = 512;
    private const string Kdf = "pbkdf2-sha256";
    private static readonly byte[] Magic = "MRS1"u8.ToArray();

    [GeneratedRegex("^(key\\.json|profile[23]?\\.bin|p[23]?/[0-9a-f]{32}\\.bin)$")]
    private static partial Regex NamePattern();
    [GeneratedRegex("^(profile2?\\.bin|p2?/[0-9a-f]{32}\\.bin)$")]
    private static partial Regex LegacyPattern();

    /// <summary>A blob of the earlier research format, which the current format replaces.</summary>
    public static bool IsLegacyBlob(string? name) => name is not null && LegacyPattern().IsMatch(name);

    /// <summary>The only blob names a client may use inside its own account folder.</summary>
    public static bool IsBlobName(string? name) => name is not null && NamePattern().IsMatch(name);

    public static byte[] NewDataKey() => RandomNumberGenerator.GetBytes(32);

    public static void ValidatePassphrase(string? passphrase)
    {
        if (passphrase is null || passphrase.Trim().Length < MinPassphraseLength || passphrase.Length > MaxPassphraseLength)
            throw new ResearchFailure(400, "weak_passphrase", $"Choose a passphrase of at least {MinPassphraseLength} characters.");
    }

    /// <summary>A new key file for the data key, wrapped by the passphrase and, when given, the recovery key.</summary>
    public static SyncKeyFile CreateKeyFile(Guid account, byte[] dataKey, string passphrase, byte[]? recoveryKey, DateTimeOffset now,
        int iterations = Iterations)
    {
        ValidatePassphrase(passphrase);
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        return new(1, Kdf, iterations, Convert.ToBase64String(salt),
            Wrap(PassphraseKey(passphrase, salt, iterations), dataKey, account, "passphrase"),
            recoveryKey is null ? null : Wrap(RecoveryKek(recoveryKey), dataKey, account, "recovery"), now,
            recoveryKey is null ? null : Wrap(SubKey(dataKey, "recovery-copy"), recoveryKey, account, "recovery-copy"));
    }

    /// <summary>The recovery key for showing it again on an unlocked computer; null when the key file has no copy of it.</summary>
    public static byte[]? OpenRecoveryKey(SyncKeyFile file, Guid account, byte[] dataKey) =>
        file.SealedRecoveryKey is { } sealedKey ? Unwrap(SubKey(dataKey, "recovery-copy"), sealedKey, account, "recovery-copy") : null;

    /// <summary>The same data key with a new recovery key (the previous recovery key stops working).</summary>
    public static SyncKeyFile ReplaceRecoveryKey(SyncKeyFile file, Guid account, byte[] dataKey, byte[] recoveryKey) => file with
    {
        RecoveryWrappedKey = Wrap(RecoveryKek(recoveryKey), dataKey, account, "recovery"),
        SealedRecoveryKey = Wrap(SubKey(dataKey, "recovery-copy"), recoveryKey, account, "recovery-copy")
    };

    /// <summary>The data key, or null when the passphrase is wrong.</summary>
    public static byte[]? UnlockWithPassphrase(SyncKeyFile file, Guid account, string passphrase)
    {
        if (file.Kdf != Kdf || file.Iterations is < 100_000 or > 10_000_000) throw Unsupported();
        return Unwrap(PassphraseKey(passphrase, Convert.FromBase64String(file.Salt), file.Iterations), file.WrappedKey, account, "passphrase");
    }

    /// <summary>The data key, or null when the recovery key is wrong or the file has none.</summary>
    public static byte[]? UnlockWithRecoveryKey(SyncKeyFile file, Guid account, string recoveryKey) =>
        file.RecoveryWrappedKey is { } wrapped && ParseRecoveryKey(recoveryKey) is { } key
            ? Unwrap(RecoveryKek(key), wrapped, account, "recovery") : null;

    /// <summary>The same data key under a new passphrase (the recovery key keeps working).</summary>
    public static SyncKeyFile ChangePassphrase(SyncKeyFile file, Guid account, byte[] dataKey, string passphrase)
    {
        ValidatePassphrase(passphrase);
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        return file with { Kdf = Kdf, Iterations = Iterations, Salt = Convert.ToBase64String(salt),
            WrappedKey = Wrap(PassphraseKey(passphrase, salt, Iterations), dataKey, account, "passphrase") };
    }

    public static byte[] NewRecoveryKey() => RandomNumberGenerator.GetBytes(20);

    /// <summary>The recovery key as 8 groups of 4 letters and digits, easy to write down (Crockford base32).</summary>
    public static string FormatRecoveryKey(byte[] key)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        var text = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (byte value in key)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5) { text.Append(alphabet[(buffer >> (bits - 5)) & 31]); bits -= 5; }
        }
        if (bits > 0) text.Append(alphabet[(buffer << (5 - bits)) & 31]);
        return string.Join('-', Enumerable.Range(0, text.Length / 4).Select(index => text.ToString(index * 4, 4)));
    }

    public static byte[]? ParseRecoveryKey(string? text)
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        string clean = new((text ?? "").ToUpperInvariant().Where(char.IsLetterOrDigit).Select(c => c switch { 'O' => '0', 'I' or 'L' => '1', _ => c }).ToArray());
        if (clean.Length != 32 || clean.Any(c => alphabet.IndexOf(c) < 0)) return null;
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (char c in clean)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8) { bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF)); bits -= 8; }
        }
        return bytes.Count == 20 ? [.. bytes] : null;
    }

    /// <summary>The blob name of a project's bundle: an HMAC of its id under the data key, so names reveal nothing.</summary>
    public static string ProjectBlob(byte[] dataKey, string projectId) =>
        "p3/" + Convert.ToHexStringLower(HMACSHA256.HashData(SubKey(dataKey, "names"), Encoding.UTF8.GetBytes(projectId)))[..32] + ".bin";

    /// <summary>Compresses and encrypts a value for one blob of one account.</summary>
    public static byte[] Seal<T>(byte[] dataKey, Guid account, string blobName, T value)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            JsonSerializer.Serialize(gzip, value, ResearchJson.Options);
        byte[] plain = compressed.ToArray(), nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], cipher = new byte[plain.Length];
        using (var aes = new AesGcm(SubKey(dataKey, "encrypt"), 16))
            aes.Encrypt(nonce, plain, cipher, tag, Aad(account, blobName));
        return [.. Magic, .. nonce, .. tag, .. cipher];
    }

    /// <summary>Decrypts a blob sealed for this account and name; throws when it was altered, moved or sealed with another key.</summary>
    public static T Open<T>(byte[] dataKey, Guid account, string blobName, byte[] sealedValue)
    {
        if (sealedValue.Length < Magic.Length + 28 || !sealedValue.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw Unsupported();
        var nonce = sealedValue.AsSpan(4, 12);
        var tag = sealedValue.AsSpan(16, 16);
        var cipher = sealedValue.AsSpan(32);
        byte[] plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(SubKey(dataKey, "encrypt"), 16);
            aes.Decrypt(nonce, cipher, tag, plain, Aad(account, blobName));
        }
        catch (CryptographicException) { throw new ResearchFailure(422, "sync_blob_invalid", "Synced data could not be decrypted with this key."); }
        using var gzip = new GZipStream(new MemoryStream(plain), CompressionMode.Decompress);
        return JsonSerializer.Deserialize<T>(gzip, ResearchJson.Options) ?? throw Unsupported();
    }

    private static byte[] PassphraseKey(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase.Normalize(NormalizationForm.FormC)), salt, iterations, HashAlgorithmName.SHA256, 32);

    private static byte[] RecoveryKek(byte[] recoveryKey) => HKDF.DeriveKey(HashAlgorithmName.SHA256, recoveryKey, 32, info: "meldresearch-recovery"u8.ToArray());
    private static byte[] SubKey(byte[] dataKey, string purpose) => HKDF.DeriveKey(HashAlgorithmName.SHA256, dataKey, 32, info: Encoding.UTF8.GetBytes("meldresearch-" + purpose));
    private static byte[] Aad(Guid account, string blobName) => Encoding.UTF8.GetBytes($"meldresearch-sync-v1|{account:N}|{blobName}");

    private static string Wrap(byte[] kek, byte[] key, Guid account, string kind)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16], cipher = new byte[key.Length];
        using var aes = new AesGcm(kek, 16);
        aes.Encrypt(nonce, key, cipher, tag, Aad(account, "key:" + kind));
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    private static byte[]? Unwrap(byte[] kek, string wrapped, Guid account, string kind)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(wrapped); }
        catch (FormatException) { throw Unsupported(); }
        // A wrapped data key (32 bytes) or a sealed recovery key (20 bytes), after the nonce and tag.
        if (bytes.Length is not (60 or 48)) throw Unsupported();
        byte[] key = new byte[bytes.Length - 28];
        try
        {
            using var aes = new AesGcm(kek, 16);
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), key, Aad(account, "key:" + kind));
            return key;
        }
        catch (CryptographicException) { return null; }
    }

    private static ResearchFailure Unsupported() => new(422, "sync_format_unsupported", "The synced data uses an unsupported format.");
}
