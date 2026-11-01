using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using MeldResearch.RemoteBackend.Data;
using MeldResearch.Research;

namespace MeldResearch.RemoteBackend.Services;

internal sealed record ProfilePicture(string ContentType, byte[] Image);

internal interface IProfilePictures
{
    /// <summary>Stores (or removes) the account's Google picture after a sign-in; failures leave the previous picture.</summary>
    Task UpdateAsync(Guid accountId, string? googleUrl, CancellationToken ct);
    Task<ProfilePicture?> FindAsync(Guid accountId, CancellationToken ct);
}

/// <summary>
/// Copies the Google profile picture named in the verified ID token to Meld's own database at sign-in, so the website and
/// the local app show it from Meld (same origin, no request to Google from any page). Only https images on Google's
/// googleusercontent.com hosts are fetched, without redirects, up to 512 KB, and only real JPEG, PNG, GIF or WebP data is
/// kept. The image is encrypted with Data Protection like names and email addresses.
/// </summary>
internal sealed class ProfilePictures(IDbContextFactory<RemoteDbContext> databases, IHttpClientFactory clients,
    IDataProtectionProvider protection, TimeProvider clock, ILogger<ProfilePictures> logger) : IProfilePictures
{
    public const string HttpClientName = "google-pictures";
    public const int MaxBytes = 512 * 1024;
    private readonly IDataProtector _protector = protection.CreateProtector("MeldResearch.Remote.Database.Pictures.v1");

    public static bool IsGooglePicture(string? url) =>
        url is { Length: > 0 and <= 2048 } && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        (uri.Host == "googleusercontent.com" || uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase));

    public async Task UpdateAsync(Guid accountId, string? googleUrl, CancellationToken ct)
    {
        try
        {
            await using var database = await databases.CreateDbContextAsync(ct);
            if (!IsGooglePicture(googleUrl))
            {
                await database.AccountPictures.Where(item => item.AccountId == accountId).ExecuteDeleteAsync(ct);
                return;
            }
            string source = ResearchEvidence.Hash(googleUrl!);
            if (await database.AccountPictures.AnyAsync(item => item.AccountId == accountId && item.SourceHash == source, ct)) return;
            var picture = await DownloadAsync(Sized(googleUrl!), ct);
            if (picture is null) return;
            var protectedImage = _protector.Protect(picture.Image);
            var now = clock.GetUtcNow();
            int updated = await database.AccountPictures.Where(item => item.AccountId == accountId).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.ContentType, picture.ContentType).SetProperty(item => item.ProtectedImage, protectedImage)
                .SetProperty(item => item.SourceHash, source).SetProperty(item => item.UpdatedAt, now), ct);
            if (updated == 0)
            {
                database.AccountPictures.Add(new() { AccountId = accountId, ContentType = picture.ContentType, ProtectedImage = protectedImage,
                    SourceHash = source, UpdatedAt = now });
                try { await database.SaveChangesAsync(ct); }
                catch (DbUpdateException) { /* A concurrent sign-in stored it first. */ }
            }
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or DbUpdateException or
            Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            logger.LogWarning("The Google profile picture could not be stored ({Type}); the initial is shown instead.", error.GetType().Name);
        }
    }

    public async Task<ProfilePicture?> FindAsync(Guid accountId, CancellationToken ct)
    {
        await using var database = await databases.CreateDbContextAsync(ct);
        var stored = await database.AccountPictures.AsNoTracking().FirstOrDefaultAsync(item => item.AccountId == accountId, ct);
        if (stored is null) return null;
        try { return new(stored.ContentType, _protector.Unprotect(stored.ProtectedImage)); }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    /// <summary>Google picture URLs end in a size option such as =s96-c; ask for 192 px so the avatar stays sharp.</summary>
    internal static string Sized(string url)
    {
        int option = url.LastIndexOf('=');
        return option > url.LastIndexOf('/') && url[(option + 1)..].StartsWith('s') ? url[..option] + "=s192-c" : url;
    }

    private async Task<ProfilePicture?> DownloadAsync(string url, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await clients.CreateClient(HttpClientName).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var memory = new MemoryStream();
        byte[] buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (memory.Length + count > MaxBytes) return null;
            memory.Write(buffer, 0, count);
        }
        byte[] image = memory.ToArray();
        return Sniff(image) is { } type ? new(type, image) : null;
    }

    /// <summary>The image type from its first bytes; anything else (HTML, SVG, scripts) is refused.</summary>
    internal static string? Sniff(ReadOnlySpan<byte> image) =>
        image.StartsWith((byte[])[0xFF, 0xD8, 0xFF]) ? "image/jpeg"
        : image.StartsWith((byte[])[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]) ? "image/png"
        : image.StartsWith("GIF8"u8) ? "image/gif"
        : image.Length > 12 && image[..4].SequenceEqual("RIFF"u8) && image[8..12].SequenceEqual("WEBP"u8) ? "image/webp"
        : null;
}
