using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using MeldResearch.Research;

namespace MeldResearch.RemoteBackend.Middleware;

internal sealed class RemoteExceptionHandler(ILogger<RemoteExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested) return false;
        var failure = exception as ResearchFailure;
        int status = failure?.Status ?? (exception is BadHttpRequestException ? 400 :
            exception is HttpRequestException ? 502 : exception is OperationCanceledException ? 504 : 500);
        string code = failure?.Code ?? (status == 400 ? "invalid_request" : status == 504 ? "remote_timeout" : "remote_operation_failed");
        logger.LogWarning("Remote operation failed: {Code} ({Type}). Credentials and evidence omitted.", code, exception.GetType().Name);
        context.Response.StatusCode = status;
        if (status == 429) context.Response.Headers.RetryAfter = "5";
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status, Title = code,
            Detail = failure?.Message ?? "The remote operation failed. Check service configuration and availability.",
            Extensions = { ["code"] = code }
        }, options: null, contentType: "application/problem+json", cancellationToken: ct);
        return true;
    }
}
