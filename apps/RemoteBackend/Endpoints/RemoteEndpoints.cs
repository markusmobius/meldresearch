using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Http.HttpResults;
using MeldResearch.BrowserProtocol;
using MeldResearch.RemoteBackend.Services;
using MeldResearch.Research;

namespace MeldResearch.RemoteBackend.Endpoints;

internal static class RemoteEndpoints
{
    public static void MapRemoteEndpoints(this WebApplication app)
    {
        string assetsPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "angular-assets.html");
        if (!File.Exists(assetsPath))
            throw new InvalidOperationException("Build the Angular frontend before building the remote backend; shared Material assets are missing.");
        string assets = File.ReadAllText(assetsPath);
        ContentHttpResult Page(string title, string body, RemoteOptions options) => RenderPage(title, body, options, assets);

        app.MapGet("/api/status", (RemoteOptions options, IFoundryService foundry, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return TypedResults.Ok(new RemoteStatusResponse(options.Dummy, options.GoogleConfigured, foundry.Configured,
                new Uri(options.PublicOrigin, "privacy.html").AbsoluteUri, ExtensionIdentity.Id, StoreUrl));
        }).WithName("GetRemoteStatus").WithSummary("Read non-secret account and Foundry configuration status.");

        app.MapPost("/api/device-links", async Task<Created<DeviceLinkResponse>> (
            CreateDeviceLinkRequest request, IAccountService accounts, CancellationToken ct) =>
            TypedResults.Created("/api/device-links/poll", await accounts.BeginLinkAsync(request, ct)))
            .WithName("CreateDeviceLink").WithSummary("Request explicit browser approval to link a local installation.");
        app.MapPost("/api/device-links/poll", async Task<Ok<PollDeviceLinkResponse>> (
            PollDeviceLinkRequest request, IAccountService accounts, CancellationToken ct) =>
            TypedResults.Ok(await accounts.PollAsync(request, ct)))
            .WithName("PollDeviceLink").WithSummary("Redeem an approved device link using its private proof.");
        app.MapGet("/api/account", async Task<Ok<MeldAccountResponse>> (
            HttpContext context, IAccountService accounts, CancellationToken ct) =>
            TypedResults.Ok(await accounts.AuthenticateAsync(context.Request.Headers.Authorization.ToString(), ct)))
            .WithName("GetLinkedAccount").WithSummary("Validate an installation credential against an existing account.");
        app.MapDelete("/api/account/device", async Task<NoContent> (
            HttpContext context, IAccountService accounts, CancellationToken ct) =>
        {
            await accounts.RevokeAsync(context.Request.Headers.Authorization.ToString(), ct);
            return TypedResults.NoContent();
        }).WithName("RevokeLinkedDevice").WithSummary("Revoke the calling installation's credential.");
        app.MapPost("/api/research/analyze", async Task<Ok<AnalyzeResearchResponse>> (
            AnalyzeResearchRequest request, HttpContext context, IAccountService accounts, IFoundryService foundry, CancellationToken ct) =>
        {
            await accounts.AuthenticateAsync(context.Request.Headers.Authorization.ToString(), ct);
            return TypedResults.Ok(await foundry.AnalyzeAsync(request, ct));
        }).WithName("AnalyzeResearchDelta").WithSummary("Convert Overleaf evidence into grounded Updates using Foundry only.");
        app.MapPost("/api/research/embeddings", async Task<Ok<ResearchEmbeddingResponse>> (
            ResearchEmbeddingRequest request, HttpContext context, IAccountService accounts, IFoundryService foundry, CancellationToken ct) =>
        {
            await accounts.AuthenticateAsync(context.Request.Headers.Authorization.ToString(), ct);
            return TypedResults.Ok(await foundry.EmbedAsync(request, ct));
        }).WithName("EmbedResearch").WithSummary("Embed bounded research text using the configured Foundry deployment.");

        app.MapGet("/", (HttpContext context, IAntiforgery antiforgery, RemoteOptions options, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            string message = context.Request.Query.ContainsKey("signInFailed")
                ? "<p class=\"notice\">Google sign-in failed. Check the configured OAuth client and callback address, then try again.</p>" : "";
            string auth = options.Dummy
                ? $"<form method=\"post\" action=\"/auth/dummy\">{Token(context, antiforgery)}<button>Use the synthetic test account</button></form>"
                : options.GoogleConfigured
                    ? "<p><a class=\"button\" href=\"/signup\">Sign up with Google</a> <a href=\"/login\">Sign in</a></p>"
                    : "<p class=\"notice\">Google signup is not configured on this server yet. Configure the OAuth client before accepting real accounts.</p>";
            return Page("Meld Research", message + """
                <p class="page-subtitle">Accounts, installation approval, and Foundry processing for your local research assistant.</p>
                <div class="stats-grid">
                  <section class="stat-card"><h2>Private source</h2><p class="stat-value">Overleaf</p>
                    <p class="stat-desc">Read through your normal browser</p></section>
                  <section class="stat-card"><h2>Research feed</h2><p class="stat-value">Updates</p>
                    <p class="stat-desc">Grounded in your selected projects</p></section>
                  <section class="stat-card"><h2>Model provider</h2><p class="stat-value">Foundry</p>
                    <p class="stat-desc">Selected text, with your consent</p></section>
                </div>
                <p>Your project files and research index stay on your computer. With your permission, selected text is sent
                through this service to Azure AI Foundry to identify meaningful research updates.</p>
                """ + (options.Dummy ? "<p class=\"callout\">Both backends are running on this computer for development. This blue page is not a deployed public website.</p>" : "") +
                auth + "<p><a href=\"/docs.html\">Read the setup guide</a> &middot; <a href=\"/privacy.html\">Privacy and data use</a></p>", options);
        }).ExcludeFromDescription();

