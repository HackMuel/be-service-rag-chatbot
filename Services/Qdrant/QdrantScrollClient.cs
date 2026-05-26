using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using be_service.Models;
using Microsoft.Extensions.Options;

namespace be_service.Services;

public class QdrantScrollClient
{
    private static readonly string[] StructuredEntityFields =
    {
        "name",
        "division",
        "position",
        "shift",
        "employeeStatus",
        "approval",
        "maintenanceStatus",
        "location",
        "equipment",
        "technician"
    };

    private readonly HttpClient _httpClient;
    private readonly QdrantOptions _options;
    private readonly ILogger<QdrantScrollClient> _logger;

    public QdrantScrollClient(
        IOptions<QdrantOptions> options,
        ILogger<QdrantScrollClient> logger)
    {
        _httpClient = new HttpClient();
        _options = options.Value;
        _logger = logger;
    }

    public async Task<List<RetrievedChunk>> SearchByKeywordAsync(
        string keyword,
        int limit = 10)
    {
        var results = new List<RetrievedChunk>();

        string? offset = null;

        while (results.Count < limit)
        {
            var body = new Dictionary<string, object?>
            {
                ["limit"] = QdrantConstants.PageSize,
                ["with_payload"] = true,
                ["with_vector"] = false
            };

            if (!string.IsNullOrWhiteSpace(offset))
            {
                body["offset"] = offset;
            }

            var response = await HttpResponseGuard.SendAsync(
                () => _httpClient.PostAsync(
                    $"{BaseUrl}/collections/{CollectionName}/points/scroll",
                    new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")),
                _logger,
                "Qdrant keyword scroll",
                BaseUrl
            );

            await HttpResponseGuard.EnsureSuccessAsync(
                response,
                _logger,
                "Qdrant keyword scroll",
                BaseUrl);

            var resultJson = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(resultJson);
            var result = doc.RootElement.GetProperty("result");

            foreach (var item in result.GetProperty("points").EnumerateArray())
            {
                var payload = item.GetProperty("payload");
                var content = RetrievedChunkMapper.GetPayloadString(payload, "content");

                if (!content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add(RetrievedChunkMapper.Map(item, 1.0f));

                if (results.Count >= limit)
                    break;
            }

            offset = GetNextPageOffset(result);

            if (string.IsNullOrWhiteSpace(offset))
            {
                break;
            }
        }

        return results;
    }

    public async Task<List<StructuredEntityMatch>> GetKnownStructuredEntitiesAsync()
    {
        var entities = new Dictionary<string, StructuredEntityMatch>(StringComparer.OrdinalIgnoreCase);
        string? offset = null;

        while (true)
        {
            var body = new Dictionary<string, object?>
            {
                ["limit"] = QdrantConstants.PageSize,
                ["with_payload"] = true,
                ["with_vector"] = false
            };

            if (!string.IsNullOrWhiteSpace(offset))
            {
                body["offset"] = offset;
            }

            var response = await HttpResponseGuard.SendAsync(
                () => _httpClient.PostAsync(
                    $"{BaseUrl}/collections/{CollectionName}/points/scroll",
                    new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")),
                _logger,
                "Qdrant structured entity scroll",
                BaseUrl
            );

            await HttpResponseGuard.EnsureSuccessAsync(
                response,
                _logger,
                "Qdrant structured entity scroll",
                BaseUrl);

            var resultJson = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(resultJson);
            var result = doc.RootElement.GetProperty("result");

            foreach (var item in result.GetProperty("points").EnumerateArray())
            {
                if (!item.TryGetProperty("payload", out var payload) ||
                    payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var recordType = RetrievedChunkMapper.GetPayloadString(payload, "recordType");

                foreach (var fieldName in StructuredEntityFields)
                {
                    var value = RetrievedChunkMapper.GetPayloadString(payload, fieldName);

                    if (string.IsNullOrWhiteSpace(value))
                        continue;

                    var key = $"{fieldName}|{value}|{recordType}";

                    entities.TryAdd(
                        key,
                        new StructuredEntityMatch
                        {
                            FieldName = fieldName,
                            Value = value,
                            RecordType = recordType,
                            Priority = value.Length
                        });
                }
            }

            offset = GetNextPageOffset(result);

            if (string.IsNullOrWhiteSpace(offset))
            {
                break;
            }
        }

        return entities
            .Values
            .OrderByDescending(x => x.Value.Length)
            .ThenBy(x => x.FieldName)
            .ToList();
    }

    public async Task<List<RetrievedChunk>> ScrollByFilterAsync(
        object filter,
        string keyword,
        int limit)
    {
        var results = new List<RetrievedChunk>();
        string? offset = null;

        while (results.Count < limit)
        {
            var body = new Dictionary<string, object?>
            {
                ["limit"] = QdrantConstants.PageSize,
                ["with_payload"] = true,
                ["with_vector"] = false,
                ["filter"] = filter
            };

            if (!string.IsNullOrWhiteSpace(offset))
            {
                body["offset"] = offset;
            }

            var response = await HttpResponseGuard.SendAsync(
                () => _httpClient.PostAsync(
                    $"{BaseUrl}/collections/{CollectionName}/points/scroll",
                    new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")),
                _logger,
                "Qdrant filtered scroll",
                BaseUrl
            );

            await HttpResponseGuard.EnsureSuccessAsync(
                response,
                _logger,
                "Qdrant filtered scroll",
                BaseUrl);

            var resultJson = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(resultJson);
            var result = doc.RootElement.GetProperty("result");

            foreach (var item in result.GetProperty("points").EnumerateArray())
            {
                var payload = item.GetProperty("payload");
                var content = RetrievedChunkMapper.GetPayloadString(payload, "content");

                if (!MatchesKeyword(content, keyword))
                    continue;

                results.Add(RetrievedChunkMapper.Map(item, 1.0f));

                if (results.Count >= limit)
                    break;
            }

            offset = GetNextPageOffset(result);

            if (string.IsNullOrWhiteSpace(offset))
            {
                break;
            }
        }

        return results;
    }

    private static bool MatchesKeyword(string content, string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return true;

        if (content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            return true;

        var terms = Regex.Matches(keyword, @"[A-Za-z0-9]+")
            .Select(x => x.Value)
            .Where(x => x.Length > 2)
            .Where(x => !IsKeywordStopWord(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return terms.Count > 0 &&
               terms.All(term => content.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsKeywordStopWord(string value)
    {
        var stopWords = new[]
        {
            "apa",
            "saja",
            "yang",
            "pada",
            "dalam",
            "dokumen",
            "ini",
            "berapa",
            "siapa",
            "tampilkan",
            "berikan",
            "data",
            "perusahaan",
            "pertamina"
        };

        return stopWords.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    private static string? GetNextPageOffset(JsonElement result)
    {
        if (!result.TryGetProperty("next_page_offset", out var offset) ||
            offset.ValueKind == JsonValueKind.Null ||
            offset.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        return offset.ValueKind == JsonValueKind.String
            ? offset.GetString()
            : offset.GetRawText();
    }

    private string BaseUrl => string.IsNullOrWhiteSpace(_options.BaseUrl)
        ? QdrantOptions.DefaultBaseUrl
        : _options.BaseUrl.TrimEnd('/');

    private string CollectionName => string.IsNullOrWhiteSpace(_options.CollectionName)
        ? QdrantOptions.DefaultCollectionName
        : _options.CollectionName.Trim();
}
