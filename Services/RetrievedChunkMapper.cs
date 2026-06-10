using System.Text.Json;
using be_service.Models;

namespace be_service.Services;

public static class RetrievedChunkMapper
{
    public static RetrievedChunk Map(JsonElement item, float similarity)
    {
        var payload = item.TryGetProperty("payload", out var payloadElement) &&
                      payloadElement.ValueKind == JsonValueKind.Object
            ? payloadElement
            : default;

        return new RetrievedChunk
        {
            Id = GetPointId(item),
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
            ChunkType = GetPayloadString(payload, "chunkType"),
            ChunkIndex = GetPayloadInt(payload, "chunkIndex")
        };
    }

    public static string GetPayloadString(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return "";

        return payload.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
    }

    private static int? GetPayloadInt(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;

        if (!payload.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var intValue))
            return intValue;

        return null;
    }

    private static Guid GetPointId(JsonElement item)
    {
        var id = item.GetProperty("id");

        return id.ValueKind == JsonValueKind.String
            ? Guid.Parse(id.GetString()!)
            : Guid.Parse(id.GetRawText());
    }
}
