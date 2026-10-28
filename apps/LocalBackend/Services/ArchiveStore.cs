using System.IO.Compression;
using System.Text.Json;
using MeldResearch.BrowserProtocol;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.LocalBackend.Middleware;

namespace MeldResearch.LocalBackend.Services;

internal sealed record StoredArchive(string Path, DownloadResponse Metadata);

internal interface IArchiveStore
{
    Task<DownloadResponse> SaveAsync(OverleafProjectResponse project, string source, CancellationToken cancellationToken);
    Task<IReadOnlyList<DownloadResponse>> ListAsync(CancellationToken cancellationToken);
    Task<StoredArchive> GetAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class ArchiveStore : IArchiveStore
{
    internal const long MaxExpandedBytes = 256L * 1024 * 1024;
    internal const int MaxEntries = 10_000;
    private readonly string _directory;

    public ArchiveStore() : this(Path.Combine(RuntimeFiles.Root, "downloads")) { }

    internal ArchiveStore(string directory)
    {
        _directory = directory;
        RuntimeFiles.CreatePrivateDirectory(_directory);
    }

    public async Task<DownloadResponse> SaveAsync(OverleafProjectResponse project, string source, CancellationToken cancellationToken)
    {
        IReadOnlyList<ArchiveEntryResponse> entries;
        try
        {
            entries = await ValidateAsync(source, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
        {
            throw new ApiFailure(502, "invalid_project_archive",
                "Overleaf did not return a safe, supported ZIP archive. Nothing was saved as a completed download.");
        }

        Guid id = Guid.NewGuid();
        string archivePath = ArchivePath(id);
        var metadata = new DownloadResponse(id, project.Id, project.Name, DateTimeOffset.UtcNow,
            new FileInfo(source).Length, entries);
        File.Move(source, archivePath);
        bool committed = false;
        try
        {
            await RuntimeFiles.WriteJsonAsync(MetadataPath(id), metadata, cancellationToken);
            committed = true;
            return metadata;
        }
        finally
        {
            if (!committed)
                File.Delete(archivePath);
        }
    }

    public async Task<IReadOnlyList<DownloadResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var downloads = new List<DownloadResponse>();
        foreach (string path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid id))
                throw CorruptStore();
            downloads.Add((await GetAsync(id, cancellationToken)).Metadata);
        }
        return downloads.OrderByDescending(download => download.DownloadedAt).ToArray();
    }

    public async Task<StoredArchive> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        string path = MetadataPath(id);
        if (!File.Exists(path))
            throw new ApiFailure(404, "download_not_found", "That local download was not found.");
        try
        {
            await using var stream = File.OpenRead(path);
            if (stream.Length > 8 * 1024 * 1024)
                throw CorruptStore();
            var metadata = await JsonSerializer.DeserializeAsync<DownloadResponse>(stream, WireProtocol.Json, cancellationToken);
            string archive = ArchivePath(id);
            if (metadata is null || metadata.Id != id || !ProjectParser.IsProjectId(metadata.ProjectId) ||
                string.IsNullOrWhiteSpace(metadata.ProjectName) || metadata.Files is null ||
                metadata.Files.Count > MaxEntries || !File.Exists(archive) || new FileInfo(archive).Length != metadata.Bytes)
                throw CorruptStore();
            return new(archive, metadata);
        }
        catch (JsonException)
        {
            throw CorruptStore();
        }
    }

    internal static async Task<IReadOnlyList<ArchiveEntryResponse>> ValidateAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        if (file.Length is 0 or > WireProtocol.MaxArchiveBytes)
            throw new InvalidDataException("Archive size limit exceeded.");
        using var archive = new ZipArchive(file, ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxEntries)
            throw new InvalidDataException("Too many archive entries.");

        var entries = new List<ArchiveEntryResponse>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buffer = new byte[64 * 1024];
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = entry.FullName.Replace('\\', '/');
            bool directory = normalized.EndsWith('/');
            string portablePath = normalized.TrimEnd('/');
            int fileType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (portablePath.Length is 0 or > 1024 || normalized.StartsWith('/') ||
                portablePath.Any(character => character < ' ' || "<>:\"|?*".Contains(character)) ||
                portablePath.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')) ||
                !paths.Add(portablePath) || fileType is not (0 or 0x8000 or 0x4000) ||
                (directory && entry.Length != 0) || entry.Length > MaxExpandedBytes - total)
                throw new InvalidDataException("Unsafe archive entry.");

            if (directory)
                continue;

            await using var content = entry.Open();
            long actual = 0;
            int read;
            while ((read = await content.ReadAsync(buffer, cancellationToken)) != 0)
            {
                actual += read;
                if (actual > entry.Length || actual > MaxExpandedBytes - total)
                    throw new InvalidDataException("Expanded archive size limit exceeded.");
            }
            if (actual != entry.Length)
                throw new InvalidDataException("Truncated archive entry.");
            total += actual;
            entries.Add(new(portablePath, actual));
        }
        return entries;
    }

    private string ArchivePath(Guid id) => Path.Combine(_directory, id.ToString("N") + ".zip");
    private string MetadataPath(Guid id) => Path.Combine(_directory, id.ToString("N") + ".json");
    private static ApiFailure CorruptStore() =>
        new(500, "local_download_damaged", "A saved download or its metadata is damaged. The existing files were left unchanged.");
}
