using MeldResearch.LocalBackend.Contracts;
using MeldResearch.LocalBackend.Middleware;

namespace MeldResearch.LocalBackend.Services;

internal interface IOverleafService
{
    /// <summary>The last project list read through the browser, by the page or by monitoring.</summary>
    OverleafProjectsSnapshot? Latest { get; }
    Task<IReadOnlyList<OverleafProjectResponse>> RefreshAsync(CancellationToken cancellationToken);
    Task<DownloadResponse> DownloadAsync(string projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<DownloadResponse>> ListDownloadsAsync(CancellationToken cancellationToken);
    Task<StoredArchive> GetDownloadAsync(Guid id, CancellationToken cancellationToken);
}

internal sealed class OverleafService(IBrowserRelay browser, IArchiveStore archives, TimeProvider clock) : IOverleafService
{
    private IReadOnlyList<OverleafProjectResponse> _projects = [];
    private OverleafProjectsSnapshot? _latest;

    public OverleafProjectsSnapshot? Latest => Volatile.Read(ref _latest);

    public async Task<IReadOnlyList<OverleafProjectResponse>> RefreshAsync(CancellationToken cancellationToken)
    {
        await using var response = await browser.FetchAsync("listProjects", null, cancellationToken);
        if (!response.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
            throw new ApiFailure(502, "overleaf_metadata_changed", "The browser did not return the expected project metadata.");
        await using var stream = File.OpenRead(response.Path);
        var projects = await ProjectParser.ParseAsync(stream, cancellationToken);
        Interlocked.Exchange(ref _projects, projects);
        Volatile.Write(ref _latest, new OverleafProjectsSnapshot(projects, clock.GetUtcNow()));
        return projects;
    }

    public async Task<DownloadResponse> DownloadAsync(string projectId, CancellationToken cancellationToken)
    {
        if (!ProjectParser.IsProjectId(projectId))
            throw new ApiFailure(400, "invalid_project_id", "The project ID is invalid.");
        var project = Volatile.Read(ref _projects).FirstOrDefault(project => project.Id == projectId);
        if (project is null)
            throw new ApiFailure(409, "refresh_projects_first", "Refresh the project list and select a project before downloading.");

        await using var response = await browser.FetchAsync("downloadProject", project.Id, cancellationToken);
        if (response.ContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
            throw new ApiFailure(409, "overleaf_sign_in_required", "Overleaf returned a web page instead of a ZIP. Check its tab and sign-in.");
        return await archives.SaveAsync(project, response.Path, cancellationToken);
    }

    public Task<IReadOnlyList<DownloadResponse>> ListDownloadsAsync(CancellationToken cancellationToken) =>
        archives.ListAsync(cancellationToken);

    public Task<StoredArchive> GetDownloadAsync(Guid id, CancellationToken cancellationToken) =>
        archives.GetAsync(id, cancellationToken);
}
