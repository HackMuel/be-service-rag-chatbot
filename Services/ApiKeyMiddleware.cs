using Microsoft.Extensions.Options;

namespace be_service.Services;

public class SecurityOptions
{
    public string ApiKey { get; set; } = string.Empty;
}

public class ApiKeyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _configuredApiKey;
    private readonly ILogger<ApiKeyMiddleware> _logger;
    private const string ApiKeyHeader = "X-API-Key";

    public ApiKeyMiddleware(
        RequestDelegate next,
        IOptions<SecurityOptions> securityOptions,
        ILogger<ApiKeyMiddleware> logger)
    {
        _next = next;
        _configuredApiKey = securityOptions.Value.ApiKey;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";

        if (path.StartsWith("/api/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (string.IsNullOrWhiteSpace(_configuredApiKey))
        {
            _logger.LogWarning("Security:ApiKey is not configured — all requests are allowed through.");
            await _next(context);
            return;
        }

        if (!context.Request.Headers.TryGetValue(ApiKeyHeader, out var providedKey) ||
            !string.Equals(providedKey, _configuredApiKey, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Unauthorized request: missing or invalid API key. Path={Path}",
                path);

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";

            await context.Response.WriteAsync(
                """{"error":"Unauthorized","message":"Invalid or missing X-API-Key header."}""");

            return;
        }

        await _next(context);
    }
}
