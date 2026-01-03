using Serilog.Context;
using System.Security.Claims;

namespace Logging.API.Middleware;

public class LogContextMiddleware
{
    private readonly RequestDelegate _next;

    public LogContextMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvockAsync(HttpContext context)
    {
        var userId = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.User?.FindFirst("oid")?.Value;
        var userName = context.User?.Identity?.Name;
        var clientIp = context.Connection.RemoteIpAddress?.ToString();
        var requestPath = context.Request.Path.Value;
        var requestMethod = context.Request.Method;
        var correlationId = context.Request.Headers["x-correlation-id"].FirstOrDefault() ?? context.TraceIdentifier;

        using (LogContext.PushProperty("UserId", userId ?? "anonymous"))
        using (LogContext.PushProperty("UserName", userName ?? "anonymous"))
        using (LogContext.PushProperty("ClientIP", clientIp))
        using (LogContext.PushProperty("RequestPath", requestPath))
        using (LogContext.PushProperty("RequestMethod", requestMethod))
        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await _next(context);
        }
    }
}
