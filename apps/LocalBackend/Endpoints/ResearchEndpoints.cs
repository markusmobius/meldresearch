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
        group.MapPut("/projects/{projectId}/theme", async Task<NoContent> (
            string projectId, MoveToThemeRequest request, IResearchCoordinator service, CancellationToken ct) =>
        {
            await service.MoveToThemeAsync(projectId, request.ThemeId, ct);
            return TypedResults.NoContent();
        }).WithName("MoveResearchProjectToTheme").WithSummary("Move an imported project to another research card, or to a card of its own.");
        group.MapPut("/themes/{themeId}/merge", async Task<NoContent> (
            string themeId, MergeThemeRequest request, IResearchCoordinator service, CancellationToken ct) =>
        {
            await service.MergeThemeIntoAsync(themeId, request.IntoThemeId, ct);
            return TypedResults.NoContent();
        }).WithName("MergeResearchTheme").WithSummary("Combine a research card into another; its projects are pinned there.");
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
        group.MapGet("/account/picture", async Task<Results<FileContentHttpResult, NotFound>> (
            IResearchStore store, IResearchRemote remote, AccountPictureCache cache, CancellationToken ct) =>
        {
            var credential = (await store.ReadAsync(ct)).Credential;
            if (credential is null || credential.ExpiresAt <= DateTimeOffset.UtcNow) return TypedResults.NotFound();
            var picture = await cache.GetAsync(credential.Account.Id, () => remote.AccountPictureAsync(credential.Token, ct));
            return picture is null ? TypedResults.NotFound() : TypedResults.File(picture.Image, picture.ContentType);
        }).WithName("GetLocalAccountPicture").WithSummary("Read the signed-in account's profile picture from Meld's server (cached for an hour).");
        group.MapPost("/sync", async Task<Accepted> (IResearchCoordinator service, CancellationToken ct) =>
        {
            if ((await service.StatusAsync(ct)).Account is null)
                throw new MeldResearch.Research.ResearchFailure(401, "meld_account_required", "Sign in with Google before checking monitored projects.");
            service.RequestSync();
            return TypedResults.Accepted("/api/research/status");
        }).WithName("SyncResearchNow").WithSummary("Queue a content check; unchanged snapshots do not consume model calls.");
        group.MapGet("/encrypted-sync", async Task<Ok<SyncStatusResponse>> (ResearchCoordinator research, CancellationToken ct) =>
            TypedResults.Ok(await research.Sync.RefreshAsync(ct)))
            .WithName("GetEncryptedSync").WithSummary("Ask Meld's server whether encrypted sync is offered and whether this account already has a key.");
        group.MapPost("/encrypted-sync/create", async Task<Ok<SyncKeyResponse>> (
            SyncPassphraseRequest request, ResearchCoordinator research, CancellationToken ct) =>
            TypedResults.Ok(await research.WithSyncAsync((sync, credential) => sync.CreateAsync(credential, request.Passphrase, ct), ct)))
            .WithName("CreateEncryptedSync").WithSummary("Choose the passphrase; returns the recovery key once. Nothing readable leaves this computer.");
        group.MapPost("/encrypted-sync/unlock", async Task<NoContent> (
            SyncPassphraseRequest request, ResearchCoordinator research, CancellationToken ct) =>
        {
            await research.WithSyncAsync(async (sync, credential) => { await sync.UnlockAsync(credential, request, ct); return true; }, ct);
            return TypedResults.NoContent();
        }).WithName("UnlockEncryptedSync").WithSummary("Unlock the account's synced research on this computer with the passphrase or recovery key.");
        group.MapPost("/encrypted-sync/start-over", async Task<Ok<SyncKeyResponse>> (
            SyncPassphraseRequest request, ResearchCoordinator research, CancellationToken ct) =>
            TypedResults.Ok(await research.WithSyncAsync((sync, credential) => sync.StartOverAsync(credential, request.Passphrase, ct), ct)))
            .WithName("StartEncryptedSyncOver").WithSummary("Forgotten passphrase: delete the synced copy, choose a new passphrase and upload this computer's research again.");
        group.MapPost("/encrypted-sync/recovery-key", async Task<Ok<SyncKeyResponse>> (ResearchCoordinator research, CancellationToken ct) =>
            TypedResults.Ok(await research.WithSyncAsync((sync, credential) => sync.RecoveryKeyAsync(credential, ct), ct)))
            .WithName("ShowEncryptedSyncRecoveryKey").WithSummary("Show the recovery key again on a computer where encrypted sync is unlocked.");
        group.MapPost("/encrypted-sync/passphrase", async Task<NoContent> (
            SyncPassphraseRequest request, ResearchCoordinator research, CancellationToken ct) =>
        {
            await research.WithSyncAsync(async (sync, credential) => { await sync.ChangePassphraseAsync(credential, request.Passphrase, ct); return true; }, ct);
            return TypedResults.NoContent();
        }).WithName("ChangeEncryptedSyncPassphrase").WithSummary("Choose a new passphrase for the same synced data; the recovery key keeps working.");
        group.MapPost("/gmail/connect", async Task<Ok<GmailConnectResponse>> (
            GmailConnectRequest request, HttpContext context, GmailConnector gmail, CancellationToken ct) =>
            // The Host is already restricted to this app's own loopback address and port; Google returns there.
            TypedResults.Ok(await gmail.BeginAsync($"http://{context.Request.Host}/gmail/callback", request.ConsentVersion, ct)))
            .WithName("ConnectGmail").WithSummary("Start connecting a Gmail account for read-only access; Google's consent page returns to this app.");
        // Google's redirect back to this app (a top-level navigation); the one-time state proves the sign-in started here.
        app.MapGet("/gmail/callback", async Task<RedirectHttpResult> (string? code, string? state, string? error, GmailConnector gmail,
            ResearchCoordinator research, CancellationToken ct) =>
        {
            // A newly connected account is imported straight away.
            if (await gmail.CompleteAsync(code, state, error, ct) is null) research.RequestWork();
            return TypedResults.Redirect("/#gmail");
        }).ExcludeFromDescription();
        group.MapPost("/gmail/accounts/{address}/import", async Task<NoContent> (string address, GmailConnector gmail, GmailImporter importer,
            ResearchCoordinator research, IResearchStore store, CancellationToken ct) =>
        {
            gmail.Require(await store.ReadAsync(ct), address);
            importer.Request(address);
            research.RequestWork();
            return TypedResults.NoContent();
        }).WithName("ImportGmailAccount").WithSummary("Import new mail from a connected Gmail account now.");
        group.MapPost("/gmail/accounts/{address}/check", async Task<Ok<GmailAccountResponse>> (string address, GmailConnector gmail, CancellationToken ct) =>
            TypedResults.Ok(await gmail.CheckAsync(address, ct)))
            .WithName("CheckGmailAccount").WithSummary("Count the threads written in during the import window (no mail content is read).");
        group.MapDelete("/gmail/accounts/{address}", async Task<NoContent> (string address, GmailConnector gmail, CancellationToken ct) =>
        {
            await gmail.RemoveAsync(address, ct);
            return TypedResults.NoContent();
        }).WithName("RemoveGmailAccount").WithSummary("Disconnect a Gmail account: revoke Meld's access at Google and remove it everywhere.");
        group.MapGet("/export", async Task<Ok<ExportStateResponse>> (ResearchArchive archive, IResearchStore store, CancellationToken ct) =>
            TypedResults.Ok(new ExportStateResponse(archive.CurrentExport((await store.ReadAsync(ct)).Credential?.Account.Id))))
            .WithName("GetResearchExport").WithSummary("The download being prepared, or the last one if it is less than five minutes old.");
        group.MapPost("/export", async Task<Ok<ExportStateResponse>> (ResearchArchive archive, CancellationToken ct) =>
            TypedResults.Ok(new ExportStateResponse(await archive.StartExportAsync(ct))))
            .WithName("StartResearchExport").WithSummary("Prepare this account's research as a ZIP of readable (unencrypted) JSON; one at a time.");
        group.MapGet("/export/{id}", async Task<Results<FileContentHttpResult, NotFound>> (
            string id, ResearchArchive archive, IResearchStore store, CancellationToken ct) =>
            archive.Download(id, (await store.ReadAsync(ct)).Credential?.Account.Id) is { } file
                ? TypedResults.File(file.Zip, "application/zip", file.FileName) : TypedResults.NotFound())
            .WithName("DownloadResearchExport").WithSummary("Download a prepared export (available for five minutes).");
        group.MapPost("/import", async Task<Ok<ImportResponse>> (HttpContext context, ResearchArchive archive, CancellationToken ct) =>
        {
            if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = ResearchArchive.MaxUploadBytes;
            return TypedResults.Ok(await archive.ImportAsync(context.Request.Body, ct));
        }).WithName("ImportResearch").WithSummary("Upload a Meld Research export ZIP; it is merged into this account's research and synced.");
        group.MapPost("/leave", async Task<Ok<LeaveResponse>> (LeaveRequest request, LeaveService leave, CancellationToken ct) =>
            TypedResults.Ok(await leave.LeaveAsync(request, ct)))
            .WithName("LeaveMeldResearch").WithSummary("Delete the Meld account, its encrypted synced data and this computer's research, and uninstall the app.");
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
