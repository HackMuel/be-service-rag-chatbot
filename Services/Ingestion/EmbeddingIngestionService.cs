namespace be_service.Services;

public class EmbeddingIngestionService
{
    private readonly OllamaService _ollamaService;
    private readonly SparseBm25Encoder _sparseEncoder;
    private readonly ILogger<EmbeddingIngestionService> _logger;

    public EmbeddingIngestionService(
        OllamaService ollamaService,
        SparseBm25Encoder sparseEncoder,
        ILogger<EmbeddingIngestionService> logger)
    {
        _ollamaService = ollamaService;
        _sparseEncoder = sparseEncoder;
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

    // Synchronous BM25 sparse encoding — no LLM call, suitable for ingest loop.
    public Dictionary<uint, float> GenerateSparseVector(string content)
    {
        return _sparseEncoder.Encode(content);
    }
}
