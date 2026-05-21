using System.Text;
using System.Text.Json;

namespace be_service.Services;

public class QdrantPointWriter
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<QdrantPointWriter> _logger;

    public QdrantPointWriter(ILogger<QdrantPointWriter> logger)
    {
        _httpClient = new HttpClient();
        _logger = logger;
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
                        nameNormalized = QdrantSearchClient.NormalizeKeyword(name),
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

        var response = await _httpClient.PutAsync(
            $"{QdrantConstants.BaseUrl}/collections/{QdrantConstants.CollectionName}/points",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        );

        await HttpResponseGuard.EnsureSuccessAsync(response, _logger, "Qdrant point upsert");
    }
}
