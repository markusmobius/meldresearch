using System.Text;
using System.Text.Json;
using MeldResearch.LocalBackend.Middleware;
using MeldResearch.LocalBackend.Services;

namespace MeldResearch.Tests;

public sealed class ProjectParserTests
{
    [Fact]
    public async Task ReadsMetadataAndFiltersArchivedProjects()
    {
        var projects = new[]
        {
            new { id = "0123456789abcdef01234567", name = "Synthetic & <untrusted> paper", archived = false, trashed = false, lastUpdated = "2026-10-01T10:00:00+02:00" },
            new { id = "1123456789abcdef01234567", name = "Archived", archived = true, trashed = false, lastUpdated = "2026-10-01T10:00:00Z" },
            new { id = "2123456789abcdef01234567", name = "Trashed", archived = false, trashed = true, lastUpdated = "2026-10-01T10:00:00Z" }
        };
        using var html = Page(JsonSerializer.Serialize(new { projects }));
        var result = await ProjectParser.ParseAsync(html, CancellationToken.None);
        var project = Assert.Single(result);
        Assert.Equal("Synthetic & <untrusted> paper", project.Name);
        Assert.Equal(TimeSpan.FromHours(2), project.UpdatedAt!.Value.Offset);
    }

    [Fact]
    public async Task AcceptsAnExplicitlyEmptyProjectList()
    {
        using var html = Page("""{"projects":[]}""");
        Assert.Empty(await ProjectParser.ParseAsync(html, CancellationToken.None));
    }

    [Fact]
    public async Task LoginOrChallengePagesAreNotReportedAsEmptyLists()
    {
        using var html = new MemoryStream("<html><form action='/login'></form></html>"u8.ToArray());
        var error = await Assert.ThrowsAsync<ApiFailure>(() => ProjectParser.ParseAsync(html, CancellationToken.None));
        Assert.Equal("overleaf_metadata_changed", error.Code);
    }

    [Theory]
    [InlineData("""{"projects":[],"projects":[]}""")]
    [InlineData("""{"projects":{}}""")]
    [InlineData("""{"projects":[{"id":"bad","name":"PRIVATE-SENTINEL","archived":false,"trashed":false}]}""")]
    [InlineData("""{"projects":[{"id":"0123456789abcdef01234567","name":"Synthetic","archived":"false","trashed":false}]}""")]
    [InlineData("""{"projects":[{"id":"0123456789abcdef01234567","name":"Synthetic","archived":false,"trashed":false,"lastUpdated":"yesterday"}]}""")]
    [InlineData("""{"projects":[{"id":"0123456789abcdef01234567","name":"Synthetic","archived":false,"trashed":false},{"id":"0123456789abcdef01234567","name":"Duplicate","archived":false,"trashed":false}]}""")]
    public async Task RejectsUnexpectedMetadataWithoutEchoingContent(string json)
    {
        using var html = Page(json);
        var error = await Assert.ThrowsAsync<ApiFailure>(() => ProjectParser.ParseAsync(html, CancellationToken.None));
        Assert.Equal("overleaf_metadata_changed", error.Code);
        Assert.DoesNotContain("PRIVATE-SENTINEL", error.Message);
    }

    private static MemoryStream Page(string json) => new(Encoding.UTF8.GetBytes(json));
}
