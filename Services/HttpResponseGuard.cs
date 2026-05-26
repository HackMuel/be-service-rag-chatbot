namespace be_service.Services;

public static class HttpResponseGuard
{
    public static async Task<HttpResponseMessage> SendAsync(
        Func<Task<HttpResponseMessage>> send,
        ILogger logger,
        string serviceName)
    {
        try
        {
            return await send();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(
                ex,
                "SERVICE_UNAVAILABLE service={ServiceName}",
                ResolveServiceName(serviceName));

            throw ExternalServiceUnavailableException.FromServiceName(serviceName, ex);
        }
    }

    public static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        ILogger logger,
        string serviceName)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync();

        logger.LogError(
            "{ServiceName} call failed: {StatusCode}, bodyLength={BodyLength}",
            serviceName,
            response.StatusCode,
            body.Length);

        throw ExternalServiceUnavailableException.FromServiceName(serviceName);
    }

    private static string ResolveServiceName(string serviceName)
    {
        if (serviceName.Contains("Qdrant", StringComparison.OrdinalIgnoreCase))
            return "Qdrant";

        if (serviceName.Contains("Ollama", StringComparison.OrdinalIgnoreCase))
            return "Ollama";

        if (serviceName.Contains("Object", StringComparison.OrdinalIgnoreCase) ||
            serviceName.Contains("MinIO", StringComparison.OrdinalIgnoreCase))
            return "ObjectStorage";

        return serviceName;
    }
}
