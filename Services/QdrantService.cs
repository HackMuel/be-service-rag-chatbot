using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using be_service.Models;

namespace be_service.Services;

public class QdrantService
{
    private readonly HttpClient _httpClient;
    private const string CollectionName = "pertamina_chunks";
    private const string BaseUrl = "http://localhost:6333";
    private const int LegacyKeywordPageSize = 256;

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

        if (response.StatusCode != System.Net.HttpStatusCode.Conflict)
        {
            response.EnsureSuccessStatusCode();
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
                var response = await _httpClient.PutAsync(
                    $"{BaseUrl}/collections/{CollectionName}/index",
                    new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
                );

                if (!response.IsSuccessStatusCode &&
                    response.StatusCode != System.Net.HttpStatusCode.Conflict)
                {
                    Console.WriteLine($"Qdrant payload index warning: {fieldName} returned {(int)response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Qdrant payload index warning: {fieldName} - {ex.Message}");
            }
        }
    }

    public async Task UpsertChunkAsync(
        Guid id,
        Guid documentId,
        string documentTitle,
        string content,
        List<float> embedding,
        int chunkIndex = -1,
        string department = "")
    {
        var recordType = ChunkMetadataExtractor.DetectRecordType(content);
        var nik = ChunkMetadataExtractor.ExtractNik(content);
        var name = ChunkMetadataExtractor.ExtractName(content);
        var maintenanceCode = ChunkMetadataExtractor.ExtractMaintenanceCode(content);
        var date = ChunkMetadataExtractor.ExtractDate(content);
        var division = ChunkMetadataExtractor.ExtractDivision(content);
        var position = ChunkMetadataExtractor.ExtractPosition(content);
        var shift = ChunkMetadataExtractor.ExtractShift(content);
        var employeeStatus = ChunkMetadataExtractor.ExtractEmployeeStatus(content);
        var duration = ChunkMetadataExtractor.ExtractDuration(content);
        var approval = ChunkMetadataExtractor.ExtractApproval(content);
        var equipment = ChunkMetadataExtractor.ExtractEquipment(content);
        var location = ChunkMetadataExtractor.ExtractLocation(content);
        var maintenanceStatus = ChunkMetadataExtractor.ExtractMaintenanceStatus(content);
        var technician = ChunkMetadataExtractor.ExtractTechnician(content);
        var sectionTitle = ChunkMetadataExtractor.ExtractSectionTitle(content);

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
                        content,
                        recordType,
                        nik,
                        name,
                        nameNormalized = NormalizeKeyword(name),
                        maintenanceCode,
                        date,
                        division,
                        department,
                        position,
                        shift,
                        employeeStatus,
                        duration,
                        approval,
                        equipment,
                        location,
                        maintenanceStatus,
                        technician,
                        sectionTitle,
                        chunkIndex = chunkIndex >= 0 ? chunkIndex : (int?)null
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

        return doc.RootElement
            .GetProperty("result")
            .EnumerateArray()
            .Select(item => MapRetrievedChunk(item, item.GetProperty("score").GetSingle()))
            .ToList();
    }

    public async Task<List<RetrievedChunk>> SearchByNikAsync(string nik)
    {
        return await SearchByPayloadMatchAsync("nik", 
        ChunkMetadataExtractor.NormalizeNik(nik), 5);
    }

    public async Task<List<RetrievedChunk>> SearchByMaintenanceCodeAsync(string code)
    {
        return await SearchByPayloadMatchAsync("maintenanceCode", 
        ChunkMetadataExtractor.NormalizeMaintenanceCode(code), 5);
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
        var must = filters
            .Where(x => !string.IsNullOrWhiteSpace(x.Key) &&
                        !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => new
            {
                key = x.Key,
                match = new
                {
                    value = x.Value
                }
            })
            .ToArray();

        if (!must.Any())
        {
            return new List<RetrievedChunk>();
        }

        var filter = new
        {
            must
        };

        return await ScrollByFilterAsync(filter, "", limit);
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

    public async Task<List<RetrievedChunk>> SearchByRecordTypeAsync(
        string recordType,
        string keyword,
        int limit = 10)
    {
        var filter = new
        {
            must = new[]
            {
                new
                {
                    key = "recordType",
                    match = new
                    {
                        value = NormalizeRecordType(recordType)
                    }
                }
            }
        };

        return await ScrollByFilterAsync(filter, keyword, limit);
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
                ["limit"] = LegacyKeywordPageSize,
                ["with_payload"] = true,
                ["with_vector"] = false
            };

            if (!string.IsNullOrWhiteSpace(offset))
            {
                body["offset"] = offset;
            }

            var response = await _httpClient.PostAsync(
                $"{BaseUrl}/collections/{CollectionName}/points/scroll",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            );

            response.EnsureSuccessStatusCode();

            var resultJson = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(resultJson);
            var result = doc.RootElement.GetProperty("result");

            foreach (var item in result.GetProperty("points").EnumerateArray())
            {
                var payload = item.GetProperty("payload");
                var content = GetPayloadString(payload, "content");

                if (!content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    continue;

                results.Add(MapRetrievedChunk(item, 1.0f));

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

    private async Task<List<RetrievedChunk>> ScrollByFilterAsync(
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
                ["limit"] = LegacyKeywordPageSize,
                ["with_payload"] = true,
                ["with_vector"] = false,
                ["filter"] = filter
            };

            if (!string.IsNullOrWhiteSpace(offset))
            {
                body["offset"] = offset;
            }

            var response = await _httpClient.PostAsync(
                $"{BaseUrl}/collections/{CollectionName}/points/scroll",
                new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            );

            response.EnsureSuccessStatusCode();

            var resultJson = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(resultJson);
            var result = doc.RootElement.GetProperty("result");

            foreach (var item in result.GetProperty("points").EnumerateArray())
            {
                var payload = item.GetProperty("payload");
                var content = GetPayloadString(payload, "content");

                if (!MatchesKeyword(content, keyword))
                    continue;

                results.Add(MapRetrievedChunk(item, 1.0f));

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

    public static string NormalizeKeyword(string value)
    {
        return Regex.Replace(value.Trim(), @"\s+", " ").ToUpperInvariant();
    }

    private static string NormalizeRecordType(string recordType)
    {
        return recordType.Trim().ToLowerInvariant();
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

    private static RetrievedChunk MapRetrievedChunk(JsonElement item, float similarity)
    {
        var payload = item.GetProperty("payload");

        return new RetrievedChunk
        {
            Id = Guid.Parse(item.GetProperty("id").GetString()!),
            DocumentId = Guid.TryParse(GetPayloadString(payload, "documentId"), out var documentId)
                ? documentId
                : Guid.Empty,
            DocumentTitle = GetPayloadString(payload, "documentTitle"),
            Content = GetPayloadString(payload, "content"),
            Similarity = similarity,
            RecordType = GetPayloadString(payload, "recordType"),
            Nik = GetPayloadString(payload, "nik"),
            Name = GetPayloadString(payload, "name"),
            MaintenanceCode = GetPayloadString(payload, "maintenanceCode"),
            Date = GetPayloadString(payload, "date"),
            Division = GetPayloadString(payload, "division"),
            Department = GetPayloadString(payload, "department"),
            Position = GetPayloadString(payload, "position"),
            Shift = GetPayloadString(payload, "shift"),
            EmployeeStatus = GetPayloadString(payload, "employeeStatus"),
            Duration = GetPayloadString(payload, "duration"),
            Approval = GetPayloadString(payload, "approval"),
            Equipment = GetPayloadString(payload, "equipment"),
            Location = GetPayloadString(payload, "location"),
            MaintenanceStatus = GetPayloadString(payload, "maintenanceStatus"),
            Technician = GetPayloadString(payload, "technician"),
            SectionTitle = GetPayloadString(payload, "sectionTitle"),
            ChunkIndex = GetPayloadInt(payload, "chunkIndex")
        };
    }

    private static string GetPayloadString(JsonElement payload, string propertyName)
    {
        return payload.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static int? GetPayloadInt(JsonElement payload, string propertyName)
    {
        if (!payload.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
            return intValue;

        return null;
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
}
