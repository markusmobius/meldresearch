namespace MeldResearch.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MeldResearch-tests", Guid.NewGuid().ToString("N"));

    public TemporaryDirectory() => Directory.CreateDirectory(Path);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
