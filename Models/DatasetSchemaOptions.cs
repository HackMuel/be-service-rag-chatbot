namespace be_service.Models;

// Config-driven description of how each recordType is chunked and which payload
// fields are extracted/indexed. Bound from appsettings section "DatasetSchema";
// falls back to Default() (the original Pertamina dummy schema) when not provided,
// so behavior is preserved on a fresh checkout without appsettings.
public class DatasetSchemaOptions
{
    public List<RecordTypeSchema> RecordTypes { get; set; } = new();

    public RecordTypeSchema? Find(string recordType) =>
        RecordTypes.FirstOrDefault(r =>
            string.Equals(r.Name, recordType, StringComparison.OrdinalIgnoreCase));

    // Section headers that drive the top-level section split (non-null only).
    public IEnumerable<string> SectionHeaders =>
        RecordTypes
            .Where(r => !string.IsNullOrWhiteSpace(r.SectionHeader))
            .Select(r => r.SectionHeader!);

    // Every field flagged indexed across all recordTypes (deduplicated).
    public IEnumerable<string> IndexedFieldKeys =>
        RecordTypes
            .SelectMany(r => r.Fields)
            .Where(f => f.Indexed)
            .Select(f => f.Key)
            .Distinct(StringComparer.OrdinalIgnoreCase);

    // The original hardcoded dummy dataset, expressed as schema. Used as the
    // built-in default so ingestion behaves identically when no config is given.
    public static DatasetSchemaOptions Default() => new()
    {
        RecordTypes = new List<RecordTypeSchema>
        {
            new()
            {
                Name = "profile",
                SectionHeader = "Profil Perusahaan",
                // Field/Value pairing handled by ChunkingService.SplitProfileSection (kept hardcoded).
                Fields = new()
            },
            new()
            {
                Name = "employee",
                SectionHeader = "Data Karyawan Internal",
                RecordDelimiter = @"Data Karyawan:\s*NIK:",
                Fields = new()
                {
                    new() { Key = "nik",            Pattern = @"NIK:\s*(RU6-\d{4})", Indexed = true },
                    new() { Key = "name",           Label = "Nama",    Indexed = false },
                    new() { Key = "division",       Label = "Divisi",  Indexed = true },
                    new() { Key = "position",       Label = "Jabatan", Indexed = true },
                    new() { Key = "shift",          Label = "Shift",   Indexed = true },
                    new() { Key = "employeeStatus", Label = "Status",  Indexed = true },
                }
            },
            new()
            {
                Name = "overtime",
                SectionHeader = "Rekap Lembur Karyawan",
                RecordDelimiter = @"Rekap Lembur:\s*Tanggal:",
                Fields = new()
                {
                    new() { Key = "date",     Label = "Tanggal",  Indexed = true },
                    new() { Key = "name",     Label = "Nama",     Indexed = false },
                    new() { Key = "division", Label = "Divisi",   Indexed = true },
                    new() { Key = "duration", Label = "Durasi",   Indexed = true },
                    new() { Key = "approval", Label = "Approval", Indexed = true },
                }
            },
            new()
            {
                Name = "maintenance",
                SectionHeader = "Log Maintenance Peralatan",
                RecordDelimiter = @"Log Maintenance:\s*Kode:",
                Fields = new()
                {
                    new() { Key = "maintenanceCode",   Pattern = @"Kode:\s*(MT-\d{3})", Indexed = true },
                    new() { Key = "equipment",         Label = "Peralatan", Indexed = true },
                    new() { Key = "location",          Label = "Lokasi",    Indexed = true },
                    new() { Key = "maintenanceStatus", Label = "Status",    Indexed = true },
                    new() { Key = "technician",        Label = "Teknisi",   Indexed = true },
                }
            },
            new() { Name = "sop",      SectionHeader = "SOP Keamanan Area Kilang",   Fields = new() },
            new() { Name = "audit",    SectionHeader = "Catatan Audit dan Keamanan", Fields = new() },
            new() { Name = "document", SectionHeader = null,                         Fields = new() },
        }
    };
}

public class RecordTypeSchema
{
    public string Name { get; set; } = string.Empty;

    // Section title used for the top-level document split (e.g. "Data Karyawan
    // Internal"). The split tolerates a leading "N. " number prefix.
    public string? SectionHeader { get; set; }

    // Regex that marks the start of a single structured record inside a section
    // (e.g. "Data Karyawan:\\s*NIK:"). Null for narrative recordTypes.
    public string? RecordDelimiter { get; set; }

    public List<DatasetField> Fields { get; set; } = new();
}

public class DatasetField
{
    public string Key { get; set; } = string.Empty;

    // Either Label (extracted via "Label: value") or Pattern (regex with one
    // capture group) — exactly one is set.
    public string? Label { get; set; }
    public string? Pattern { get; set; }

    public bool Indexed { get; set; }
}
