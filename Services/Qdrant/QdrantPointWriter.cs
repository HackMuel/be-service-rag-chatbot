using System.Text;
using System.Text.Json;

namespace be_service.Services;

public class QdrantPointWriter
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<QdrantPointWriter> _logger;

    public QdrantPointWriter(ILogger<QdrantPointWriter> logger)
    {
        _httpClient = new HttpClient();
        _logger = logger;
    }

    public async Task UpsertChunkAsync(
        Guid id,
        Guid documentId,
        string documentTitle,
        string content,
        List<float> embedding,
        int chunkIndex = -1,
        string department = "")
    {
        var body = new
        {
            points = new[]
            {
                new
                {
                    id = id.ToString(),
                    vector = embedding
                }
            }
        };

        var response = await _httpClient.PutAsync(
            $"{QdrantConstants.BaseUrl}/collections/{QdrantConstants.CollectionName}/points",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        );

        await HttpResponseGuard.EnsureSuccessAsync(response, _logger, "Qdrant point upsert");
    }
}
