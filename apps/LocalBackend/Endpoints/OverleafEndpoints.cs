using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http.HttpResults;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.LocalBackend.Services;
using MeldResearch.LocalBackend.Services.Research;
using MeldResearch.LocalBackend.Services.Updates;

namespace MeldResearch.LocalBackend.Endpoints;

internal static class OverleafEndpoints
{
    public static void MapOverleaf(this WebApplication app)
    {
        app.MapGet("/api/status", (HttpContext context, IAntiforgery antiforgery, IBrowserRelay browser, CancellationToken cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tokens = antiforgery.GetAndStoreTokens(context);
            context.Response.Cookies.Append("XSRF-TOKEN", tokens.RequestToken!,
                new CookieOptions { HttpOnly = false, SameSite = SameSiteMode.Strict, Path = "/", IsEssential = true });
            return TypedResults.Ok(browser.Status);
        }).WithName("GetBridgeStatus").WithSummary("Read connection status and initialize local request protection.");

        app.MapGet("/api/extension", (IExtensionSetupService service, CancellationToken cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TypedResults.Ok(service.GetSetup());
        }).WithName("GetExtensionSetup").WithSummary("Read the browser connector identity and development setup instructions.");

        app.MapGet("/api/version", (DesktopUpdater updater, CancellationToken cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return TypedResults.Ok(updater.Status);
        }).WithName("GetDesktopVersion").WithSummary("Read the installed desktop release and automatic-update state.");

        app.MapGet("/api/remote-ping", async Task<Ok<RemotePingResponse>> (RemoteConnectivity remote, CancellationToken ct) =>
            TypedResults.Ok(await remote.CheckAsync(ct)))
            .WithName("PingRemoteService").WithSummary("Report whether the Meld Research remote service is reachable (no credentials are sent).");

        var overleaf = app.MapGroup("/api/overleaf").WithTags("Overleaf");
        overleaf.MapPost("/projects/refresh", async Task<Ok<ProjectListResponse>> (
            IOverleafService service, CancellationToken cancellationToken) =>
            TypedResults.Ok(new ProjectListResponse(await service.RefreshAsync(cancellationToken))))
            .WithName("RefreshOverleafProjects").WithSummary("Read accessible projects through the connected browser.")
            .WithDescription("Reads through the connected browser extension. Archived and trashed projects are omitted.");

        overleaf.MapPost("/projects/{projectId}/downloads", async Task<Created<DownloadResponse>> (
            string projectId, IOverleafService service, CancellationToken cancellationToken) =>
        {
            var download = await service.DownloadAsync(projectId, cancellationToken);
            return TypedResults.Created($"/api/overleaf/downloads/{download.Id}", download);
        }).WithName("DownloadOverleafProject").WithSummary("Download and validate a selected project into local storage.");

        overleaf.MapGet("/downloads", async Task<Ok<DownloadListResponse>> (
            IOverleafService service, CancellationToken cancellationToken) =>
            TypedResults.Ok(new DownloadListResponse(await service.ListDownloadsAsync(cancellationToken))))
            .WithName("ListDownloads").WithSummary("List persisted local project snapshots.");

        overleaf.MapGet("/downloads/{id:guid}", async Task<Ok<DownloadResponse>> (
            Guid id, IOverleafService service, CancellationToken cancellationToken) =>
            TypedResults.Ok((await service.GetDownloadAsync(id, cancellationToken)).Metadata))
            .WithName("GetDownload").WithSummary("Read snapshot metadata and its validated file listing.");

        overleaf.MapGet("/downloads/{id:guid}/archive", async Task<PhysicalFileHttpResult> (
            Guid id, IOverleafService service, CancellationToken cancellationToken) =>
        {
            var archive = await service.GetDownloadAsync(id, cancellationToken);
            return TypedResults.PhysicalFile(archive.Path, "application/zip", archive.Metadata.ProjectId + ".zip");
        }).WithName("GetDownloadArchive").WithSummary("Save a copy of an already downloaded ZIP.");
    }

}
