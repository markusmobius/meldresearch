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

internal sealed record BrowserSession(string Name, string Email, string SignOutToken);

internal static class RemoteEndpoints
{
    public static void MapRemoteEndpoints(this WebApplication app)
    {
        string assetsPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "angular-assets.html");
        if (!File.Exists(assetsPath))
            throw new InvalidOperationException("Build the Angular frontend before building the remote backend; shared Material assets are missing.");
        string assets = File.ReadAllText(assetsPath);
        ContentHttpResult Page(HttpContext context, string title, string body, RemoteOptions options, bool heading = true) =>
            RenderPage(context, title, body, options, assets, heading);
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

        app.MapPost("/api/sign-in/redeem", async Task<Ok<RedeemSignInResponse>> (
            RedeemSignInRequest request, IAccountService accounts, CancellationToken ct) =>
            TypedResults.Ok(await accounts.RedeemSignInAsync(request, ct)))
            .WithName("RedeemSignIn").WithSummary("Exchange a one-time browser sign-in code and its PKCE verifier for an installation credential.");
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
        app.MapPost("/api/researcher/candidates", async Task<Ok<ResearcherSearchResponse>> (
            ResearcherSearchRequest request, HttpContext context, IAccountService accounts, IResearcherResolver resolver, CancellationToken ct) =>
        {
            await accounts.AuthenticateAsync(context.Request.Headers.Authorization.ToString(), ct);
            return TypedResults.Ok(new ResearcherSearchResponse(await resolver.ResolveAsync(request.Name ?? "", ct)));
        }).WithName("FindResearcherCandidates").WithSummary("Offer up to five OpenAlex authors for a name; nothing is stored.");
        app.MapPost("/api/researcher/works", async Task<Ok<ResearcherWorksResponse>> (
            ResearcherWorksRequest request, HttpContext context, IAccountService accounts, IOpenAlexClient openAlex, CancellationToken ct) =>
        {
            await accounts.AuthenticateAsync(context.Request.Headers.Authorization.ToString(), ct);
            return TypedResults.Ok(new ResearcherWorksResponse(await openAlex.ReadPublicationsAsync(request.AuthorId ?? "", ct)));
        }).WithName("ReadResearcherWorks").WithSummary("Read a confirmed OpenAlex author's public works; nothing is stored.");

        // Home is a single centered action: sign up / sign in with Google, or install once signed up.
        app.MapGet("/", async Task<ContentHttpResult> (HttpContext context, IAntiforgery antiforgery, IAccountService accounts,
            RemoteOptions options, CancellationToken ct) =>
        {
            string message = context.Request.Query.ContainsKey("signInFailed")
                ? "<p class=\"notice\">Google sign-in failed. Try again.</p>" : "";
            bool signedIn = context.User.Identity?.IsAuthenticated == true;
            var account = signedIn ? await accounts.FindAsync(context.User, ct) : null;
            string action = account is not null
                ? "<a class=\"install-button\" href=\"/account\"><span class=\"material-icons\" aria-hidden=\"true\">download</span>Install Meld Research</a>"
                // A Google identity without a Meld account can only sign up; otherwise the browser chooses (see google-sign-in.js).
                : SignInControls(context, antiforgery, options, signup: signedIn ? true : null, link: null);
            return Page(context, "Meld Research", message + $"<div class=\"home-action\">{action}</div>", options, heading: false);
        }).ExcludeFromDescription();