        app.MapGet("/login", (HttpContext context, RemoteOptions options, CancellationToken ct) =>
            GoogleChallenge(context, options, false, ct)).ExcludeFromDescription();
        app.MapGet("/signup", (HttpContext context, RemoteOptions options, CancellationToken ct) =>
            GoogleChallenge(context, options, true, ct)).ExcludeFromDescription();
        app.MapGet("/signup-complete", async Task<RedirectHttpResult> (
            HttpContext context, IAccountService accounts, CancellationToken ct) =>
        {
            await accounts.RegisterAsync(context.User, ct);
            return TypedResults.Redirect(ReturnPath(context));
        }).ExcludeFromDescription();

        app.MapGet("/account", async Task<ContentHttpResult> (
            HttpContext context, IAccountService accounts, RemoteOptions options, CancellationToken ct) =>
        {
            var account = await accounts.FindAsync(context.User, ct);
            return account is null
                ? Page("An existing Meld account is required", "<p>Signing in does not automatically create an account.</p><p><a href=\"/signup\">Create a Meld account with Google</a></p>", options)
                : Page("Your Meld Research account", $"<p>Signed in as {E(account.Name)}.</p><p>Install the desktop companion, then link it to this account.</p>" +
                    "<p><a class=\"button\" href=\"/downloads/win-x64\">Windows test package</a></p>" +
                    "<p><a href=\"/downloads/osx-arm64\">macOS Apple Silicon test package</a> &middot; <a href=\"/downloads/osx-x64\">macOS Intel test package</a></p>" +
                    "<p>Test packages are not yet signed/notarized production installers.</p>", options);
        }).ExcludeFromDescription();

        app.MapGet("/connect", async Task<ContentHttpResult> (
            HttpContext context, IAccountService accounts, IAntiforgery antiforgery, RemoteOptions options, CancellationToken ct) =>
        {
            string code = Code(context.Request.Query["code"].ToString());
            string name = await accounts.DeviceNameAsync(code, ct);
            if (context.User.Identity?.IsAuthenticated != true)
            {
                string login = options.Dummy
                    ? $"<form method=\"post\" action=\"/auth/dummy\">{Token(context, antiforgery)}<input type=\"hidden\" name=\"code\" value=\"{code}\"><button>Use the synthetic test account</button></form>"
                    : $"<p><a class=\"button\" href=\"/login?code={code}\">Continue with Google</a></p>";
                return Page("Link your Meld installation", "<p>Sign in to the same Google identity used for your existing Meld account.</p>" + login, options);
            }
            var account = await accounts.FindAsync(context.User, ct);
            if (account is null)
                return Page("Create a remote account first", $"<p>This Google identity has no Meld Research account.</p><p><a href=\"/signup?code={code}\">Sign up on this website</a></p>", options);
            return Page("Approve this installation",
                $"<p>Account: {E(account.Name)}. Device: {E(name)}.</p><p>Only approve a device-link request you just started in your local Meld application.</p>" +
                $"<form method=\"post\" action=\"/connect\">{Token(context, antiforgery)}<input type=\"hidden\" name=\"code\" value=\"{code}\">" +
                "<button name=\"decision\" value=\"allow\">Allow this installation</button> <button name=\"decision\" value=\"deny\">Deny</button></form>", options);
        }).ExcludeFromDescription();
        app.MapPost("/connect", async Task<ContentHttpResult> (
            HttpContext context, IAccountService accounts, IAntiforgery antiforgery, RemoteOptions options, CancellationToken ct) =>
        {
            await ValidateFormAsync(context, antiforgery);
            var form = await context.Request.ReadFormAsync(ct);
            string decision = form["decision"].ToString();
            if (decision is not ("allow" or "deny")) throw new ResearchFailure(400, "invalid_decision", "Choose Allow or Deny.");
            await accounts.ApproveAsync(Code(form["code"].ToString()), context.User, decision == "allow", ct);
            return Page(decision == "allow" ? "Installation linked" : "Installation denied",
                "<p>Return to the local Meld application. You can close this page.</p>", options);
        }).ExcludeFromDescription();

