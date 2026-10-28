using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MeldResearch.Research;

namespace MeldResearch.RemoteBackend.Services;

/// <summary>The Google identity proven by a verified ID token.</summary>
internal sealed record GoogleIdentity(string Subject, string Email, string Name);

internal interface IGoogleIdTokenVerifier
{
    Task<GoogleIdentity> VerifyAsync(string idToken, string expectedNonce, CancellationToken ct);
}

/// <summary>
/// Verifies a Sign in with Google ID token: Google's signature (keys from its OpenID metadata, refreshed on
/// rotation), issuer, this site's client ID as audience, lifetime, RS256, a verified email address, and the
/// one-time nonce bound to the browser session (replay protection). No client secret is involved.
/// </summary>
internal sealed class GoogleIdTokenVerifier(RemoteOptions options, IConfigurationManager<OpenIdConnectConfiguration> metadata)
    : IGoogleIdTokenVerifier
{
    public const string MetadataAddress = "https://accounts.google.com/.well-known/openid-configuration";
    private const int MaxTokenLength = 8192;
    private static readonly JsonWebTokenHandler Handler = new() { MapInboundClaims = false };

    public async Task<GoogleIdentity> VerifyAsync(string idToken, string expectedNonce, CancellationToken ct)
    {
        if (!options.GoogleConfigured) throw Failure("Google sign-in is not configured on this server.");
        if (string.IsNullOrEmpty(idToken) || idToken.Length > MaxTokenLength || string.IsNullOrEmpty(expectedNonce) ||
            !Handler.CanReadToken(idToken))
            throw Failure("Google did not return a sign-in token. Try again.");

        var result = await ValidateAsync(idToken, ct);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // Google rotated its signing keys; fetch the new set once.
            metadata.RequestRefresh();
            result = await ValidateAsync(idToken, ct);
        }
        if (!result.IsValid) throw Failure("Google sign-in could not be verified. Try again.");

        var claims = result.Claims;
        string? Claim(string name) => claims.TryGetValue(name, out var value) ? value?.ToString() : null;
        string subject = Claim("sub") ?? "", email = Claim("email") ?? "", nonce = Claim("nonce") ?? "";
        bool verified = claims.TryGetValue("email_verified", out var flag) &&
            (flag is true || string.Equals(flag?.ToString(), "true", StringComparison.OrdinalIgnoreCase));
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(nonce), Encoding.UTF8.GetBytes(expectedNonce)))
            throw Failure("This Google sign-in belongs to another page load. Try again.");
        if (subject.Length is 0 or > 255 || email.Length is 0 or > 320 || !verified)
            throw Failure("A Google account with a verified email address is required.");
        string name = Claim("name") is { Length: > 0 } display ? display[..Math.Min(display.Length, 200)] : email;
        return new(subject, email, name);
    }

    private async Task<TokenValidationResult> ValidateAsync(string idToken, CancellationToken ct)
    {
        OpenIdConnectConfiguration configuration;
        try { configuration = await metadata.GetConfigurationAsync(ct); }
        catch (Exception error) when (error is InvalidOperationException or IOException or HttpRequestException)
        { throw new ResearchFailure(503, "google_keys_unavailable", "Google sign-in is temporarily unavailable. Try again shortly."); }
        return await Handler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuers = ["https://accounts.google.com", "accounts.google.com"],
            ValidAudience = options.GoogleClientId,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        });
    }

    private static ResearchFailure Failure(string message) => new(401, "google_sign_in_failed", message);
}
