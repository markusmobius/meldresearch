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
        // The connector reads through its own background Overleaf tab (connector 0.6.6); these say what happened to it.
        "tab_closed" => new(503, "overleaf_tab_closed", "The Overleaf tab Meld reads through was closed during a read. Meld retries automatically; you can also retry now."),
        "tab_navigated" => new(503, "overleaf_tab_navigated",
            "The Overleaf tab Meld reads through moved to another page during a read. Meld retries automatically; you can also retry now."),
        "tab_discarded" => new(503, "overleaf_tab_asleep",
            "Your browser put the Overleaf tab Meld reads through to sleep. Meld retries automatically; if this repeats, keep overleaf.com awake in your browser's sleeping-tab or Memory Saver settings."),
        "tab_unavailable" => new(503, "overleaf_tab_unavailable",
            "Your browser could not open Overleaf for Meld. Check that www.overleaf.com loads in your browser, then retry; Meld also retries automatically."),
        "network_error" => new(502, "overleaf_network_error", "The browser could not read Overleaf. Check your browser's Overleaf tab and connection."),
        "project_list_unavailable" => new(502, "overleaf_project_list_unavailable",
            "Overleaf did not return its project list. Finish sign-in or browser verification and retry. Its page format may also have changed."),
        "metadata_invalid" => new(502, "overleaf_metadata_changed", "Overleaf returned unsupported project metadata. No project list was replaced."),
        "bridge_timeout" => new(504, "bridge_timeout", "The local transfer stopped responding. Reconnect the extension."),
        _ => new(502, "overleaf_transfer_failed", "The Overleaf transfer failed. Check the browser connection and Overleaf sign-in.")
    };
}