        if (app.Services.GetRequiredService<RemoteOptions>().Dummy)
        {
            app.MapGet("/api/development/inference-counts", (IFoundryService foundry) =>
                TypedResults.Ok(new { foundry.ChatRequests, foundry.EmbeddingRequests })).ExcludeFromDescription();
            app.MapPost("/auth/dummy", async Task<RedirectHttpResult> (
                HttpContext context, IAntiforgery antiforgery, IAccountService accounts, CancellationToken ct) =>
            {
                await ValidateFormAsync(context, antiforgery);
                var principal = new ClaimsPrincipal(new ClaimsIdentity([
                    new(ClaimTypes.NameIdentifier, "synthetic-researcher"), new(ClaimTypes.Name, "Demo Researcher"),
                    new(ClaimTypes.Email, "researcher@example.invalid"), new("meld_provider", "dummy"),
                    new("meld_email_verified", "true")
                ], CookieAuthenticationDefaults.AuthenticationScheme));
                await accounts.RegisterAsync(principal, ct);
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
                var form = await context.Request.ReadFormAsync(ct);
                string code = form["code"].ToString();
                return TypedResults.Redirect(code.Length == 0 ? "/account" : "/connect?code=" + Code(code));
            }).ExcludeFromDescription();
        }

        app.MapGet("/downloads/{runtime}", async Task<PhysicalFileHttpResult> (
            string runtime, HttpContext context, IAccountService accounts, RemoteOptions options, CancellationToken ct) =>
        {
            if (await accounts.FindAsync(context.User, ct) is null)
                throw new ResearchFailure(403, "meld_account_required", "Sign up for a Meld account before downloading the local application.");
            if (runtime is not ("win-x64" or "osx-arm64" or "osx-x64"))
                throw new ResearchFailure(404, "installer_not_found", "That platform package does not exist.");
            string filename = $"meldresearch-{runtime}.zip";
            string? path = options.DownloadDirectory is { } directory ? Path.Combine(directory, filename) : null;
            if (path is null || !File.Exists(path))
                throw new ResearchFailure(503, "installer_not_published", "The desktop package has not been made available by this server yet.");
            return TypedResults.PhysicalFile(path, "application/zip", filename);
        }).WithName("DownloadLocalApplication").WithSummary("Download an available desktop package after remote account signup.");
    }

    private static IResult GoogleChallenge(HttpContext context, RemoteOptions options, bool signup, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!options.GoogleConfigured || options.Dummy)
            throw new ResearchFailure(503, "google_not_configured", "Real Google sign-in is not configured on this server.");
        string code = context.Request.Query["code"].ToString();
        string destination = signup ? "/signup-complete" : "/account";
        if (code.Length != 0) destination = (signup ? "/signup-complete" : "/connect") + "?code=" + Code(code);
        return TypedResults.Challenge(new AuthenticationProperties { RedirectUri = destination }, [GoogleDefaults.AuthenticationScheme]);
    }

    private static string ReturnPath(HttpContext context) =>
        context.Request.Query["code"].ToString() is { Length: > 0 } code ? "/connect?code=" + Code(code) : "/account";
    private static string Code(string code) => code.Length == 12 && code.All(Uri.IsHexDigit) ? code.ToUpperInvariant() :
        throw new ResearchFailure(400, "invalid_device_code", "The browser verification code is invalid.");
    private static string Token(HttpContext context, IAntiforgery antiforgery) =>
        $"<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"{E(antiforgery.GetAndStoreTokens(context).RequestToken!)}\">";
    private static async Task ValidateFormAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try { await antiforgery.ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { throw new ResearchFailure(403, "invalid_browser_request", "Reload this page before trying again."); }
    }
    private static string E(string value) => HtmlEncoder.Default.Encode(value);
    private static string StoreUrl => "https://chromewebstore.google.com/detail/" + ExtensionIdentity.Id;
    private static ContentHttpResult RenderPage(string title, string body, RemoteOptions options, string assets) =>
        TypedResults.Content("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            $"<title>{E(title)} - Meld Research</title><link rel=\"stylesheet\" href=\"/site.css\">{assets}</head><body class=\"information\">" +
            """
            <app-meld-toolbar><header class="site-header">
              <a class="site-brand" href="/"><span class="brand-mark" aria-hidden="true">m</span>
                <span class="brand-text">Meld Research<span class="brand-role">Remote Backend</span></span></a>
              <nav class="site-nav" aria-label="Main navigation"><a href="/">Home</a><a href="/docs.html">Docs</a>
                <a href="/privacy.html">Privacy</a><a href="/team.html">Project Team</a></nav>
            </header></app-meld-toolbar><main class="site-main"><article class="site-card">
            """ +
            (options.Dummy ? "<p class=\"notice\">DUMMY DEVELOPMENT MODE: synthetic account and evidence; configured Foundry inference is real.</p>" : "") +
            $"<h1>{E(title)}</h1>{body}</article></main>" +
            """
            <footer class="meld-footer"><span>Meld Research &middot; Independent research software</span>
              <nav aria-label="Footer"><a href="/team.html">Project Team</a><a href="/privacy.html">Privacy</a>
                <a href="mailto:markusmobius@gmail.com">Contact</a></nav>
            </footer></body></html>
            """, "text/html; charset=utf-8");
}
