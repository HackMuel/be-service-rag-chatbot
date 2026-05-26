using System.Text;
using System.Text.Json;

namespace be_service.Services;

public class QdrantCollectionService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<QdrantCollectionService> _logger;

    public QdrantCollectionService(ILogger<QdrantCollectionService> logger)
    {
        _httpClient = new HttpClient();
        _logger = logger;
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

        var response = await HttpResponseGuard.SendAsync(
            () => _httpClient.PutAsync(
                $"{QdrantConstants.BaseUrl}/collections/{QdrantConstants.CollectionName}",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")),
            _logger,
            "Qdrant collection setup"
        );

        if (response.StatusCode != System.Net.HttpStatusCode.Conflict)
        {
            await HttpResponseGuard.EnsureSuccessAsync(response, _logger, "Qdrant collection setup");
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
                        $"{QdrantConstants.BaseUrl}/collections/{QdrantConstants.CollectionName}/index",
                        new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")),
                    _logger,
                    "Qdrant payload index"
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
}
