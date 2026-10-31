using System.Security.Claims;

namespace MeldResearch.RemoteBackend.Services;

internal sealed class RemoteOptions
{
    public required string DataDirectory { get; init; }
    public required Uri PublicOrigin { get; init; }
    public required bool Dummy { get; init; }
    /// <summary>The public OAuth client ID for Sign in with Google; no client secret is used.</summary>
    public string? GoogleClientId { get; init; }
    public bool GoogleConfigured => !string.IsNullOrWhiteSpace(GoogleClientId);
    public string? DownloadDirectory { get; init; }
    /// <summary>Verified Google emails that may see operator pages such as LLM usage (config Superusers, comma-separated).</summary>
    public IReadOnlySet<string> Superusers { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A session from Sign in with Google with a verified email on the superuser list. In Dummy mode (development loopback
    /// only) the synthetic account stands in, so the operator pages can be tested.
    /// </summary>
    public bool IsSuperuser(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return false;
        string? provider = user.FindFirstValue("meld_provider");
        if (Dummy) return provider == "dummy";
        return provider == "google" && user.FindFirstValue("meld_email_verified") == "true" &&
            user.FindFirstValue(ClaimTypes.Email) is { Length: > 0 } email && Superusers.Contains(email);
    }

    public const string DefaultSuperuser = "markusmobius@gmail.com";

    public static RemoteOptions Load(IConfiguration configuration, IHostEnvironment environment)
    {
        bool dummy = configuration.GetValue<bool>("Dummy");
        string origin = configuration["PublicOrigin"] ??
            (environment.IsDevelopment() ? "http://127.0.0.1:6050" : "https://www.meldresearch.org");
        if (!Uri.TryCreate(origin.TrimEnd('/') + "/", UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/" ||
            (uri.Scheme != "https" && !(environment.IsDevelopment() && uri.Scheme == "http" && uri.IsLoopback)) ||
            (dummy && (!environment.IsDevelopment() || !uri.IsLoopback)))
            throw new InvalidOperationException("The remote origin must use HTTPS. Dummy mode is development-loopback only.");
        string data = Path.GetFullPath(configuration["DataDirectory"] ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeldResearch", "Remote"));
        return new()
        {
            DataDirectory = data, PublicOrigin = uri, Dummy = dummy,
            GoogleClientId = configuration["Google:ClientId"] is { Length: > 0 } clientId ? clientId.Trim() : null,
            DownloadDirectory = configuration["DownloadDirectory"] is { Length: > 0 } downloads ? Path.GetFullPath(downloads) : null,
            Superusers = (configuration["Superusers"] ?? DefaultSuperuser).Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
        };
    }
}
