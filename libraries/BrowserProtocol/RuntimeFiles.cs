using System.Text.Json;

namespace MeldResearch.BrowserProtocol;

public sealed record BridgeConnection(int Version, int Port, string Token);

public static class RuntimeFiles
{
    public static string Root
    {
        get
        {
            string? configured = Environment.GetEnvironmentVariable("MELDRESEARCH_DATA_DIR");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (!Path.IsPathFullyQualified(configured))
                    throw new InvalidDataException("MELDRESEARCH_DATA_DIR must be an absolute path.");
                return Path.GetFullPath(configured);
            }

            string parent = OperatingSystem.IsMacOS()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(parent))
                throw new InvalidOperationException("A per-user application data directory is required.");
            return Path.Combine(parent, "MeldResearch", "OverleafPrototype");
        }
    }

    public static string ConnectionPath => Path.Combine(Root, "bridge-connection.json");

    public static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public static FileStream CreatePrivateFile(string path)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous
        };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    public static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var stream = CreatePrivateFile(temporary))
                await JsonSerializer.SerializeAsync(stream, value, WireProtocol.Json, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static async Task<BridgeConnection> ReadConnectionAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(ConnectionPath);
        if (stream.Length is 0 or > 4096)
            throw new InvalidDataException("Invalid local bridge configuration.");

        var connection = await JsonSerializer.DeserializeAsync<BridgeConnection>(stream, WireProtocol.Json, cancellationToken);
        if (connection is null || connection.Version != WireProtocol.Version ||
            connection.Port is < 1024 or > 65535 or 6000 ||
            string.IsNullOrEmpty(connection.Token) || connection.Token.Length != 64 || !connection.Token.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid local bridge configuration.");
        return connection;
    }
}
