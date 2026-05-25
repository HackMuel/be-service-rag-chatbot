using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using be_service.Models;

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
            ChunkIndex = chunkIndex
        };

        await UpsertChunkAsync(chunk, embedding);
    }

    public async Task UpsertChunkAsync(
        RetrievedChunk chunk,
        List<float> embedding)
    {
        var body = new
        {
            points = new[]
            {
                new
                {
                    id = chunk.Id.ToString(),
                    vector = embedding,
                    payload = BuildPayload(chunk)
                }
            }
        };

        var response = await _httpClient.PutAsync(
            $"{QdrantConstants.BaseUrl}/collections/{QdrantConstants.CollectionName}/points",
            new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        );

        await HttpResponseGuard.EnsureSuccessAsync(response, _logger, "Qdrant point upsert");
    }

    private static object BuildPayload(RetrievedChunk chunk)
    {
        return new
        {
            documentId = chunk.DocumentId.ToString(),
            documentTitle = chunk.DocumentTitle,
            content = chunk.Content,
            recordType = chunk.RecordType,
            nik = chunk.Nik,
            name = chunk.Name,
            nameNormalized = NormalizeKeyword(chunk.Name),
            maintenanceCode = chunk.MaintenanceCode,
            date = chunk.Date,
            division = chunk.Division,
            department = chunk.Department,
            position = chunk.Position,
            shift = chunk.Shift,
            employeeStatus = chunk.EmployeeStatus,
            duration = chunk.Duration,
            approval = chunk.Approval,
            equipment = chunk.Equipment,
            location = chunk.Location,
            maintenanceStatus = chunk.MaintenanceStatus,
            technician = chunk.Technician,
            sectionTitle = chunk.SectionTitle,
            chunkIndex = chunk.ChunkIndex ?? -1
        };
    }

    private static string NormalizeKeyword(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return Regex.Replace(value.Trim(), @"\s+", " ").ToUpperInvariant();
    }
}
