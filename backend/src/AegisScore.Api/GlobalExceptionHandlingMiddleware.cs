using System.Net.Mime;
using AegisScore.Application.Abstractions;

namespace AegisScore.Api;

/// <summary>
/// Terminal error boundary for the whole pipeline. Nothing internal (message, stack trace,
/// exception type) ever reaches the client — only a generic HTTP 500 plus a correlation id.
/// Security-relevant failures (tenant isolation) are logged as a distinct security event.
/// </summary>
public sealed class GlobalExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandlingMiddleware> _logger;

    public GlobalExceptionHandlingMiddleware(RequestDelegate next, ILogger<GlobalExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (TenantSecurityException ex)
        {
            // SECURITY EVENT: fail-closed multi-tenant violation. Full detail stays server-side.
            _logger.LogError(ex,
                "SECURITY: violação de isolamento multi-tenant na escrita. " +
                "TraceId={TraceId} Method={Method} Path={Path} User={User} RemoteIp={RemoteIp}",
                context.TraceIdentifier,
                context.Request.Method,
                context.Request.Path.Value,
                context.User?.Identity?.Name ?? "(anon)",
                context.Connection.RemoteIpAddress?.ToString() ?? "(unknown)");

            await WriteGenericServerErrorAsync(context);
        }
        catch (Exception ex)
        {
            // Any other unhandled failure: log server-side, never leak internals.
            _logger.LogError(ex,
                "Unhandled exception. TraceId={TraceId} Method={Method} Path={Path}",
                context.TraceIdentifier, context.Request.Method, context.Request.Path.Value);

            await WriteGenericServerErrorAsync(context);
        }
    }

    /// <summary>Writes an opaque 500 with only a correlation id — no exception details.</summary>
    private static async Task WriteGenericServerErrorAsync(HttpContext context)
    {
        // If the response already began streaming we can't safely rewrite it.
        if (context.Response.HasStarted)
            return;

        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = MediaTypeNames.Application.Json; // application/json

        // RFC 7807-ish body. traceId lets support correlate with the server log above.
        await context.Response.WriteAsJsonAsync(new
        {
            title = "Erro interno do servidor.",
            status = 500,
            traceId = context.TraceIdentifier,
        });
    }
}
