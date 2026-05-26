using System.Text;
using Newtonsoft.Json;

namespace be_service.Services;

public class OllamaService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<OllamaService> _logger;

    public OllamaService(
        HttpClient httpClient,
        ILogger<OllamaService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<float>> GenerateEmbeddingAsync(string text)
    {
        var request = new
        {
            model = "nomic-embed-text",
            input = text
        };

        var response = await HttpResponseGuard.SendAsync(
            () => _httpClient.PostAsync(
                "http://localhost:11434/api/embed",
                new StringContent(
                    JsonConvert.SerializeObject(request),
                    Encoding.UTF8,
                    "application/json")),
            _logger,
            "Ollama embedding"
        );

        await HttpResponseGuard.EnsureSuccessAsync(response, _logger, "Ollama embedding");

        var json = await response.Content.ReadAsStringAsync();

        dynamic result = JsonConvert.DeserializeObject(json)!;

        var embedding = ((IEnumerable<dynamic>)result.embeddings[0])
            .Select(x => (float)x)
            .ToList();

        return embedding;
    }

    public async Task<string> GenerateChatAsync(string prompt)
    {
        var request = new
        {
            model = "qwen2.5:1.5b",
            prompt = prompt,
            stream = false
        };

        var response = await HttpResponseGuard.SendAsync(
            () => _httpClient.PostAsync(
                "http://localhost:11434/api/generate",
                new StringContent(
                    JsonConvert.SerializeObject(request),
                    Encoding.UTF8,
                    "application/json")),
            _logger,
            "Ollama generation"
        );

        await HttpResponseGuard.EnsureSuccessAsync(response, _logger, "Ollama generation");

        var json = await response.Content.ReadAsStringAsync();

        dynamic result = JsonConvert.DeserializeObject(json)!;

        return result.response.ToString();
    }
}
