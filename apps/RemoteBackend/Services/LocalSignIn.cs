using Microsoft.Extensions.Primitives;

namespace MeldResearch.RemoteBackend.Services;

/// <summary>
/// A local installation's sign-in request (OAuth loopback redirect with PKCE). The website returns a one-time code only
/// to the installation's own loopback callback, so a forwarded or phished link cannot deliver an account to another
/// computer; the code is useless without the verifier that only the installation holds.
/// </summary>
internal sealed record LocalSignIn(string State, string Challenge, string RedirectUri, string Device)
{
    public const string CallbackPath = "/account/callback";

    public static LocalSignIn? From(Func<string, StringValues> read)
    {
        string state = read("state").ToString(), challenge = read("challenge").ToString();
        string redirect = read("redirect_uri").ToString(), device = read("device").ToString();
        if (state.Length == 0 && challenge.Length == 0 && redirect.Length == 0 && device.Length == 0) return null;
        if (!Hex64(state) || !Hex64(challenge) || device.Length is 0 or > 100 || device.Any(char.IsControl) ||
            !Uri.TryCreate(redirect, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttp ||
            uri.Host is not ("127.0.0.1" or "localhost") || uri.Port is < 1024 or > 65535 || uri.UserInfo.Length != 0 ||
            uri.AbsolutePath != CallbackPath || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new MeldResearch.Research.ResearchFailure(400, "invalid_sign_in_request",
                "This sign-in link is invalid. Start again from Account in the Meld Research app.");
        return new(state.ToLowerInvariant(), challenge.ToLowerInvariant(), $"http://{uri.Host}:{uri.Port}{CallbackPath}", device);
    }

    public string Query => $"state={State}&challenge={Challenge}&redirect_uri={Uri.EscapeDataString(RedirectUri)}&device={Uri.EscapeDataString(Device)}";
    public string ConnectUrl => "/connect?" + Query;
    public string Callback(string code) => $"{RedirectUri}?code={code}&state={State}";
    public string FormInputs(Func<string, string> encode) =>
        $"<input type=\"hidden\" name=\"state\" value=\"{State}\"><input type=\"hidden\" name=\"challenge\" value=\"{Challenge}\">" +
        $"<input type=\"hidden\" name=\"redirect_uri\" value=\"{encode(RedirectUri)}\"><input type=\"hidden\" name=\"device\" value=\"{encode(Device)}\">";

    private static bool Hex64(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
}
