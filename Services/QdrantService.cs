using System.Text;
using System.Text.Json;
using be_service.Models;

namespace be_service.Services;

public class QdrantService
{
    private readonly HttpClient _httpClient;
    private const string CollectionName = "pertamina_chunks";
    private const string BaseUrl = "http://localhost:6333";

    public QdrantService()
    {
        _httpClient = new HttpClient();
    }

    public async Task EnsureCollectionAsync()
    {
        var body = new
        {
            vectors = new
            {
                size = 768,
                distance = "Cosine"
            }
        };

        var json = JsonSerializer.Serialize(body);

        var response = await _httpClient.PutAsync(
            $"{BaseUrl}/collections/{CollectionName}",
            new StringContent(json, Encoding.UTF8, "application/json")
        );

        if (response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task UpsertChunkAsync(
        Guid id,
        Guid documentId,
        string documentTitle,
        string content,
        List<float> embedding)
    {
        var body = new
        {
            points = new[]
            {
                new
                {
                    id = id.ToString(),
                    vector = embedding,
                    payload = new
                    {
                        documentId = documentId.ToString(),
                        documentTitle,
                        content
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(body);

        var response = await _httpClient.PutAsync(
            $"{BaseUrl}/collections/{CollectionName}/points",
            new StringContent(json, Encoding.UTF8, "application/json")
        );

        response.EnsureSuccessStatusCode();
    }

    public async Task<List<RetrievedChunk>> SearchAsync(
        List<float> queryEmbedding,
        int limit = 10)
    {
        var body = new
        {
            vector = queryEmbedding,
            limit,
            with_payload = true
        };

        var json = JsonSerializer.Serialize(body);

        var response = await _httpClient.PostAsync(
            $"{BaseUrl}/collections/{CollectionName}/points/search",
            new StringContent(json, Encoding.UTF8, "application/json")
        );

        response.EnsureSuccessStatusCode();

        var resultJson = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(resultJson);

        var results = new List<RetrievedChunk>();

        foreach (var item in doc.RootElement.GetProperty("result").EnumerateArray())
        {
            var payload = item.GetProperty("payload");

            results.Add(new RetrievedChunk
            {
                Id = Guid.Parse(item.GetProperty("id").GetString()!),
                DocumentId = Guid.Parse(payload.GetProperty("documentId").GetString()!),
                DocumentTitle = payload.GetProperty("documentTitle").GetString()!,
                Content = payload.GetProperty("content").GetString()!,
                Similarity = item.GetProperty("score").GetSingle()
            });
        }

        return results;
    }

    public async Task<List<RetrievedChunk>> SearchByKeywordAsync(
    string keyword,
    int limit = 10)
    {
        var results = new List<RetrievedChunk>();

        var body = new
        {
            limit = 100,
            with_payload = true,
            with_vector = false
        };

        var json = JsonSerializer.Serialize(body);

        var response = await _httpClient.PostAsync(
            $"{BaseUrl}/collections/{CollectionName}/points/scroll",
            new StringContent(json, Encoding.UTF8, "application/json")
        );

        response.EnsureSuccessStatusCode();

        var resultJson = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(resultJson);

        foreach (var item in doc.RootElement
                     .GetProperty("result")
                     .GetProperty("points")
                     .EnumerateArray())
        {
            var payload = item.GetProperty("payload");
            var content = payload.GetProperty("content").GetString() ?? "";

            if (!content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                continue;

            results.Add(new RetrievedChunk
            {
                Id = Guid.Parse(item.GetProperty("id").GetString()!),
                DocumentId = Guid.Parse(payload.GetProperty("documentId").GetString()!),
                DocumentTitle = payload.GetProperty("documentTitle").GetString()!,
                Content = content,
                Similarity = 1.0f
            });

            if (results.Count >= limit)
                break;
        }

        return results;
    }
}