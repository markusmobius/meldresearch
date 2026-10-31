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
        group.MapPost("/account/link", async Task<Ok<AccountLinkResponse>> (HttpContext context, IResearchCoordinator service, CancellationToken ct) =>
            // The Host is already restricted to this app's own loopback address and port.
            TypedResults.Ok(await service.BeginLinkAsync($"http://{context.Request.Host}/account/callback", ct)))
            .WithName("LinkResearchAccount").WithSummary("Start signing in with Google on the Meld website; it returns to this app.");
        // The website's redirect back to this app (a top-level navigation, so it is outside the /api same-origin checks).
        // The one-time state proves the sign-in started here; the code is redeemed with this app's private PKCE verifier.
        app.MapGet("/account/callback", async Task<RedirectHttpResult> (
            string? code, string? state, IResearchCoordinator service, CancellationToken ct) =>
        {
            await service.CompleteLinkAsync(code, state, ct);
            return TypedResults.Redirect("/");
        }).ExcludeFromDescription();
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
        group.MapPut("/projects/{projectId}/decision", async Task<NoContent> (
            string projectId, ProjectDecisionRequest request, IResearchCoordinator service, CancellationToken ct) =>
        {
            await service.DecideProjectAsync(projectId, request, ct);
            return TypedResults.NoContent();
        }).WithName("DecideResearchProject").WithSummary("Include a project in the private profile (starts monitoring; needs consent) or exclude it.");
        group.MapPost("/projects/{projectId}/summary", async Task<Accepted> (
            string projectId, IResearchCoordinator service, CancellationToken ct) =>
        {
            await service.RequestSummaryAsync(projectId, ct);
            return TypedResults.Accepted("/api/research/status");
        }).WithName("SummarizeResearchProject").WithSummary("Queue a one-paragraph summary of a recent project from its main .tex file; choosing it sends only that file to Foundry.");
        group.MapPost("/researcher", async Task<NoContent> (
            ResearcherDecisionRequest request, IResearcherService service, CancellationToken ct) =>
        {
            await service.DecideAsync(request, ct);
            return TypedResults.NoContent();
        }).WithName("DecideResearcher").WithSummary("Search OpenAlex for a name, confirm an offered author, or reject all candidates.");
        group.MapGet("/publications", async Task<Ok<PublicationsResponse>> (IResearcherService service, CancellationToken ct) =>
            TypedResults.Ok(await service.PublicationsAsync(ct)))
            .WithName("GetPublications").WithSummary("Read the confirmed OpenAlex author's public works stored on this computer.");
        group.MapPost("/sync", async Task<Accepted> (IResearchCoordinator service, CancellationToken ct) =>
        {
            if ((await service.StatusAsync(ct)).Account is null)
                throw new MeldResearch.Research.ResearchFailure(401, "meld_account_required", "Sign in with Google before checking monitored projects.");
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
