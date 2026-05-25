using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using be_service.Models;

namespace be_service.Services;

public class QdrantSearchClient
{
    private readonly HttpClient _httpClient;
    private readonly QdrantScrollClient _scrollClient;
    private readonly QdrantFilterBuilder _filterBuilder;
    private readonly ILogger<QdrantSearchClient> _logger;

    public QdrantSearchClient(
        QdrantScrollClient scrollClient,
        QdrantFilterBuilder filterBuilder,
        ILogger<QdrantSearchClient> logger)
    {
        _httpClient = new HttpClient();
        _scrollClient = scrollClient;
        _filterBuilder = filterBuilder;
        _logger = logger;
    }

    public async Task<List<RetrievedChunk>> SearchAsync(
        List<float> queryEmbedding,
        int limit = 10)
    {
        return await SearchSemanticAsync(queryEmbedding, limit);
    }

    public async Task<List<RetrievedChunk>> SearchSemanticAsync(
        List<float> queryEmbedding,
        int limit = 10)
    {
        var body = new
        {
            vector = queryEmbedding,
            limit,
            with_payload = true,
            with_vector = false
        };

        var response = await _httpClient.PostAsync(
            $"{QdrantConstants.BaseUrl}/collections/{QdrantConstants.CollectionName}/points/search",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        );

        await HttpResponseGuard.EnsureSuccessAsync(response, _logger, "Qdrant vector search");

        var resultJson = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(resultJson);

        return doc.RootElement
            .GetProperty("result")
            .EnumerateArray()
            .Select(item => RetrievedChunkMapper.Map(item, item.GetProperty("score").GetSingle()))
            .ToList();
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
}
