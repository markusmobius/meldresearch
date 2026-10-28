using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.Extensions.FileProviders;
using MeldResearch.BrowserProtocol;
using MeldResearch.LocalBackend.Endpoints;
using MeldResearch.LocalBackend.Middleware;
using MeldResearch.LocalBackend.Services;
using MeldResearch.LocalBackend.Services.Research;
using MeldResearch.LocalBackend.Services.Updates;

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables("MELDRESEARCH_").AddCommandLine(args);
var researchOptions = ResearchOptions.Load(builder.Configuration, builder.Environment);
int port = builder.Configuration.GetValue("Port", builder.Environment.IsDevelopment() ? 6010 : 6001);
if (port is < 1024 or > 65535 or 6000)
    throw new InvalidOperationException("Select a non-restricted loopback port (6010 for development or 6001 for the installed application).");

RuntimeFiles.CreatePrivateDirectory(RuntimeFiles.Root);
using var instanceLock = new FileStream(Path.Combine(RuntimeFiles.Root, "backend.lock"),
    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
string localUrl = $"http://127.0.0.1:{port}";

builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-XSRF-TOKEN";
    options.Cookie.Name = "MeldResearch.Antiforgery";
    options.Cookie.SameSite = SameSiteMode.Strict;
});
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNameCaseInsensitive = false;
    options.SerializerOptions.AllowDuplicateProperties = false;
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase, false));
});
if (researchOptions.Dummy) builder.Services.AddSingleton<IBrowserRelay, DummyBrowserRelay>();
else builder.Services.AddSingleton<IBrowserRelay, BrowserRelay>();
builder.Services.AddSingleton<IExtensionSetupService, ExtensionSetupService>();
builder.Services.AddSingleton<IArchiveStore, ArchiveStore>();
builder.Services.AddSingleton<IOverleafService, OverleafService>();
builder.Services.AddSingleton(researchOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ILocalVault, LocalVault>();
builder.Services.AddSingleton<IResearchStore, ResearchStore>();
builder.Services.AddSingleton<IResearchRemote, ResearchRemote>();
builder.Services.AddSingleton<IOverleafEvidenceSource, OverleafEvidenceSource>();
builder.Services.AddHttpClient("research-remote", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddSingleton<ResearchCoordinator>();
builder.Services.AddSingleton<IResearchCoordinator>(services => services.GetRequiredService<ResearchCoordinator>());
builder.Services.AddHostedService(services => services.GetRequiredService<ResearchCoordinator>());
builder.Services.AddSingleton(UpdateOptions.Load(builder.Configuration, researchOptions));
builder.Services.AddSingleton<INativeHostRegistrar, NativeHostRegistrar>();
builder.Services.AddHttpClient(DesktopUpdater.HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddSingleton<DesktopUpdater>();
builder.Services.AddHostedService(services => services.GetRequiredService<DesktopUpdater>());

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (context, next) =>
{
    if (context.Request.Host.Host is not ("127.0.0.1" or "localhost") || context.Request.Host.Port != port)
        throw new ApiFailure(400, "invalid_local_host", "This application accepts only its loopback address.");

    context.Response.Headers.CacheControl = "no-cache, no-store";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";

    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path == "/native")
    {
        string origin = context.Request.Headers.Origin.ToString();
        if ((origin.Length != 0 && origin != $"http://{context.Request.Host}") ||
            context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
            throw new ApiFailure(403, "cross_origin_request", "Cross-origin access to this local application is not allowed.");

        if (context.Request.Path.StartsWithSegments("/api") &&
            !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            try
            {
                await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                throw new ApiFailure(403, "invalid_local_request", "Reload the local application before trying again.");
            }
        }
    }
    await next(context);
});

app.UseWebSockets();
app.MapGet("/native", async (HttpContext context, IBrowserRelay browser, CancellationToken cancellationToken) =>
{
    if (researchOptions.Dummy)
        throw new ApiFailure(403, "dummy_browser_disabled", "Dummy mode accepts only synthetic Overleaf evidence.");
    string nonce = context.Request.Headers[NativeHandshake.NonceHeader].ToString();
    string authorization = context.Request.Headers.Authorization.ToString();
    string? proof = authorization.StartsWith(NativeHandshake.AuthorizationPrefix, StringComparison.Ordinal)
        ? authorization[NativeHandshake.AuthorizationPrefix.Length..] : null;
    if (!NativeHandshake.VerifyClientProof(token, nonce, proof))
        throw new ApiFailure(401, "native_auth_required", "A registered native bridge is required.");
    if (!context.WebSockets.IsWebSocketRequest)
        throw new ApiFailure(400, "websocket_required", "The native bridge requires a WebSocket connection.");
    context.Response.Headers[NativeHandshake.ServerProofHeader] = NativeHandshake.CreateServerProof(token, nonce);
    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, app.Lifetime.ApplicationStopping);
    await browser.ConnectAsync(socket, localUrl, lifetime.Token);
}).ExcludeFromDescription();

app.MapOverleaf();
app.MapResearch();
if (app.Environment.IsDevelopment())
    app.MapOpenApi();

string frontend = Path.GetFullPath(builder.Configuration["FrontendPath"] ??
    Path.Combine(builder.Environment.ContentRootPath, "..", "frontend", "dist", "frontend", "browser"));
if (!File.Exists(Path.Combine(frontend, "index.html")))
    throw new InvalidOperationException("Build the Angular application first, or supply --FrontendPath with its build directory.");
using var frontendFiles = new PhysicalFileProvider(frontend);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = frontendFiles });
app.UseStaticFiles(new StaticFileOptions { FileProvider = frontendFiles });

await app.StartAsync();
try
{
    if (!researchOptions.Dummy)
        await RuntimeFiles.WriteJsonAsync(RuntimeFiles.ConnectionPath,
            new BridgeConnection(WireProtocol.Version, port, token), app.Lifetime.ApplicationStopping);
    app.Logger.LogInformation("Meld Research listening at {LocalUrl}. Dummy evidence: {Dummy}. Model processing requires account linking and explicit project consent.", localUrl, researchOptions.Dummy);
    await app.WaitForShutdownAsync();
    // The stable launcher restarts into the new version when it sees this exit code.
    if (app.Services.GetRequiredService<DesktopUpdater>().RestartRequested) Environment.ExitCode = DesktopUpdates.UpdateExitCode;
}
finally
{
    if (!researchOptions.Dummy) File.Delete(RuntimeFiles.ConnectionPath);
    await app.DisposeAsync();
}
