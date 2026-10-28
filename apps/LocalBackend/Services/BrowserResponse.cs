using MeldResearch.LocalBackend.Middleware;

namespace MeldResearch.LocalBackend.Services;

internal sealed class BrowserResponse(string path, string contentType) : IAsyncDisposable
{
    public string Path { get; } = path;
    public string ContentType { get; } = contentType;

    public ValueTask DisposeAsync()
    {
        File.Delete(Path);
        return ValueTask.CompletedTask;
    }
}

internal static class BrowserFailures
{
    public static ApiFailure FromCode(string? code) => code switch
    {
        "sign_in_required" or "http_401" or "http_403" =>
            new(409, "overleaf_sign_in_required", "Choose Sign in to Overleaf and finish signing in or browser verification in your normal browser."),
        "http_429" => new(429, "overleaf_rate_limited", "Overleaf is rate limiting requests. Wait before trying again."),
        "http_404" => new(404, "overleaf_project_unavailable", "Overleaf could not find this project. Refresh the project list."),
        "response_too_large" => new(413, "response_too_large", "The Overleaf response exceeds the prototype's size limit."),
        "tab_closed" => new(503, "overleaf_tab_closed", "The Overleaf tab closed or navigated. Choose Sign in to Overleaf or retry the project list."),
        "network_error" => new(502, "overleaf_network_error", "The browser could not read Overleaf. Check your browser's Overleaf tab and connection."),
        "project_list_unavailable" => new(502, "overleaf_project_list_unavailable",
            "Overleaf did not return its project list. Finish sign-in or browser verification and retry. Its page format may also have changed."),
        "metadata_invalid" => new(502, "overleaf_metadata_changed", "Overleaf returned unsupported project metadata. No project list was replaced."),
        "bridge_timeout" => new(504, "bridge_timeout", "The local transfer stopped responding. Reconnect the extension."),
        _ => new(502, "overleaf_transfer_failed", "The Overleaf transfer failed. Check the browser connection and Overleaf sign-in.")
    };
}
