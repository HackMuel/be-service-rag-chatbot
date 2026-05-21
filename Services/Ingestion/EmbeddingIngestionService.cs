namespace be_service.Services;

public class EmbeddingIngestionService
{
    private readonly OllamaService _ollamaService;
    private readonly ILogger<EmbeddingIngestionService> _logger;

    public EmbeddingIngestionService(
        OllamaService ollamaService,
        ILogger<EmbeddingIngestionService> logger)
    {
        _ollamaService = ollamaService;
        _logger = logger;
    }

    public async Task<List<float>> GenerateEmbeddingAsync(string content)
    {
        try
        {
            return await _ollamaService.GenerateEmbeddingAsync(content);
        }
        catch (UpstreamServiceException ex)
        {
            _logger.LogError(
                ex,
                "Embedding generation failed due to upstream error: service={ServiceName}, status={StatusCode}",
                ex.ServiceName,
                ex.StatusCode);
            throw;
        }
    }
}
