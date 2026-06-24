namespace be_service.Services;

public class ExternalServiceUnavailableException : Exception
{
    public string ServiceName { get; }
    public string Error { get; }
    public string UserMessage { get; }

    public ExternalServiceUnavailableException(
        string serviceName,
        string error,
        string userMessage,
        Exception? innerException = null)
        : base(userMessage, innerException)
    {
        ServiceName = serviceName;
        Error = error;
        UserMessage = userMessage;
    }

    public static ExternalServiceUnavailableException FromServiceName(
        string serviceName,
        Exception? innerException = null,
        string serviceBaseUrl = "")
    {
        if (serviceName.Contains("Qdrant", StringComparison.OrdinalIgnoreCase))
        {
            return Qdrant(innerException, serviceBaseUrl);
        }

        if (serviceName.Contains("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            return Ollama(innerException, serviceBaseUrl);
        }

        if (serviceName.Contains("Object", StringComparison.OrdinalIgnoreCase) ||
            serviceName.Contains("MinIO", StringComparison.OrdinalIgnoreCase))
        {
            return ObjectStorage(innerException);
        }

        return new ExternalServiceUnavailableException(
            serviceName,
            $"{serviceName} unavailable",
            "Layanan eksternal tidak dapat dihubungi.",
            innerException);
    }

    public static ExternalServiceUnavailableException Qdrant(
        Exception? innerException = null,
        string serviceBaseUrl = "")
    {
        return new ExternalServiceUnavailableException(
            "Qdrant",
            "Qdrant unavailable",
            BuildUnavailableMessage("Qdrant", serviceBaseUrl),
            innerException);
    }

    public static ExternalServiceUnavailableException Ollama(
        Exception? innerException = null,
        string serviceBaseUrl = "")
    {
        return new ExternalServiceUnavailableException(
            "Ollama",
            "Ollama unavailable",
            BuildUnavailableMessage("Ollama", serviceBaseUrl),
            innerException);
    }

    public static ExternalServiceUnavailableException ObjectStorage(Exception? innerException = null)
    {
        return new ExternalServiceUnavailableException(
            "ObjectStorage",
            "Object storage unavailable",
            "Object storage tidak dapat dihubungi. Pastikan MinIO berjalan dan bucket tersedia.",
            innerException);
    }

    private static string BuildUnavailableMessage(
        string serviceName,
        string serviceBaseUrl)
    {
        return string.IsNullOrWhiteSpace(serviceBaseUrl)
            ? $"Layanan {serviceName} tidak dapat dihubungi."
            : $"Layanan {serviceName} tidak dapat dihubungi. Pastikan {serviceName} berjalan di {serviceBaseUrl}.";
    }
}
