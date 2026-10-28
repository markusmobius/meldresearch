using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using MeldResearch.Research;

namespace MeldResearch.LocalBackend.Middleware;

internal sealed class ApiFailure(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

internal sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
            return false;

        int status = exception is ApiFailure failure ? failure.Status : exception is ResearchFailure research ? research.Status :
            exception is BadHttpRequestException ? 400 : StatusCodes.Status500InternalServerError;
        string code = exception is ApiFailure known ? known.Code : exception is ResearchFailure knownResearch ? knownResearch.Code : "local_operation_failed";
        string detail = exception is ApiFailure or ResearchFailure ? exception.Message :
            "The local operation failed. Check available disk space and access to the application data directory.";

        logger.LogWarning("Local operation failed: {Code} ({ExceptionType}). Content and credentials omitted.",
            code, exception.GetType().Name);
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Status = status,
            Title = code,
            Detail = detail,
            Extensions = { ["code"] = code }
        }, options: null, contentType: "application/problem+json", cancellationToken: cancellationToken);
        return true;
    }
}
