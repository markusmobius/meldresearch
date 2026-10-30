using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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
        var origin = app.Services.GetRequiredService<RemoteOptions>().PublicOrigin.GetLeftPart(UriPartial.Authority);
        var installers = new Dictionary<string, (string FileName, byte[] Script)>();
        foreach (var (platform, fileName) in new[] { ("windows", "meldresearch-install.ps1"), ("macos", "meldresearch-install.sh") })
            installers[platform] = (fileName, System.Text.Encoding.UTF8.GetBytes(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Install", fileName)).Replace("\r\n", "\n").Replace("{{REMOTEURL}}", origin)));

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

        app.MapGet("/", async Task<ContentHttpResult> (HttpContext context, IAntiforgery antiforgery, IAccountService accounts,
            RemoteOptions options, CancellationToken ct) =>
        {
            string message = context.Request.Query.ContainsKey("signInFailed")
                ? "<p class=\"notice\">Google sign-in failed. Try again.</p>" : "";
            var account = context.User.Identity?.IsAuthenticated == true ? await accounts.FindAsync(context.User, ct) : null;
            string auth = account is not null
                ? $"<p>Signed in as {E(account.Name)}.</p><p><a class=\"button\" href=\"/account\">Your account and downloads</a></p>" +
                    SignOutForm(context, antiforgery)
                : context.User.Identity?.IsAuthenticated == true
                ? "<p>This Google identity has no Meld Research account.</p><p><a class=\"button\" href=\"/signup\">Sign up with Google</a></p>" +
                    SignOutForm(context, antiforgery)
                : options.Dummy
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

        app.MapGet("/login", (HttpContext context, IAntiforgery antiforgery, RemoteOptions options, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            string? code = OptionalCode(context);
            return Page("Sign in to Meld Research", "<p>Sign in with the Google account you used to create your Meld account. " +
                "Signing in does not create an account.</p>" + SignInControls(context, antiforgery, options, signup: false, code) +
                $"<p>No account yet? <a href=\"/signup{(code is null ? "" : "?code=" + code)}\">Sign up with Google</a></p>", options);
        }).ExcludeFromDescription();
        app.MapGet("/signup", (HttpContext context, IAntiforgery antiforgery, RemoteOptions options, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            string? code = OptionalCode(context);
            return Page("Create your Meld Research account", "<p>Your Google account identifies you; Meld stores its identifier, " +
                "name and verified email address. Google does not share your password with Meld.</p>" +
                SignInControls(context, antiforgery, options, signup: true, code), options);
        }).ExcludeFromDescription();
        // The Google button's callback submits this same-origin form, so antiforgery and Origin checks still apply.
        app.MapPost("/auth/google", async Task<RedirectHttpResult> (HttpContext context, IAntiforgery antiforgery,
            IAccountService accounts, IGoogleIdTokenVerifier google, RemoteOptions options, CancellationToken ct) =>
        {
            await ValidateFormAsync(context, antiforgery);
            var form = await context.Request.ReadFormAsync(ct);
            string? code = form["code"].ToString() is { Length: > 0 } value ? Code(value) : null;
            bool signup = form["mode"].ToString() == "signup";
            string nonce = context.Request.Cookies[GoogleNonceCookie] ?? "";
            context.Response.Cookies.Delete(GoogleNonceCookie, new CookieOptions { Path = "/auth/google" });
            GoogleIdentity identity;
            try
            {
                if (options.Dummy) throw new ResearchFailure(503, "google_not_configured", "Dummy mode uses the synthetic account.");
                identity = await google.VerifyAsync(form["credential"].ToString(), nonce, ct);
            }
            catch (ResearchFailure)
            {
                return TypedResults.Redirect("/?signInFailed=1");
            }
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new(ClaimTypes.NameIdentifier, identity.Subject), new(ClaimTypes.Name, identity.Name),
                new(ClaimTypes.Email, identity.Email), new("meld_provider", "google"), new("meld_email_verified", "true")
            ], CookieAuthenticationDefaults.AuthenticationScheme));
            if (signup) await accounts.RegisterAsync(principal, ct);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            return TypedResults.Redirect(code is null ? "/account" : "/connect?code=" + code);
        }).ExcludeFromDescription();

        app.MapPost("/auth/signout", async Task<RedirectHttpResult> (HttpContext context, IAntiforgery antiforgery) =>
        {
            await ValidateFormAsync(context, antiforgery);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return TypedResults.Redirect("/");
        }).ExcludeFromDescription();

        app.MapGet("/account", async Task<ContentHttpResult> (
            HttpContext context, IAccountService accounts, IAntiforgery antiforgery, RemoteOptions options, CancellationToken ct) =>
        {
            var account = await accounts.FindAsync(context.User, ct);
            return account is null
                ? Page("An existing Meld account is required", "<p>Signing in does not automatically create an account.</p><p><a href=\"/signup\">Create a Meld account with Google</a></p>", options)
                : Page("Your Meld Research account", $"<p>Signed in as {E(account.Name)}.</p>" + InstallPanel(origin) +
                    "<p>After installation, open <strong>Account</strong> in the local app and link it to this account.</p>" +
                    SignOutForm(context, antiforgery), options);
        }).ExcludeFromDescription();

        app.MapGet("/connect", async Task<ContentHttpResult> (
            HttpContext context, IAccountService accounts, IAntiforgery antiforgery, RemoteOptions options, CancellationToken ct) =>
        {
            string code = Code(context.Request.Query["code"].ToString());
            string name = await accounts.DeviceNameAsync(code, ct);
            if (context.User.Identity?.IsAuthenticated != true)
                return Page("Link your Meld installation", "<p>Sign in with the same Google account used for your existing Meld account.</p>" +
                    SignInControls(context, antiforgery, options, signup: false, code), options);
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

        // Public by design: installed clients verify the offline signature, length and SHA-256 before using a package.
        app.MapGet("/api/updates/{runtime}", async Task<ContentHttpResult> (string runtime, RemoteOptions options, CancellationToken ct) =>
        {
            var (_, envelope) = await PublishedUpdateAsync(runtime, options, ct);
            return TypedResults.Content(envelope, "application/json");
        }).WithName("GetDesktopUpdate").WithSummary("Read the signed manifest of the published desktop release for a runtime.");
        app.MapGet("/api/updates/{runtime}/package/{version}", async Task<PhysicalFileHttpResult> (
            string runtime, string version, RemoteOptions options, CancellationToken ct) =>
        {
            var (manifest, _) = await PublishedUpdateAsync(runtime, options, ct);
            if (version != manifest.Version)
                throw new ResearchFailure(404, "update_superseded", "That desktop release is no longer published. Check for updates again.");
            string path = Path.Combine(options.DownloadDirectory!, DesktopUpdates.PackageFileName(runtime));
            if (!File.Exists(path))
                throw new ResearchFailure(404, "update_not_published", "No desktop update is published for this platform.");
            return TypedResults.PhysicalFile(path, "application/zip", DesktopUpdates.PackageFileName(runtime));
        }).WithName("DownloadDesktopUpdate").WithSummary("Download the published desktop package named by the signed manifest.");
        // Public by design: "irm ... | iex" and "curl ... | bash" run without a browser session. The scripts install the
        // public signed release above and check its length and SHA-256 against the manifest.
        app.MapGet("/api/install/{platform}", (string platform) =>
            installers.TryGetValue(platform, out var installer)
                ? TypedResults.File(installer.Script, "text/plain; charset=utf-8", installer.FileName)
                : throw new ResearchFailure(404, "installer_not_found", "Use windows or macos."))
            .WithName("GetDesktopInstaller").WithSummary("Download the desktop install script for Windows or macOS, bound to this server.");
    }

    private static async Task<(DesktopUpdateManifest Manifest, string Envelope)> PublishedUpdateAsync(
        string runtime, RemoteOptions options, CancellationToken ct)
    {
        if (!DesktopUpdates.IsRuntime(runtime) || options.DownloadDirectory is not { } directory ||
            new FileInfo(Path.Combine(directory, DesktopUpdates.ManifestFileName(runtime))) is not { Exists: true } file)
            throw new ResearchFailure(404, "update_not_published", "No desktop update is published for this platform.");
        try
        {
            if (file.Length > DesktopUpdates.MaxEnvelopeBytes) throw new InvalidDataException("The published update manifest is too large.");
            byte[] bytes = await File.ReadAllBytesAsync(file.FullName, ct);
            var manifest = DesktopUpdates.ReadUnverified(DesktopUpdates.ReadEnvelope(bytes));
            if (manifest.Runtime != runtime) throw new InvalidDataException("The published update manifest names another runtime.");
            return (manifest, System.Text.Encoding.UTF8.GetString(bytes));
        }
        catch (InvalidDataException)
        {
            throw new ResearchFailure(503, "update_manifest_invalid", "The published desktop update is unavailable.");
        }
    }

    private const string GoogleNonceCookie = "MeldResearch.Remote.GoogleNonce";
    private const string GoogleContentSecurityPolicy = "default-src 'self'; script-src 'self' https://accounts.google.com/gsi/client; " +
        "style-src 'self' 'unsafe-inline' https://accounts.google.com/gsi/style; frame-src https://accounts.google.com/gsi/; " +
        "connect-src 'self' https://accounts.google.com/gsi/; font-src 'self'; img-src 'self'; object-src 'none'; base-uri 'none'; " +
        "frame-ancestors 'none'; form-action 'self'";

    /// <summary>
    /// Renders Sign in with Google for this page only: Google's script, a per-page nonce cookie, and a hidden
    /// same-origin form that our callback fills with the ID token. Other pages keep the strict default policy.
    /// </summary>
    private static string SignInControls(HttpContext context, IAntiforgery antiforgery, RemoteOptions options, bool signup, string? code)
    {
        string codeInput = code is null ? "" : $"<input type=\"hidden\" name=\"code\" value=\"{code}\">";
        if (options.Dummy)
            return $"<form method=\"post\" action=\"/auth/dummy\">{Token(context, antiforgery)}{codeInput}<button>Use the synthetic test account</button></form>";
        if (!options.GoogleConfigured)
            return "<p class=\"notice\">Google sign-in is not configured on this server yet.</p>";
        string nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        context.Response.Cookies.Append(GoogleNonceCookie, nonce, new CookieOptions
        {
            HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = context.Request.IsHttps, Path = "/auth/google",
            MaxAge = TimeSpan.FromMinutes(15), IsEssential = true
        });
        context.Response.Headers.ContentSecurityPolicy = GoogleContentSecurityPolicy;
        // Google's button validates the page origin from the Referer; same-origin would send none to accounts.google.com.
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        return $"<form id=\"google-sign-in\" method=\"post\" action=\"/auth/google\">{Token(context, antiforgery)}{codeInput}" +
            $"<input type=\"hidden\" name=\"mode\" value=\"{(signup ? "signup" : "login")}\"><input type=\"hidden\" name=\"credential\" value=\"\"></form>" +
            $"<div id=\"g_id_onload\" data-client_id=\"{E(options.GoogleClientId!)}\" data-callback=\"meldGoogleCredential\" " +
            $"data-nonce=\"{nonce}\" data-auto_prompt=\"false\" data-context=\"{(signup ? "signup" : "signin")}\" data-itp_support=\"true\"></div>" +
            $"<div class=\"g_id_signin\" data-type=\"standard\" data-theme=\"outline\" data-size=\"large\" data-shape=\"pill\" " +
            $"data-text=\"{(signup ? "signup_with" : "signin_with")}\" data-logo_alignment=\"left\"></div>" +
            "<noscript><p class=\"notice\">Sign in with Google needs JavaScript from accounts.google.com.</p></noscript>" +
            "<script src=\"https://accounts.google.com/gsi/client\" async></script><script src=\"/google-sign-in.js\"></script>";
    }

    private static string SignOutForm(HttpContext context, IAntiforgery antiforgery) =>
        $"<form method=\"post\" action=\"/auth/signout\">{Token(context, antiforgery)}<button>Sign out</button></form>";

    /// <summary>The Angular install card replaces this content; without JavaScript the commands remain usable.</summary>
    private static string InstallPanel(string origin) =>
        "<app-meld-install><h2>Install Meld Research</h2>" +
        "<p><strong>Windows:</strong> open PowerShell and run</p>" +
        $"<pre><code>irm -useb {E(origin)}/api/install/windows | iex</code></pre>" +
        "<p><strong>macOS:</strong> open Terminal and run</p>" +
        $"<pre><code>curl -fsSL {E(origin)}/api/install/macos | bash</code></pre>" +
        "<p>Or download the <a href=\"/api/install/windows\">Windows script</a> or the <a href=\"/api/install/macos\">macOS script</a>.</p>" +
        "</app-meld-install>";

    private static string? OptionalCode(HttpContext context) =>
        context.Request.Query["code"].ToString() is { Length: > 0 } code ? Code(code) : null;
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
