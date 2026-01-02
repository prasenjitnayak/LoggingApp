using System.Diagnostics;

namespace Logging.API.Middleware;

public class SampleMiddleware
{
    private readonly RequestDelegate _next;

    public SampleMiddleware(
        RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var activity = Activity.Current;

        var traceId = activity?.TraceId.ToString();
        var spanId = activity?.SpanId.ToString();

        // Add to response headers (very useful)
        if (!string.IsNullOrEmpty(traceId))
        {
            context.Response.Headers["trace-id"] = traceId;
            context.Response.Headers["span-id"] = spanId ?? string.Empty;
        }
        await _next(context);
    }
}