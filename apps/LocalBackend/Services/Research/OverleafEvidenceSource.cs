using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Services.Research;

internal interface IOverleafEvidenceSource
{
    bool Available { get; }
    Task<IReadOnlyList<OverleafProjectResponse>> ProjectsAsync(CancellationToken ct);
    Task<ResearchSnapshot> SnapshotAsync(OverleafProjectResponse project, CancellationToken ct);
}

internal sealed class OverleafEvidenceSource(IBrowserRelay browser, IOverleafService overleaf) : IOverleafEvidenceSource
{
    public bool Available => browser.Status.Connected && !browser.Status.Busy;
    public Task<IReadOnlyList<OverleafProjectResponse>> ProjectsAsync(CancellationToken ct) => overleaf.RefreshAsync(ct);
    public async Task<ResearchSnapshot> SnapshotAsync(OverleafProjectResponse project, CancellationToken ct)
    {
        await using var response = await browser.FetchAsync("downloadProject", project.Id, ct);
        await ArchiveStore.ValidateAsync(response.Path, ct);
        return await ReadSnapshotAsync(response.Path, project, DateTimeOffset.UtcNow, ct);
    }

    internal static async Task<ResearchSnapshot> ReadSnapshotAsync(string path, OverleafProjectResponse project,
        DateTimeOffset observedAt, CancellationToken ct)
    {
        using var archive = ZipFile.OpenRead(path);
        var entries = archive.Entries.Where(entry => !entry.FullName.Replace('\\', '/').EndsWith('/')).ToArray();
        if (entries.Length > ResearchEvidence.MaxFiles)
            throw new ResearchFailure(413, "research_file_limit", "The project exceeds the research-ingestion file limit.");
        var files = new List<ResearchFile>();
        long textBytes = 0;
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            string name = entry.FullName.Replace('\\', '/');
            await using var stream = entry.Open();
            if (ResearchEvidence.TextExtensions.Contains(Path.GetExtension(name)))
            {
                if (entry.Length > ResearchEvidence.MaxFileTextBytes ||
                    textBytes + entry.Length > ResearchEvidence.MaxProjectTextBytes)
                    throw new ResearchFailure(413, "research_text_limit", "The project exceeds the supported text size. Nothing was partially indexed.");
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory, ct);
                byte[] bytes = memory.ToArray();
                string text;
                try { text = new UTF8Encoding(false, true).GetString(bytes); }
                catch (DecoderFallbackException) { throw new ResearchFailure(422, "unsupported_text_encoding", "Research text sources must use UTF-8. Existing knowledge was preserved."); }
                if (text.Contains('\0')) throw new ResearchFailure(422, "binary_text_source", "A supported text source contains binary data. Existing knowledge was preserved.");
                files.Add(new(name, ResearchEvidence.Hash(bytes), bytes.Length, text));
                textBytes += bytes.Length;
            }
            else
            {
                byte[] hash = await SHA256.HashDataAsync(stream, ct);
                files.Add(new(name, Convert.ToHexStringLower(hash), entry.Length, null));
            }
        }
        var snapshot = new ResearchSnapshot(project.Id, project.Name, ResearchEvidence.Revision(files), observedAt, project.UpdatedAt, files);
        ResearchEvidence.ValidateSnapshot(snapshot);
        return snapshot;
    }
}
