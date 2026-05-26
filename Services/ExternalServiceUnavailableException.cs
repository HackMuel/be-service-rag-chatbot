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
        Exception? innerException = null)
    {
        if (serviceName.Contains("Qdrant", StringComparison.OrdinalIgnoreCase))
        {
            return Qdrant(innerException);
        }

        if (serviceName.Contains("Ollama", StringComparison.OrdinalIgnoreCase))
        {
            return Ollama(innerException);
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

    public static ExternalServiceUnavailableException Qdrant(Exception? innerException = null)
    {
        return new ExternalServiceUnavailableException(
            "Qdrant",
            "Qdrant unavailable",
            "Layanan Qdrant tidak dapat dihubungi. Pastikan Qdrant berjalan di localhost:6333.",
            innerException);
    }

    public static ExternalServiceUnavailableException Ollama(Exception? innerException = null)
    {
        return new ExternalServiceUnavailableException(
            "Ollama",
            "Ollama unavailable",
            "Layanan Ollama tidak dapat dihubungi. Pastikan Ollama berjalan di localhost:11434.",
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
}
