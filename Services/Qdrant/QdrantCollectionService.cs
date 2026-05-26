using System.Text;
using System.Text.Json;
using be_service.Models;
using Microsoft.Extensions.Options;

namespace be_service.Services;

public class QdrantCollectionService
{
    private readonly HttpClient _httpClient;
    private readonly QdrantOptions _options;
    private readonly ILogger<QdrantCollectionService> _logger;

    public QdrantCollectionService(
        IOptions<QdrantOptions> options,
        ILogger<QdrantCollectionService> logger)
    {
        _httpClient = new HttpClient();
        _options = options.Value;
        _logger = logger;
    }

    public async Task EnsureCollectionAsync()
    {
        var body = new
        {
            vectors = new
            {
                size = _options.VectorSize > 0
                    ? _options.VectorSize
                    : QdrantOptions.DefaultVectorSize,
                distance = string.IsNullOrWhiteSpace(_options.Distance)
                    ? QdrantOptions.DefaultDistance
                    : _options.Distance
            }
        };

        var response = await HttpResponseGuard.SendAsync(
            () => _httpClient.PutAsync(
                $"{BaseUrl}/collections/{CollectionName}",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")),
            _logger,
            "Qdrant collection setup",
            BaseUrl
        );

        if (response.StatusCode != System.Net.HttpStatusCode.Conflict)
        {
            await HttpResponseGuard.EnsureSuccessAsync(
                response,
                _logger,
                "Qdrant collection setup",
                BaseUrl);
        }

        await EnsurePayloadIndexesAsync();
    }

    private async Task EnsurePayloadIndexesAsync()
    {
        var indexes = new[]
        {
            "recordType",
            "nik",
            "nameNormalized",
            "maintenanceCode",
            "date",
            "division",
            "department",
            "position",
            "shift",
            "employeeStatus",
            "duration",
            "approval",
            "equipment",
            "location",
            "maintenanceStatus",
            "technician",
            "sectionTitle"
        };

        foreach (var fieldName in indexes)
        {
            var body = new
            {
                field_name = fieldName,
                field_schema = "keyword"
            };

            try
            {
                var response = await HttpResponseGuard.SendAsync(
                    () => _httpClient.PutAsync(
                        $"{BaseUrl}/collections/{CollectionName}/index",
                        new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")),
                    _logger,
                    "Qdrant payload index",
                    BaseUrl
                );

                if (!response.IsSuccessStatusCode &&
                    response.StatusCode != System.Net.HttpStatusCode.Conflict)
                {
                    _logger.LogWarning(
                        "Qdrant payload index warning: field={FieldName}, status={StatusCode}",
                        fieldName,
                        response.StatusCode);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Qdrant payload index warning: field={FieldName}",
                    fieldName);
            }
        }
    }

    private string BaseUrl => string.IsNullOrWhiteSpace(_options.BaseUrl)
        ? QdrantOptions.DefaultBaseUrl
        : _options.BaseUrl.TrimEnd('/');

    private string CollectionName => string.IsNullOrWhiteSpace(_options.CollectionName)
        ? QdrantOptions.DefaultCollectionName
        : _options.CollectionName.Trim();
}
