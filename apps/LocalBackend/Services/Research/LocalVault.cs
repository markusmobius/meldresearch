using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MeldResearch.BrowserProtocol;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

internal interface ILocalVault
{
    Task<LocalResearchState> ReadAsync(CancellationToken ct);
    Task WriteAsync(LocalResearchState state, CancellationToken ct);
}

internal sealed class LocalVault : ILocalVault, IDisposable
{
    private const int MaximumBytes = 64 * 1024 * 1024;
    private static readonly byte[] Magic = "MELDRS01"u8.ToArray();
    private readonly string _path;
    private readonly byte[] _key;

    public LocalVault() : this(Path.Combine(RuntimeFiles.Root, "research"), null) { }

    internal LocalVault(string directory, byte[]? key)
    {
        RuntimeFiles.CreatePrivateDirectory(directory);
        _path = Path.Combine(directory, "research.protected");
        _key = key?.ToArray() ?? LoadKey(directory);
        if (_key.Length != 32) throw new CryptographicException("The local research key is invalid.");
    }

    public async Task<LocalResearchState> ReadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new(1, null, null, null, [], []);
        if (new FileInfo(_path).Length is < 36 or > MaximumBytes)
            throw Damaged();
        byte[] encrypted = await File.ReadAllBytesAsync(_path, ct);
        if (!encrypted.AsSpan(0, 8).SequenceEqual(Magic)) throw Damaged();
        byte[] plaintext = new byte[encrypted.Length - 36];
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(encrypted.AsSpan(8, 12), encrypted.AsSpan(36), encrypted.AsSpan(20, 16), plaintext, Magic);
            var state = JsonSerializer.Deserialize<LocalResearchState>(plaintext, ResearchJson.Options);
            return state is { Version: 1, Projects: not null, PendingRevocations: not null } ? state : throw Damaged();
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            throw Damaged();
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public async Task WriteAsync(LocalResearchState state, CancellationToken ct)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(state, ResearchJson.Options);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            if (bytes.Length > MaximumBytes - 36)
                throw new ResearchFailure(413, "local_research_limit", "The local research store reached its size limit. Existing data was retained.");
            byte[] encrypted = new byte[bytes.Length + 36];
            Magic.CopyTo(encrypted, 0);
            RandomNumberGenerator.Fill(encrypted.AsSpan(8, 12));
            using var aes = new AesGcm(_key, 16);
            aes.Encrypt(encrypted.AsSpan(8, 12), bytes, encrypted.AsSpan(36), encrypted.AsSpan(20, 16), Magic);
            await using (var stream = RuntimeFiles.CreatePrivateFile(temporary))
                await stream.WriteAsync(encrypted, ct);
            File.Move(temporary, _path, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static byte[] LoadKey(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            string path = Path.Combine(directory, "key.dpapi");
            if (File.Exists(path))
                return ProtectedData.Unprotect(File.ReadAllBytes(path), Magic, DataProtectionScope.CurrentUser);
            byte[] key = RandomNumberGenerator.GetBytes(32);
            try
            {
                byte[] wrapped = ProtectedData.Protect(key, Magic, DataProtectionScope.CurrentUser);
                using var stream = RuntimeFiles.CreatePrivateFile(path);
                stream.Write(wrapped);
                return key;
            }
            catch { CryptographicOperations.ZeroMemory(key); throw; }
        }
        if (OperatingSystem.IsMacOS()) return MacKeychain.GetOrCreate(ResearchEvidence.Hash(Path.GetFullPath(directory)));
        throw new PlatformNotSupportedException("Local research storage requires Windows DPAPI or the macOS Keychain.");
    }

    private static ResearchFailure Damaged() => new(500, "research_store_damaged",
        "The encrypted research store could not be read. It was not reset or replaced.");
    public void Dispose() => CryptographicOperations.ZeroMemory(_key);

    private static class MacKeychain
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        [DllImport(Security)]
        private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
            uint accountLength, byte[] account, out uint passwordLength, out IntPtr password, IntPtr item);
        [DllImport(Security)]
        private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, byte[] service,
            uint accountLength, byte[] account, uint passwordLength, byte[] password, IntPtr item);
        [DllImport(Security)]
        private static extern int SecKeychainItemFreeContent(IntPtr attributes, IntPtr data);

        public static byte[] GetOrCreate(string identity)
        {
            byte[] service = Encoding.UTF8.GetBytes("org.meldresearch.research-key");
            byte[] account = Encoding.UTF8.GetBytes(identity);
            int status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)service.Length, service,
                (uint)account.Length, account, out uint length, out IntPtr pointer, IntPtr.Zero);
            if (status == 0)
            {
                try
                {
                    if (length != 32) throw new CryptographicException("The Keychain entry has an invalid size.");
                    byte[] key = new byte[32];
                    Marshal.Copy(pointer, key, 0, key.Length);
                    return key;
                }
                finally { SecKeychainItemFreeContent(IntPtr.Zero, pointer); }
            }
            if (status != -25300) throw new CryptographicException("The macOS Keychain did not allow research-key access.");
            byte[] created = RandomNumberGenerator.GetBytes(32);
            status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)service.Length, service,
                (uint)account.Length, account, (uint)created.Length, created, IntPtr.Zero);
            if (status == 0) return created;
            CryptographicOperations.ZeroMemory(created);
            throw new CryptographicException("The macOS Keychain could not save the research key.");
        }
    }
}
