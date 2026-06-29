using System.Text;
using System.Text.Json;
using be_service.Models;
using Microsoft.Extensions.Options;

namespace be_service.Services;

public class QdrantCollectionService
{
    private readonly HttpClient _httpClient;
    private readonly QdrantOptions _options;
    private readonly DatasetSchemaOptions _schema;
    private readonly ILogger<QdrantCollectionService> _logger;

    public QdrantCollectionService(
        IOptions<QdrantOptions> options,
        IOptions<DatasetSchemaOptions> datasetSchema,
        ILogger<QdrantCollectionService> logger)
    {
        _httpClient = new HttpClient();
        _options = options.Value;
        _schema = datasetSchema.Value;
        _logger = logger;
    }

    public async Task EnsureCollectionAsync()
    {
        var size = _options.VectorSize > 0 ? _options.VectorSize : QdrantOptions.DefaultVectorSize;
        var distance = string.IsNullOrWhiteSpace(_options.Distance)
            ? QdrantOptions.DefaultDistance
            : _options.Distance;

        // Named vectors: "dense" (Cosine, 768-dim) + "sparse" (BM25 TF, on-disk=false)
        var body = new
        {
            vectors = new Dictionary<string, object>
            {
                ["dense"] = new { size, distance }
            },
            sparse_vectors = new Dictionary<string, object>
            {
                ["sparse"] = new { index = new { on_disk = false } }
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

    // Drops and recreates the collection with the current schema.
    // Call this endpoint when migrating from unnamed → named vectors.
    // All data will be lost — re-ingest is required afterward.
    public async Task ForceRecreateCollectionAsync()
    {
        _logger.LogWarning(
            "COLLECTION_RECREATE dropping collection={CollectionName}", CollectionName);

        var deleteResponse = await _httpClient.DeleteAsync(
            $"{BaseUrl}/collections/{CollectionName}");

        if (!deleteResponse.IsSuccessStatusCode &&
            deleteResponse.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning(
                "COLLECTION_DELETE status={Status}", deleteResponse.StatusCode);
        }

        await EnsureCollectionAsync();

        _logger.LogInformation(
            "COLLECTION_RECREATE done, collection={CollectionName}", CollectionName);
    }

    private async Task EnsurePayloadIndexesAsync()
    {
        // System fields that are filtered/indexed regardless of dataset, plus every
        // schema field flagged indexed. Same set as the original hardcoded list,
        // now derived from the schema.
        var systemIndexes = new[] { "recordType", "sectionTitle", "department", "nameNormalized" };
        var indexes = systemIndexes
            .Concat(_schema.IndexedFieldKeys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
