using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using be_service.Models;
using Microsoft.Extensions.Options;

namespace be_service.Services;

public class QdrantPointWriter
{
    private readonly HttpClient _httpClient;
    private readonly QdrantOptions _options;
    private readonly ILogger<QdrantPointWriter> _logger;

    public QdrantPointWriter(
        IOptions<QdrantOptions> options,
        ILogger<QdrantPointWriter> logger)
    {
        _httpClient = new HttpClient();
        _options = options.Value;
        _logger = logger;
    }

    public async Task UpsertChunkAsync(
        Guid id,
        Guid documentId,
        string documentTitle,
        string content,
        List<float> embedding,
        int chunkIndex = -1,
        string department = "",
        Dictionary<uint, float>? sparseVector = null)
    {
        var chunk = new RetrievedChunk
        {
            Id = id,
            DocumentId = documentId,
            DocumentTitle = documentTitle,
            Content = content,
            RecordType = ChunkMetadataExtractor.DetectRecordType(content),
            Nik = ChunkMetadataExtractor.ExtractNik(content),
            Name = ChunkMetadataExtractor.ExtractName(content),
            MaintenanceCode = ChunkMetadataExtractor.ExtractMaintenanceCode(content),
            Date = ChunkMetadataExtractor.ExtractDate(content),
            Division = ChunkMetadataExtractor.ExtractDivision(content),
            Department = department,
            Position = ChunkMetadataExtractor.ExtractPosition(content),
            Shift = ChunkMetadataExtractor.ExtractShift(content),
            EmployeeStatus = ChunkMetadataExtractor.ExtractEmployeeStatus(content),
            Duration = ChunkMetadataExtractor.ExtractDuration(content),
            Approval = ChunkMetadataExtractor.ExtractApproval(content),
            Equipment = ChunkMetadataExtractor.ExtractEquipment(content),
            Location = ChunkMetadataExtractor.ExtractLocation(content),
            MaintenanceStatus = ChunkMetadataExtractor.ExtractMaintenanceStatus(content),
            Technician = ChunkMetadataExtractor.ExtractTechnician(content),
            SectionTitle = ChunkMetadataExtractor.ExtractSectionTitle(content),
            ChunkType = ChunkMetadataExtractor.DetectChunkType(content),
            ChunkIndex = chunkIndex
        };

        await UpsertChunkAsync(chunk, embedding, sparseVector);
    }

    public async Task UpsertChunkAsync(
        RetrievedChunk chunk,
        List<float> denseEmbedding,
        Dictionary<uint, float>? sparseVector = null)
    {
        var vectorPayload = BuildVectorPayload(denseEmbedding, sparseVector);

        var body = new
        {
            points = new[]
            {
                new
                {
                    id = chunk.Id.ToString(),
                    vector = vectorPayload,
                    payload = BuildPayload(chunk)
                }
            }
        };

        var response = await HttpResponseGuard.SendAsync(
            () => _httpClient.PutAsync(
                $"{BaseUrl}/collections/{CollectionName}/points",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")),
            _logger,
            "Qdrant point upsert",
            BaseUrl
        );

        await HttpResponseGuard.EnsureSuccessAsync(
            response,
            _logger,
            "Qdrant point upsert",
            BaseUrl);
    }

    // Deletes all points for a given documentId via payload filter.
    // Used before re-ingesting a document to avoid duplicates.
    public async Task DeleteByDocumentIdAsync(Guid documentId)
    {
        var body = new
        {
            filter = new
            {
                must = new[]
                {
                    new { key = "documentId", match = new { value = documentId.ToString() } }
                }
            }
        };

        var response = await HttpResponseGuard.SendAsync(
            () => _httpClient.PostAsync(
                $"{BaseUrl}/collections/{CollectionName}/points/delete",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")),
            _logger,
            "Qdrant delete by documentId",
            BaseUrl
        );

        await HttpResponseGuard.EnsureSuccessAsync(
            response,
            _logger,
            "Qdrant delete by documentId",
            BaseUrl);
    }

    private static object BuildVectorPayload(
        List<float> dense,
        Dictionary<uint, float>? sparse)
    {
        if (sparse is { Count: > 0 })
        {
            var sorted = sparse.OrderBy(kv => kv.Key).ToList();
            return new
            {
                dense,
                sparse = new
                {
                    indices = sorted.Select(kv => (int)kv.Key).ToArray(),
                    values  = sorted.Select(kv => kv.Value).ToArray()
                }
            };
        }

        return new { dense };
    }

    // Two-layer payload: system fields (always) + the recordType's dataset fields
    // (flat, only the fields that belong to this chunk's recordType). nameNormalized
    // is derived from the dataset "name" field when present.
    private static Dictionary<string, object> BuildPayload(RetrievedChunk chunk)
    {
        var payload = new Dictionary<string, object>
        {
            ["documentId"] = chunk.DocumentId.ToString(),
            ["documentTitle"] = chunk.DocumentTitle,
            ["content"] = chunk.Content,
            ["recordType"] = chunk.RecordType,
            ["department"] = chunk.Department,
            ["sectionTitle"] = chunk.SectionTitle,
            ["chunkType"] = chunk.ChunkType,
            ["chunkIndex"] = chunk.ChunkIndex ?? -1,
            ["ingestedAt"] = DateTime.UtcNow.ToString("o")
        };

        foreach (var field in chunk.DatasetFields)
            payload[field.Key] = field.Value;

        if (chunk.DatasetFields.TryGetValue("name", out var name) &&
            !string.IsNullOrWhiteSpace(name))
        {
            payload["nameNormalized"] = NormalizeKeyword(name);
        }

        return payload;
    }

    private static string NormalizeKeyword(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return Regex.Replace(value.Trim(), @"\s+", " ").ToUpperInvariant();
    }

    private string BaseUrl => string.IsNullOrWhiteSpace(_options.BaseUrl)
        ? QdrantOptions.DefaultBaseUrl
        : _options.BaseUrl.TrimEnd('/');

    private string CollectionName => string.IsNullOrWhiteSpace(_options.CollectionName)
        ? QdrantOptions.DefaultCollectionName
        : _options.CollectionName.Trim();
}
