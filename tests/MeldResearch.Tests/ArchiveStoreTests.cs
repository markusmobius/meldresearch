using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.LocalBackend.Middleware;
using MeldResearch.LocalBackend.Services;

namespace MeldResearch.Tests;

public sealed class ArchiveStoreTests : IDisposable
{
    private readonly TemporaryDirectory _temporary = new();
    public void Dispose() => _temporary.Dispose();

    [Fact]
    public async Task ValidatesAndPersistsASnapshotWithoutExtractingFiles()
    {
        string source = CreateZip(("main.tex", "Synthetic paper"), ("figures/data.txt", "Synthetic data"));
        string destination = Path.Combine(_temporary.Path, "downloads");
        var store = new ArchiveStore(destination);
        var project = new OverleafProjectResponse("0123456789abcdef01234567", "Synthetic project", null);
        var saved = await store.SaveAsync(project, source, CancellationToken.None);
        Assert.False(File.Exists(source));
        Assert.Equal(2, saved.Files.Count);
        Assert.False(File.Exists(Path.Combine(destination, "main.tex")));

        var reloaded = new ArchiveStore(destination);
        var result = Assert.Single(await reloaded.ListAsync(CancellationToken.None));
        Assert.Equal(saved.Id, result.Id);
        Assert.Equal(saved.ProjectName, result.ProjectName);
        var archive = await reloaded.GetAsync(saved.Id, CancellationToken.None);
        Assert.Equal(saved.Bytes, new FileInfo(archive.Path).Length);
    }

    [Theory]
    [InlineData("../escape.tex")]
    [InlineData("..\\escape.tex")]
    [InlineData("/absolute.tex")]
    [InlineData("C:\\absolute.tex")]
    [InlineData("folder/./file.tex")]
    [InlineData("folder//file.tex")]
    [InlineData("folder/file.tex:stream")]
    [InlineData("folder/../file.tex")]
    public async Task RejectsNonPortableOrTraversingPaths(string path)
    {
        string archive = CreateZip((path, "Synthetic"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveStore.ValidateAsync(archive, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsCaseCollisions()
    {
        string archive = CreateZip(("main.tex", "One"), ("MAIN.tex", "Two"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveStore.ValidateAsync(archive, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsSymbolicLinks()
    {
        string path = NewPath();
        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("link");
            entry.ExternalAttributes = unchecked((int)0xa1ff0000);
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveStore.ValidateAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task RejectsLoginHtmlDisguisedAsAZip()
    {
        string path = NewPath();
        await File.WriteAllTextAsync(path, "<html>Sign in</html>");
        var store = new ArchiveStore(Path.Combine(_temporary.Path, "downloads"));
        var failure = await Assert.ThrowsAsync<ApiFailure>(() => store.SaveAsync(
            new("0123456789abcdef01234567", "Synthetic", null), path, CancellationToken.None));
        Assert.Equal("invalid_project_archive", failure.Code);
        Assert.Empty(await store.ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task EnforcesTheExactExpandedSizeLimit()
    {
        string path = NewPath();
        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("synthetic.bin", CompressionLevel.Fastest);
            using var contents = entry.Open();
            var buffer = new byte[1024 * 1024];
            for (long written = 0; written < ArchiveStore.MaxExpandedBytes; written += buffer.Length)
                contents.Write(buffer);
        }
        var files = await ArchiveStore.ValidateAsync(path, CancellationToken.None);
        Assert.Equal(ArchiveStore.MaxExpandedBytes, Assert.Single(files).Bytes);

        byte[] bytes = await File.ReadAllBytesAsync(path);
        int centralDirectory = -1;
        for (int offset = bytes.Length - 46; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)) == 0x02014b50)
            {
                centralDirectory = offset;
                break;
            }
        }
        Assert.True(centralDirectory >= 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(centralDirectory + 24), (uint)ArchiveStore.MaxExpandedBytes + 1);
        await File.WriteAllBytesAsync(path, bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveStore.ValidateAsync(path, CancellationToken.None));
    }

    [Theory]
    [InlineData(ArchiveStore.MaxEntries, true)]
    [InlineData(ArchiveStore.MaxEntries + 1, false)]
    public async Task EnforcesTheExactEntryCountLimit(int count, bool accepted)
    {
        string path = NewPath();
        using (var file = File.Create(path))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            for (int index = 0; index < count; index++)
                archive.CreateEntry($"synthetic-{index}.txt");

        if (accepted)
            Assert.Equal(count, (await ArchiveStore.ValidateAsync(path, CancellationToken.None)).Count);
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => ArchiveStore.ValidateAsync(path, CancellationToken.None));
    }

    [Fact]
    public async Task MissingSnapshotReturnsAnExplicitNotFound()
    {
        var store = new ArchiveStore(Path.Combine(_temporary.Path, "downloads"));
        var failure = await Assert.ThrowsAsync<ApiFailure>(() => store.GetAsync(Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(404, failure.Status);
    }

    private string NewPath() => Path.Combine(_temporary.Path, Guid.NewGuid().ToString("N") + ".zip");

    private string CreateZip(params (string Path, string Content)[] files)
    {
        string path = NewPath();
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var item in files)
        {
            using var content = archive.CreateEntry(item.Path).Open();
            content.Write(Encoding.UTF8.GetBytes(item.Content));
        }
        return path;
    }
}
