using Microsoft.AspNetCore.Http.HttpResults;
using MeldResearch.LocalBackend.Contracts;
using MeldResearch.LocalBackend.Services;
using MeldResearch.LocalBackend.Services.Research;

namespace MeldResearch.LocalBackend.Endpoints;

internal static class ResearchEndpoints
{
    public static void MapResearch(this WebApplication app)
    {
        var group = app.MapGroup("/api/research").WithTags("Research updates");
        group.MapGet("/status", async Task<Ok<ResearchStatusResponse>> (IResearchCoordinator service, CancellationToken ct) =>
            TypedResults.Ok(await service.StatusAsync(ct)))
            .WithName("GetResearchStatus").WithSummary("Read account linking, selected-project monitoring, and Updates-only feed state.");
        group.MapPost("/account/link", async Task<Ok<AccountLinkResponse>> (IResearchCoordinator service, CancellationToken ct) =>
            TypedResults.Ok(await service.BeginLinkAsync(ct)))
            .WithName("LinkResearchAccount").WithSummary("Begin Google-account device approval in the normal browser.");
        group.MapDelete("/account", async Task<NoContent> (IResearchCoordinator service, CancellationToken ct) =>
        {
            await service.UnlinkAsync(ct);
            return TypedResults.NoContent();
        }).WithName("UnlinkResearchAccount").WithSummary("Stop local processing and revoke the installation credential.");
        group.MapPut("/projects/{projectId}", async Task<NoContent> (
            string projectId, SetProjectMonitoringRequest request, IResearchCoordinator service, CancellationToken ct) =>
        {
            await service.SetMonitoringAsync(projectId, request, ct);
            return TypedResults.NoContent();
        }).WithName("SetProjectMonitoring").WithSummary("Explicitly consent to monitoring selected Overleaf text sources, or pause it.");
        group.MapDelete("/projects/{projectId}", async Task<NoContent> (
            string projectId, IResearchCoordinator service, CancellationToken ct) =>
        {
            await service.ForgetAsync(projectId, ct);
            return TypedResults.NoContent();
        }).WithName("ForgetResearchProject").WithSummary("Delete this account's indexed project and updates without changing Overleaf or manual ZIP copies.");
        group.MapPost("/sync", async Task<Accepted> (IResearchCoordinator service, CancellationToken ct) =>
        {
            if ((await service.StatusAsync(ct)).Account is null)
                throw new MeldResearch.Research.ResearchFailure(401, "meld_account_required", "Link a Meld account before checking monitored projects.");
            service.RequestSync();
            return TypedResults.Accepted("/api/research/status");
        }).WithName("SyncResearchNow").WithSummary("Queue a content check; unchanged snapshots do not consume model calls.");
        if (app.Services.GetRequiredService<ResearchOptions>().Dummy)
            app.MapPost("/api/development/overleaf/change", async (IBrowserRelay browser, IResearchCoordinator research, CancellationToken ct) =>
            {
                ct.ThrowIfCancellationRequested();
                int revision = await ((DummyBrowserRelay)browser).AdvanceAsync(ct);
                research.RequestSync();
                return TypedResults.Ok(new { revision });
            }).ExcludeFromDescription();
    }
}