        app.MapGet("/login", (HttpContext context, IAntiforgery antiforgery, RemoteOptions options, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Page(context, "Sign in to Meld Research", "<p>Sign in with the Google account you used to create your Meld account. " +
                "Signing in does not create an account.</p>" + SignInControls(context, antiforgery, options, signup: false, link: null) +
                "<p>No account yet? <a href=\"/signup\">Sign up with Google</a></p>", options);
        }).ExcludeFromDescription();
        app.MapGet("/signup", (HttpContext context, IAntiforgery antiforgery, RemoteOptions options, CancellationToken ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return Page(context, "Create your Meld Research account", "<p>Your Google account identifies you; Meld stores its identifier, " +
                "name and verified email address. Google does not share your password with Meld.</p>" +
                SignInControls(context, antiforgery, options, signup: true, link: null), options);
        }).ExcludeFromDescription();
        // The Google button's callback submits this same-origin form, so antiforgery and Origin checks still apply.
        app.MapPost("/auth/google", async Task<RedirectHttpResult> (HttpContext context, IAntiforgery antiforgery,
            IAccountService accounts, IGoogleIdTokenVerifier google, RemoteOptions options, CancellationToken ct) =>
        {
            await ValidateFormAsync(context, antiforgery);
            var form = await context.Request.ReadFormAsync(ct);
            var link = LocalSignIn.From(key => form[key]);
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
                return TypedResults.Redirect(link is null ? "/?signInFailed=1" : link.ConnectUrl + "&signInFailed=1");
            }
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new(ClaimTypes.NameIdentifier, identity.Subject), new(ClaimTypes.Name, identity.Name),
                new(ClaimTypes.Email, identity.Email), new("meld_provider", "google"), new("meld_email_verified", "true")
            ], CookieAuthenticationDefaults.AuthenticationScheme));
            if (signup) await accounts.RegisterAsync(principal, ct);
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            return TypedResults.Redirect(link?.ConnectUrl ?? "/account");
        }).ExcludeFromDescription();

        app.MapPost("/auth/signout", async Task<RedirectHttpResult> (HttpContext context, IAntiforgery antiforgery) =>
        {
            await ValidateFormAsync(context, antiforgery);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return TypedResults.Redirect("/");
        }).ExcludeFromDescription();
        // The toolbar's account menu on static pages (Docs, Privacy, Team). Same-origin only: the global Origin check and
        // the browser's same-origin policy keep the sign-out token from other sites.
        app.MapGet("/api/session", Results<Ok<BrowserSession>, NoContent> (HttpContext context, IAntiforgery antiforgery) =>
            context.User.Identity?.IsAuthenticated == true
                ? TypedResults.Ok(new BrowserSession(context.User.FindFirstValue(ClaimTypes.Name) ?? "",
                    context.User.FindFirstValue(ClaimTypes.Email) ?? "", antiforgery.GetAndStoreTokens(context).RequestToken!))
                : TypedResults.NoContent()).ExcludeFromDescription();

        app.MapGet("/account", async Task<ContentHttpResult> (
            HttpContext context, IAccountService accounts, RemoteOptions options, CancellationToken ct) =>
        {
            var account = await accounts.FindAsync(context.User, ct);
            return account is null
                ? Page(context, "An existing Meld account is required", "<p>Signing in does not automatically create an account.</p><p><a href=\"/signup\">Create a Meld account with Google</a></p>", options)
                : Page(context, "Install Meld Research", InstallPanel(origin) +
                    "<p class=\"after-install\">After installation, choose <strong>Sign in with Google</strong> in the local app.</p>",
                    options, heading: false);
        }).ExcludeFromDescription();

        // Sign-in for the local app: an account holder who is signed in here goes straight back to the app's loopback
        // callback with a one-time code. Without a session, Google's button signs in first; without an account, one click
        // creates it. Nothing is shown to approve because the code can only reach the app that started the sign-in.
        app.MapGet("/connect", async Task<Results<RedirectHttpResult, ContentHttpResult>> (
            HttpContext context, IAccountService accounts, IAntiforgery antiforgery, RemoteOptions options, CancellationToken ct) =>
        {
            var link = LocalSignIn.From(key => context.Request.Query[key]) ??
                throw new ResearchFailure(400, "invalid_sign_in_request", "Start signing in from Account in the Meld Research app.");
            string failed = context.Request.Query.ContainsKey("signInFailed") ? "<p class=\"notice\">Google sign-in failed. Try again.</p>" : "";
            if (context.User.Identity?.IsAuthenticated != true)
            {
                var signIn = Page(context, "Sign in to Meld Research", failed + "<div class=\"home-action\">" +
                    SignInControls(context, antiforgery, options, signup: null, link) + "</div>", options, heading: false);
                AllowLoopbackRedirect(context);
                return signIn;
            }
            if (await accounts.FindAsync(context.User, ct) is null)
            {
                var create = Page(context, "Create your Meld Research account",
                    $"<p>{E(context.User.FindFirstValue(ClaimTypes.Email) ?? "This Google account")} does not have a Meld Research account yet. " +
                    "Meld stores your Google account identifier, name and verified email address.</p>" +
                    $"<form method=\"post\" action=\"/connect/signup\">{Token(context, antiforgery)}{link.FormInputs(E)}" +
                    "<button>Create account and continue</button></form>", options);
                AllowLoopbackRedirect(context);
                return create;
            }
            return TypedResults.Redirect(link.Callback(await accounts.IssueSignInCodeAsync(context.User, link, ct)));
        }).ExcludeFromDescription();
        app.MapPost("/connect/signup", async Task<RedirectHttpResult> (
            HttpContext context, IAccountService accounts, IAntiforgery antiforgery, CancellationToken ct) =>
        {
            await ValidateFormAsync(context, antiforgery);
            var form = await context.Request.ReadFormAsync(ct);
            var link = LocalSignIn.From(key => form[key]) ??
                throw new ResearchFailure(400, "invalid_sign_in_request", "Start signing in from Account in the Meld Research app.");
            await accounts.RegisterAsync(context.User, ct);
            return TypedResults.Redirect(link.ConnectUrl);
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
                return TypedResults.Redirect(LocalSignIn.From(key => form[key])?.ConnectUrl ?? "/account");
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
    private static string SignInControls(HttpContext context, IAntiforgery antiforgery, RemoteOptions options, bool? signup, LocalSignIn? link)
    {
        string codeInput = link?.FormInputs(E) ?? "";
        if (options.Dummy)
            return $"<form method=\"post\" action=\"/auth/dummy\">{Token(context, antiforgery)}{codeInput}" +
                "<button class=\"dummy-sign-in\"><span class=\"material-icons\" aria-hidden=\"true\">science</span>Use the synthetic test account</button></form>";
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
        bool up = signup ?? true;
        // signup == null: google-sign-in.js switches to "Sign in" when this browser has seen a Meld account before.
        return $"<form id=\"google-sign-in\" method=\"post\" action=\"/auth/google\">{Token(context, antiforgery)}{codeInput}" +
            $"<input type=\"hidden\" name=\"mode\" value=\"{(up ? "signup" : "login")}\"><input type=\"hidden\" name=\"credential\" value=\"\"></form>" +
            $"<div id=\"g_id_onload\" data-client_id=\"{E(options.GoogleClientId!)}\" data-callback=\"meldGoogleCredential\" " +
            $"data-nonce=\"{nonce}\" data-auto_prompt=\"false\" data-context=\"{(up ? "signup" : "signin")}\" data-itp_support=\"true\"></div>" +
            $"<div class=\"google-button\"><div class=\"g_id_signin\" data-type=\"standard\" data-theme=\"filled_blue\" data-size=\"large\" " +
            $"data-shape=\"pill\" data-width=\"300\" data-text=\"{(up ? "signup_with" : "signin_with")}\" data-logo_alignment=\"left\"" +
            $"{(signup is null ? " data-meld-auto-mode=\"true\"" : "")}></div></div>" +
            "<noscript><p class=\"notice\">Sign in with Google needs JavaScript from accounts.google.com.</p></noscript>" +
            // Ours first: it must adjust the button before Google's script reads it.
            "<script src=\"/google-sign-in.js\"></script><script src=\"https://accounts.google.com/gsi/client\" async></script>";
    }

    /// <summary>The Angular install card replaces this content; without JavaScript the commands remain usable.</summary>
    private static string InstallPanel(string origin) =>
        "<app-meld-install><h2>Install Meld Research</h2>" +
        "<p><strong>Windows:</strong> open PowerShell and run</p>" +
        $"<pre><code>irm -useb {E(origin)}/api/install/windows | iex</code></pre>" +
        "<p><strong>macOS:</strong> open Terminal and run</p>" +
        $"<pre><code>curl -fsSL {E(origin)}/api/install/macos | bash</code></pre>" +
        "<p>Or download the <a href=\"/api/install/windows\">Windows script</a> or the <a href=\"/api/install/macos\">macOS script</a>.</p>" +
        "</app-meld-install>";

    /// <summary>
    /// Forms on local-app sign-in pages end in a redirect to the app's loopback callback; Chrome applies the submitting
    /// page's form-action to that redirect, so these pages (only) also allow loopback HTTP targets.
    /// </summary>
    private static void AllowLoopbackRedirect(HttpContext context) =>
        context.Response.Headers.ContentSecurityPolicy = context.Response.Headers.ContentSecurityPolicy.ToString()
            .Replace("form-action 'self'", "form-action 'self' http://127.0.0.1:* http://localhost:*");
    private static string Token(HttpContext context, IAntiforgery antiforgery) =>
        $"<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"{E(antiforgery.GetAndStoreTokens(context).RequestToken!)}\">";
    private static async Task ValidateFormAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try { await antiforgery.ValidateRequestAsync(context); }
        catch (AntiforgeryValidationException) { throw new ResearchFailure(403, "invalid_browser_request", "Reload this page before trying again."); }
    }
    private static string E(string value) => HtmlEncoder.Default.Encode(value);
    private static string StoreUrl => "https://chromewebstore.google.com/detail/" + ExtensionIdentity.Id;
    /// <summary>
    /// Server-rendered page inside the shared Material toolbar. When signed in, the toolbar's account menu (name, email,
    /// Install, Sign out) reads the meld-account meta tag and submits the hidden sign-out form; the no-JavaScript header
    /// fallback submits the same form.
    /// </summary>
    private static ContentHttpResult RenderPage(HttpContext context, string title, string body, RemoteOptions options, string assets, bool heading)
    {
        string meta = "", signOutForm = "", fallbackAccount = "";
        if (context.User.Identity?.IsAuthenticated == true)
        {
            string name = context.User.FindFirstValue(ClaimTypes.Name) ?? "", email = context.User.FindFirstValue(ClaimTypes.Email) ?? "";
            meta = $"<meta name=\"meld-account\" content=\"{E(name)}\" data-email=\"{E(email)}\">";
            signOutForm = "<form id=\"meld-sign-out\" method=\"post\" action=\"/auth/signout\" hidden>" +
                Token(context, context.RequestServices.GetRequiredService<IAntiforgery>()) + "</form>";
            fallbackAccount = $"<span class=\"site-account\">{E(name)} <button form=\"meld-sign-out\">Sign out</button></span>";
        }
        return TypedResults.Content("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">" +
            $"<title>{E(title)} - Meld Research</title>{meta}<link rel=\"stylesheet\" href=\"/site.css\">{assets}</head><body class=\"information\">" +
            """
            <app-meld-toolbar><header class="site-header">
              <a class="site-brand" href="/"><span class="brand-mark" aria-hidden="true">m</span>
                <span class="brand-text">Meld Research<span class="brand-role">Remote Backend</span></span></a>
              <nav class="site-nav" aria-label="Main navigation"><a href="/">Home</a><a href="/docs.html">Docs</a>
                <a href="/privacy.html">Privacy</a><a href="/team.html">Project Team</a></nav>
            """ + fallbackAccount + "</header></app-meld-toolbar>" + signOutForm +
            "<main class=\"site-main\"><article class=\"site-card\">" +
            (options.Dummy ? "<p class=\"notice\">DUMMY DEVELOPMENT MODE: synthetic account and evidence; configured Foundry inference is real.</p>" : "") +
            $"<h1{(heading ? "" : " class=\"visually-hidden\"")}>{E(title)}</h1>{body}</article></main>" +
            """
            <footer class="meld-footer"><nav aria-label="Footer"><a href="mailto:markusmobius@gmail.com">Contact</a>
              <a href="/privacy.html">Privacy</a></nav></footer></body></html>
            """, "text/html; charset=utf-8");
    }
}
