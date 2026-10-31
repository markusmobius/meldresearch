using System.Net;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using MeldResearch.BrowserProtocol;
using MeldResearch.RemoteBackend.Data;
using MeldResearch.RemoteBackend.Endpoints;
using MeldResearch.RemoteBackend.Middleware;
using MeldResearch.RemoteBackend.Services;
using MeldResearch.Research;

bool smokeTest = FoundrySmokeTest.TryParse(args, out var smokeOptions, out args);
var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration["SettingsFile"] is { Length: > 0 } settings)
    builder.Configuration.AddJsonFile(Path.GetFullPath(settings), optional: false);
builder.Configuration.AddEnvironmentVariables("MELDRESEARCH_").AddCommandLine(args);
if (smokeTest)
{
    Environment.ExitCode = await FoundrySmokeTest.RunAsync(builder.Configuration, builder.Environment, smokeOptions!);
    return;
}
var options = RemoteOptions.Load(builder.Configuration, builder.Environment);
int port = builder.Configuration.GetValue("Port", builder.Environment.IsDevelopment() ? 6050 : 8096);
if (port is < 1024 or > 65535) throw new InvalidOperationException("Select an unprivileged remote backend port.");
var listenAddress = IPAddress.Parse(builder.Configuration["ListenAddress"] ?? "127.0.0.1");
if (options.Dummy && !IPAddress.IsLoopback(listenAddress))
    throw new InvalidOperationException("Dummy mode must listen only on loopback.");
RuntimeFiles.CreatePrivateDirectory(options.DataDirectory);
using var instanceLock = new FileStream(Path.Combine(options.DataDirectory, "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
builder.WebHost.ConfigureKestrel(server =>
{
    server.Listen(listenAddress, port);
    server.Limits.MaxRequestBodySize = ResearchEvidence.MaxRequestBytes;
});
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
{
    // X-Forwarded-For lets the per-client rate limiter see real clients behind the trusted proxy.
    forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    forwarded.KnownProxies.Add(IPAddress.Loopback);
    forwarded.KnownProxies.Add(IPAddress.IPv6Loopback);
    foreach (var proxy in builder.Configuration.GetSection("TrustedProxies").GetChildren())
        forwarded.KnownProxies.Add(IPAddress.Parse(proxy.Value ?? throw new InvalidOperationException("A trusted proxy address is missing.")));
});
builder.Services.AddDbContextFactory<RemoteDbContext>(database => database.UseSqlite(RemoteDatabase.ConnectionString(options.DataDirectory)));
var protection = builder.Services.AddDataProtection().SetApplicationName(options.Dummy ? "MeldResearch.Remote.Dummy" : "MeldResearch.Remote")
    .PersistKeysToDbContext<RemoteDbContext>();
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
else if (builder.Configuration["DataProtection:CertificatePath"] is { Length: > 0 } certificate)
    protection.ProtectKeysWithCertificate(X509CertificateLoader.LoadPkcs12FromFile(certificate, builder.Configuration["DataProtection:CertificatePassword"]));
else if (!builder.Environment.IsDevelopment())
    throw new InvalidOperationException("Production on this platform requires a certificate to protect persisted server keys.");

builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IAccountService, AccountService>();
builder.Services.AddSingleton<InferenceUsageStore>();
builder.Services.AddSingleton<InferenceGate>();
builder.Services.AddSingleton<IFoundryService, FoundryService>();
builder.Services.AddHttpClient("foundry", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
// OpenAlex: public author/work metadata for researcher matching. OpenAlex:ApiKey is optional but recommended.
builder.Services.AddSingleton<IOpenAlexClient, OpenAlexClient>();
builder.Services.AddSingleton<IResearcherResolver, ResearcherResolver>();
builder.Services.AddHttpClient("openalex", client => client.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(cookie =>
    {
        cookie.Cookie.Name = "MeldResearch.Remote.Session";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Lax;
        cookie.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
        cookie.LoginPath = "/login";
    });
// Sign in with Google: the browser receives a Google-signed ID token, which the server verifies against Google's
// published keys. Only the public client ID is configured; no client secret or server-side code exchange.
builder.Services.AddSingleton<IConfigurationManager<OpenIdConnectConfiguration>>(
    new ConfigurationManager<OpenIdConnectConfiguration>(GoogleIdTokenVerifier.MetadataAddress,
        new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever { RequireHttps = true }));
builder.Services.AddSingleton<IGoogleIdTokenVerifier, GoogleIdTokenVerifier>();
builder.Services.AddAntiforgery(antiforgery =>
{
    antiforgery.Cookie.Name = "MeldResearch.Remote.Antiforgery";
    antiforgery.Cookie.SameSite = SameSiteMode.Strict;
    antiforgery.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
});
builder.Services.ConfigureHttpJsonOptions(json =>
{
    json.SerializerOptions.PropertyNameCaseInsensitive = false;
    json.SerializerOptions.AllowDuplicateProperties = false;
    json.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    json.SerializerOptions.RespectRequiredConstructorParameters = true;
    json.SerializerOptions.MaxDepth = 32;
    json.SerializerOptions.Converters.Add(new JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase, false));
});
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<RemoteExceptionHandler>();
builder.Services.AddRateLimiter(limits =>
{
    limits.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    limits.GlobalLimiter = System.Threading.RateLimiting.PartitionedRateLimiter.Create<HttpContext, string>(context =>
        System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new()
            {
                PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
            }));
});

var app = builder.Build();
await RemoteDatabase.InitializeAsync(app.Services.GetRequiredService<IDbContextFactory<RemoteDbContext>>(), options.DataDirectory, CancellationToken.None);
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.Use(async (context, next) =>
{
    bool local = app.Environment.IsDevelopment() && context.Request.Host.Host is "localhost" or "127.0.0.1";
    bool health = context.Request.Path == "/healthz" && HttpMethods.IsGet(context.Request.Method);
    if (!health && ((!local && context.Request.Host.Host != options.PublicOrigin.Host) ||
        (!app.Environment.IsDevelopment() && !context.Request.IsHttps)))
        throw new ResearchFailure(400, "invalid_remote_origin", "Use the configured HTTPS Meld Research address.");
    string origin = context.Request.Headers.Origin.ToString();
    if (origin.Length != 0 && origin != $"{context.Request.Scheme}://{context.Request.Host}")
        throw new ResearchFailure(403, "cross_origin_request", "Cross-origin browser requests are not allowed.");
    context.Response.Headers.CacheControl = "no-cache, no-store";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    // no-referrer makes normal form navigations send Origin: null.
    context.Response.Headers["Referrer-Policy"] = "same-origin";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self'; img-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    await next(context);
});
string webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
using var files = new PhysicalFileProvider(webRoot);
// Loading the Material UI and its fonts must not exhaust the API request budget.
app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
app.UseRateLimiter();
app.UseAuthentication();
app.MapGet("/healthz", () => TypedResults.Ok(new { status = "ok" })).ExcludeFromDescription();
app.MapRemoteEndpoints();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
await app.RunAsync();
