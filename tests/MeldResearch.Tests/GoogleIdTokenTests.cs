using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MeldResearch.RemoteBackend.Services;
using MeldResearch.Research;

namespace MeldResearch.Tests;

public sealed class GoogleIdTokenTests : IDisposable
{
    private const string ClientId = "synthetic-client.apps.googleusercontent.com";
    private const string Nonce = "4f1c2b8a9d0e7f6a5b4c3d2e1f0a9b8c";
    private readonly RSA _google = RSA.Create(2048);
    private readonly RSA _attacker = RSA.Create(2048);
    private readonly GoogleIdTokenVerifier _verifier;

    public GoogleIdTokenTests()
    {
        var configuration = new OpenIdConnectConfiguration();
        configuration.SigningKeys.Add(new RsaSecurityKey(_google) { KeyId = "google-key" });
        _verifier = new GoogleIdTokenVerifier(
            new RemoteOptions { DataDirectory = Path.GetTempPath(), PublicOrigin = new("https://meld.example.invalid"), Dummy = false, GoogleClientId = ClientId },
            new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration));
    }

    public void Dispose()
    {
        _google.Dispose();
        _attacker.Dispose();
    }

    [Fact]
    public async Task AcceptsAGoogleSignedTokenForThisClientWithAVerifiedEmailAndTheSessionNonce()
    {
        var identity = await _verifier.VerifyAsync(Token(), Nonce, CancellationToken.None);
        Assert.Equal(new GoogleIdentity("108234567890123456789", "person@gmail.com", "Synthetic Person"), identity);
    }

    public static TheoryData<string> Rejections => ["audience", "issuer", "expired", "unverified", "nonce", "signer", "unsigned", "malformed", "subject"];

    [Theory]
    [MemberData(nameof(Rejections))]
    public async Task RejectsTokensThatDoNotProveThisGoogleUser(string scenario)
    {
        string token = scenario switch
        {
            "audience" => Token(audience: "another-client.apps.googleusercontent.com"),
            "issuer" => Token(issuer: "https://accounts.example.invalid"),
            "expired" => Token(expires: DateTime.UtcNow.AddMinutes(-10)),
            "unverified" => Token(claims => claims["email_verified"] = false),
            "nonce" => Token(claims => claims["nonce"] = "replayed-from-another-page"),
            "signer" => Token(signer: _attacker),
            "unsigned" => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = "https://accounts.google.com", Audience = ClientId, Expires = DateTime.UtcNow.AddMinutes(30),
                Claims = new Dictionary<string, object> { ["sub"] = "1", ["email"] = "person@gmail.com", ["email_verified"] = true, ["nonce"] = Nonce }
            }),
            "malformed" => "not-a-google-token",
            "subject" => Token(claims => claims.Remove("sub")),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var error = await Assert.ThrowsAsync<ResearchFailure>(() => _verifier.VerifyAsync(token, Nonce, CancellationToken.None));
        Assert.Equal("google_sign_in_failed", error.Code);
    }

    [Fact]
    public async Task RequiresAServerIssuedNonce()
    {
        await Assert.ThrowsAsync<ResearchFailure>(() => _verifier.VerifyAsync(Token(), "", CancellationToken.None));
    }

    private string Token(Action<Dictionary<string, object>>? change = null, string issuer = "https://accounts.google.com",
        string audience = ClientId, DateTime? expires = null, RSA? signer = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["sub"] = "108234567890123456789", ["email"] = "person@gmail.com", ["email_verified"] = true,
            ["name"] = "Synthetic Person", ["nonce"] = Nonce
        };
        change?.Invoke(claims);
        var expiry = expires ?? DateTime.UtcNow.AddMinutes(30);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = audience, Claims = claims, IssuedAt = expiry.AddHours(-1), NotBefore = expiry.AddHours(-1), Expires = expiry,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(signer ?? _google) { KeyId = "google-key" }, SecurityAlgorithms.RsaSha256)
        });
    }
}
