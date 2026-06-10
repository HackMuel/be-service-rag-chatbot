using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using be_service.Models;
using Microsoft.Extensions.Options;
using Qdrant.Client;
using Qdrant.Client.Grpc;

namespace be_service.Services;

public class QdrantSearchClient
{
    private readonly HttpClient _httpClient;
    private readonly QdrantClient _qdrantClient;
    private readonly QdrantScrollClient _scrollClient;
    private readonly QdrantFilterBuilder _filterBuilder;
    private readonly QdrantOptions _options;
    private readonly ILogger<QdrantSearchClient> _logger;

    public QdrantSearchClient(
        QdrantClient qdrantClient,
        QdrantScrollClient scrollClient,
        QdrantFilterBuilder filterBuilder,
        IOptions<QdrantOptions> options,
        ILogger<QdrantSearchClient> logger)
    {
        _httpClient = new HttpClient();
        _qdrantClient = qdrantClient;
        _scrollClient = scrollClient;
        _filterBuilder = filterBuilder;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<List<RetrievedChunk>> SearchAsync(
        List<float> queryEmbedding,
        int limit = 10)
    {
        return await SearchSemanticAsync(queryEmbedding, limit);
    }

    // Dense-only or hybrid search depending on whether sparseVector is provided.
    public async Task<List<RetrievedChunk>> SearchSemanticAsync(
        List<float> queryEmbedding,
        int limit = 10,
        Dictionary<uint, float>? sparseVector = null)
    {
        if (sparseVector is { Count: > 0 })
            return await SearchHybridAsync(queryEmbedding, sparseVector, limit);

        return await SearchDenseAsync(queryEmbedding, limit);
    }

    // Dense-only search via Qdrant.Client SDK using named "dense" vector.
    // Falls back to the legacy unnamed-vector HTTP search if the collection was not
    // yet migrated (i.e., POST /api/qdrant/recreate has not been called).
    private async Task<List<RetrievedChunk>> SearchDenseAsync(
        List<float> queryEmbedding,
        int limit)
    {
        try
        {
            var results = await _qdrantClient.SearchAsync(
                collectionName: CollectionName,
                vector: queryEmbedding.ToArray(),
                vectorName: "dense",
                limit: (ulong)limit,
                payloadSelector: true);

            return results.Select(MapScoredPoint).ToList();
        }
        catch (Exception ex) when (
            ex.Message.Contains("Not existing vector name") ||
            ex.Message.Contains("dense"))
        {
            _logger.LogWarning(
                "SEARCH_SCHEMA_FALLBACK collection still uses unnamed vector schema. " +
                "Call POST /api/qdrant/recreate then re-ingest to enable named vectors.");

            return await SearchDenseLegacyAsync(queryEmbedding, limit);
        }
    }

    // Legacy fallback: unnamed single-vector search via raw HTTP.
    // Used when the collection has not yet been migrated to named vectors.
    private async Task<List<RetrievedChunk>> SearchDenseLegacyAsync(
        List<float> queryEmbedding,
        int limit)
    {
        var body = new
        {
            vector = queryEmbedding,
            limit,
            with_payload = true,
            with_vector = false
        };

        var response = await HttpResponseGuard.SendAsync(
            () => _httpClient.PostAsync(
                $"{BaseUrl}/collections/{CollectionName}/points/search",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")),
            _logger,
            "Qdrant vector search (legacy schema)",
            BaseUrl
        );

        await HttpResponseGuard.EnsureSuccessAsync(
            response,
            _logger,
            "Qdrant vector search (legacy schema)",
            BaseUrl);

        var resultJson = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(resultJson);

        return doc.RootElement
            .GetProperty("result")
            .EnumerateArray()
            .Select(item => RetrievedChunkMapper.Map(item, item.GetProperty("score").GetSingle()))
            .ToList();
    }

    // Hybrid search: dense + sparse prefetch with RRF fusion via Qdrant Query API.
    private async Task<List<RetrievedChunk>> SearchHybridAsync(
        List<float> denseEmbedding,
        Dictionary<uint, float> sparseVector,
        int limit)
    {
        var sorted = sparseVector.OrderBy(kv => kv.Key).ToList();
        var sparseValues  = sorted.Select(kv => kv.Value).ToArray();
        var sparseIndices = sorted.Select(kv => kv.Key).ToArray();

        var prefetches = new List<PrefetchQuery>
        {
            new PrefetchQuery
            {
                Query = denseEmbedding.ToArray(),   // float[] → VectorInput (implicit)
                Using = "dense",
                Limit = (ulong)(limit * 2)
            },
            new PrefetchQuery
            {
                Query = (sparseValues, sparseIndices), // (float[], uint[]) → VectorInput (implicit)
                Using = "sparse",
                Limit = (ulong)(limit * 2)
            }
        };

        var results = await _qdrantClient.QueryAsync(
            collectionName: CollectionName,
            query: Fusion.Rrf,          // Fusion → Query (implicit)
            prefetch: prefetches,
            limit: (ulong)limit,
            payloadSelector: true);

        return results.Select(MapScoredPoint).ToList();
    }

    // Maps a Qdrant.Client ScoredPoint to a RetrievedChunk.
    private static RetrievedChunk MapScoredPoint(ScoredPoint point)
    {
        return new RetrievedChunk
        {
            Id = Guid.TryParse(point.Id.Uuid, out var id) ? id : Guid.Empty,
            DocumentId = Guid.TryParse(GetStr(point, "documentId"), out var docId)
                ? docId
                : Guid.Empty,
            DocumentTitle  = GetStr(point, "documentTitle"),
            Content        = GetStr(point, "content"),
            Similarity     = point.Score,
            RecordType     = GetStr(point, "recordType"),
            Nik            = GetStr(point, "nik"),
            Name           = GetStr(point, "name"),
            MaintenanceCode = GetStr(point, "maintenanceCode"),
            Date           = GetStr(point, "date"),
            Division       = GetStr(point, "division"),
            Department     = GetStr(point, "department"),
            Position       = GetStr(point, "position"),
            Shift          = GetStr(point, "shift"),
            EmployeeStatus = GetStr(point, "employeeStatus"),
            Duration       = GetStr(point, "duration"),
            Approval       = GetStr(point, "approval"),
            Equipment      = GetStr(point, "equipment"),
            Location       = GetStr(point, "location"),
            MaintenanceStatus = GetStr(point, "maintenanceStatus"),
            Technician     = GetStr(point, "technician"),
            SectionTitle   = GetStr(point, "sectionTitle"),
            ChunkType      = GetStr(point, "chunkType"),
            ChunkIndex     = GetInt(point, "chunkIndex")
        };
    }

    private static string GetStr(ScoredPoint point, string key)
    {
        if (point.Payload.TryGetValue(key, out var v) &&
            v.KindCase == Value.KindOneofCase.StringValue)
            return v.StringValue;
        return "";
    }

    private static int? GetInt(ScoredPoint point, string key)
    {
        if (!point.Payload.TryGetValue(key, out var v)) return null;
        if (v.KindCase == Value.KindOneofCase.IntegerValue) return (int)v.IntegerValue;
        if (v.KindCase == Value.KindOneofCase.DoubleValue)  return (int)v.DoubleValue;
        return null;
    }

    public async Task<List<RetrievedChunk>> SearchByNikAsync(string nik)
    {
        return await SearchByPayloadMatchAsync(
            "nik",
            ChunkMetadataExtractor.NormalizeNik(nik),
            5);
    }

    public async Task<List<RetrievedChunk>> SearchByMaintenanceCodeAsync(string code)
    {
        return await SearchByPayloadMatchAsync(
            "maintenanceCode",
            ChunkMetadataExtractor.NormalizeMaintenanceCode(code),
            5);
    }

    public async Task<List<RetrievedChunk>> SearchByDateAsync(string date)
    {
        return await SearchByPayloadMatchAsync("date", date.Trim(), 10);
    }

    public async Task<List<RetrievedChunk>> SearchByNameAsync(string name, int limit = 10)
    {
        return await SearchByPayloadMatchAsync("nameNormalized", NormalizeKeyword(name), limit);
    }

    public async Task<List<RetrievedChunk>> SearchByPayloadFilterAsync(
        Dictionary<string, string> filters,
        int limit = 50)
    {
        if (!_filterBuilder.HasAnyCondition(filters))
        {
            return new List<RetrievedChunk>();
        }

        var filter = _filterBuilder.BuildPayloadFilter(filters);

        return await _scrollClient.ScrollByFilterAsync(filter, "", limit);
    }

    public async Task<List<StructuredEntityMatch>> GetKnownStructuredEntitiesAsync()
    {
        return await _scrollClient.GetKnownStructuredEntitiesAsync();
    }

    public async Task<List<RetrievedChunk>> SearchEmployeesByDivisionAsync(string division, int limit = 50)
    {
        return await SearchByPayloadFilterAsync(new Dictionary<string, string>
        {
            ["recordType"] = "employee",
            ["division"] = division
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchEmployeesByShiftAsync(string shift, int limit = 50)
    {
        return await SearchByPayloadFilterAsync(new Dictionary<string, string>
        {
            ["recordType"] = "employee",
            ["shift"] = shift
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchEmployeesByStatusAsync(string status, int limit = 50)
    {
        return await SearchByPayloadFilterAsync(new Dictionary<string, string>
        {
            ["recordType"] = "employee",
            ["employeeStatus"] = status
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchEmployeesByPositionAsync(string position, int limit = 50)
    {
        return await SearchByPayloadFilterAsync(new Dictionary<string, string>
        {
            ["recordType"] = "employee",
            ["position"] = position
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchOvertimeByApprovalAsync(string approval, int limit = 50)
    {
        return await SearchByPayloadFilterAsync(new Dictionary<string, string>
        {
            ["recordType"] = "overtime",
            ["approval"] = approval
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchOvertimeByDivisionAsync(string division, int limit = 50)
    {
        return await SearchByPayloadFilterAsync(new Dictionary<string, string>
        {
            ["recordType"] = "overtime",
            ["division"] = division
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchOvertimeByNameAsync(string name, int limit = 50)
    {
        return await SearchByPayloadFilterAsync(new Dictionary<string, string>
        {
            ["recordType"] = "overtime",
            ["nameNormalized"] = NormalizeKeyword(name)
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchMaintenanceByStatusAsync(string status, int limit = 50)
    {
        return await SearchByPayloadFilterAsync(new Dictionary<string, string>
        {
            ["recordType"] = "maintenance",
            ["maintenanceStatus"] = status
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchMaintenanceByLocationAsync(string location, int limit = 50)
    {
        return await SearchByPayloadFilterAsync(new Dictionary<string, string>
        {
            ["recordType"] = "maintenance",
            ["location"] = location
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchMaintenanceByTechnicianAsync(string technician, int limit = 50)
    {
        return await SearchByPayloadFilterAsync(new Dictionary<string, string>
        {
            ["recordType"] = "maintenance",
            ["technician"] = technician
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchMaintenanceByEquipmentAsync(string equipment, int limit = 50)
    {
        return await SearchByPayloadFilterAsync(new Dictionary<string, string>
        {
            ["recordType"] = "maintenance",
            ["equipment"] = equipment
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchByRecordTypeAsync(
        string recordType,
        string keyword,
        int limit = 10)
    {
        var filter = _filterBuilder.BuildPayloadFilter("recordType", NormalizeRecordType(recordType));

        return await _scrollClient.ScrollByFilterAsync(filter, keyword, limit);
    }

    public async Task<List<RetrievedChunk>> SearchByRecordTypeAsync(
        string recordType,
        int limit = 10)
    {
        return await SearchByRecordTypeAsync(recordType, "", limit);
    }

    public static string NormalizeKeyword(string value)
    {
        return Regex.Replace(value.Trim(), @"\s+", " ").ToUpperInvariant();
    }

    private async Task<List<RetrievedChunk>> SearchByPayloadMatchAsync(
        string fieldName,
        string value,
        int limit)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new List<RetrievedChunk>();
        }

        return await SearchByPayloadFilterAsync(new Dictionary<string, string>
        {
            [fieldName] = value
        }, limit);
    }

    private static string NormalizeRecordType(string recordType)
    {
        return recordType.Trim().ToLowerInvariant();
    }

    private string BaseUrl => string.IsNullOrWhiteSpace(_options.BaseUrl)
        ? QdrantOptions.DefaultBaseUrl
        : _options.BaseUrl.TrimEnd('/');

    private string CollectionName => string.IsNullOrWhiteSpace(_options.CollectionName)
        ? QdrantOptions.DefaultCollectionName
        : _options.CollectionName.Trim();
}
