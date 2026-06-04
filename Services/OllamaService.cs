using System.Text;
using be_service.Models;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;

namespace be_service.Services;

public class OllamaService
{
    private readonly HttpClient _httpClient;
    private readonly OllamaOptions _options;
    private readonly ILogger<OllamaService> _logger;

    public OllamaService(
        HttpClient httpClient,
        IOptions<OllamaOptions> options,
        ILogger<OllamaService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        _httpClient.BaseAddress = new Uri($"{BaseUrl}/");

        if (_options.TimeoutSeconds > 0)
        {
            _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
        }
    }

    public async Task<List<float>> GenerateEmbeddingAsync(string text)
    {
        var request = new
        {
            model = EmbeddingModel,
            input = text
        };

        var response = await HttpResponseGuard.SendAsync(
            () => _httpClient.PostAsync(
                "api/embed",
                new StringContent(
                    JsonConvert.SerializeObject(request),
                    Encoding.UTF8,
                    "application/json")),
            _logger,
            "Ollama embedding",
            BaseUrl
        );

        await HttpResponseGuard.EnsureSuccessAsync(
            response,
            _logger,
            "Ollama embedding",
            BaseUrl);

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
            model = ChatModel,
            prompt = prompt,
            stream = false
        };

        var response = await HttpResponseGuard.SendAsync(
            () => _httpClient.PostAsync(
                "api/generate",
                new StringContent(
                    JsonConvert.SerializeObject(request),
                    Encoding.UTF8,
                    "application/json")),
            _logger,
            "Ollama generation",
            BaseUrl
        );

        await HttpResponseGuard.EnsureSuccessAsync(
            response,
            _logger,
            "Ollama generation",
            BaseUrl);

        var json = await response.Content.ReadAsStringAsync();

        dynamic result = JsonConvert.DeserializeObject(json)!;

        return result.response.ToString();
    }

    public async Task<string> CompleteAsync(
        string systemPrompt,
        string userMessage,
        double? temperature = null)
    {
        object request = temperature is null
            ? new
            {
                model = ChatModel,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user",   content = userMessage  }
                },
                stream = false
            }
            : new
            {
                model = ChatModel,
                messages = new[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user",   content = userMessage  }
                },
                stream = false,
                options = new { temperature = temperature.Value }
            };

        var response = await HttpResponseGuard.SendAsync(
            () => _httpClient.PostAsync(
                "api/chat",
                new StringContent(
                    JsonConvert.SerializeObject(request),
                    Encoding.UTF8,
                    "application/json")),
            _logger,
            "Ollama complete",
            BaseUrl
        );

        await HttpResponseGuard.EnsureSuccessAsync(
            response,
            _logger,
            "Ollama complete",
            BaseUrl);

        var json = await response.Content.ReadAsStringAsync();
        dynamic result = JsonConvert.DeserializeObject(json)!;
        return result.message.content.ToString();
    }

    private string BaseUrl => string.IsNullOrWhiteSpace(_options.BaseUrl)
        ? OllamaOptions.DefaultBaseUrl
        : _options.BaseUrl.TrimEnd('/');

    private string EmbeddingModel => string.IsNullOrWhiteSpace(_options.EmbeddingModel)
        ? OllamaOptions.DefaultEmbeddingModel
        : _options.EmbeddingModel;

    private string ChatModel => string.IsNullOrWhiteSpace(_options.ChatModel)
        ? OllamaOptions.DefaultChatModel
        : _options.ChatModel;
}
