namespace be_service.Services;

public static class HttpResponseGuard
{
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

        throw new UpstreamServiceException(serviceName, response.StatusCode);
    }

}
