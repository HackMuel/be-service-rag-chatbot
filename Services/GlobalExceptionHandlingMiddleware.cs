using System.Text.Json;

namespace be_service.Services;

public class GlobalExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<GlobalExceptionHandlingMiddleware> _logger;

    public GlobalExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<GlobalExceptionHandlingMiddleware> logger)
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
        catch (ExternalServiceUnavailableException ex)
        {
            _logger.LogError(
                ex,
                "SERVICE_UNAVAILABLE service={ServiceName}",
                ex.ServiceName);

            await WriteJsonAsync(
                context,
                StatusCodes.Status503ServiceUnavailable,
                new
                {
                    error = ex.Error,
                    message = ex.UserMessage
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "UNHANDLED_EXCEPTION path={Path}",
                context.Request.Path.Value);

            await WriteJsonAsync(
                context,
                StatusCodes.Status500InternalServerError,
                new
                {
                    error = "Internal server error",
                    message = "Terjadi kesalahan pada server."
                });
        }
    }

    private static async Task WriteJsonAsync(
        HttpContext context,
        int statusCode,
        object body)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }
}
