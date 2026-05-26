namespace be_service.Models;

public class OllamaOptions
{
    public const string DefaultBaseUrl = "http://localhost:11434";
    public const string DefaultEmbeddingModel = "nomic-embed-text";
    public const string DefaultChatModel = "qwen2.5:1.5b";
    public const int DefaultTimeoutSeconds = 120;

    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string EmbeddingModel { get; set; } = DefaultEmbeddingModel;
    public string ChatModel { get; set; } = DefaultChatModel;
    public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;
}
