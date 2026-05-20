namespace be_service.Models;

public class RetrievedChunk
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    public string DocumentTitle { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public float Similarity { get; set; }
    public string RecordType { get; set; } = string.Empty;
    public string Nik { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string MaintenanceCode { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;
    public string Division { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Position { get; set; } = string.Empty;
    public string Shift { get; set; } = string.Empty;
    public string EmployeeStatus { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
    public string Approval { get; set; } = string.Empty;
    public string Equipment { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public string MaintenanceStatus { get; set; } = string.Empty;
    public string Technician { get; set; } = string.Empty;
    public string SectionTitle { get; set; } = string.Empty;
    public int? ChunkIndex { get; set; }
}
