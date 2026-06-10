using System.Text.RegularExpressions;

namespace be_service.Services;

public static class ChunkMetadataExtractor
{
    // Generic heading detection (non-dummy documents):
    //   "SECTION 8 - FAQ dan Kasus Uji Retrieval"
    //   "2.4 Jadwal Backup NusaCloud" / "8.2 Pertanyaan ..." / "1.1 Judul"
    //   "BAB I Judul" / "BAB II Judul"
    // Multiline form is used by the chunker to locate split points; the
    // single-line form tests whether content STARTS with a heading.
    private const string GenericHeadingBody =
        @"(?:SECTION\s+\d+\s*[-–—].*)" +
        @"|(?:BAB\s+[IVXLCDM0-9]+\b.*)" +
        @"|(?:\d+\.\d+(?:\.\d+)?\s+\S.*)";

    public static readonly Regex GenericHeadingRegex = new(
        $@"^[ \t]*(?:{GenericHeadingBody})$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SingleLineHeadingRegex = new(
        $@"^[ \t]*(?:{GenericHeadingBody})$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string NormalizeNik(string value)
    {
        var normalized = Regex.Replace(value, @"\s+", "").ToUpperInvariant();

        if (normalized.Contains('-'))
            return normalized;

        var match = Regex.Match(normalized, @"^([A-Z]+[0-9])([0-9]+)$");

        return match.Success
            ? $"{match.Groups[1].Value}-{match.Groups[2].Value}"
            : normalized;
    }

    public static string NormalizeMaintenanceCode(string value)
    {
        var normalized = Regex.Replace(value, @"\s+", "").ToUpperInvariant();

        if (normalized.Contains('-'))
            return normalized;

        var match = Regex.Match(normalized, @"^([A-Z]{2,4})([0-9]+)$");

        return match.Success
            ? $"{match.Groups[1].Value}-{match.Groups[2].Value}"
            : normalized;
    }

    // Classifies a chunk by record type. Conservative for generic documents:
    // only the dummy dataset's specific structured markers / section headings
    // map to a domain type; everything else is "document". This prevents a
    // generic doc from being misread as "audit"/"sop" just because it mentions
    // a word like "backup", "audit", "logbook", or "keamanan".
    public static string DetectRecordType(string content)
    {
        if (content.Contains("Data Karyawan:", StringComparison.OrdinalIgnoreCase))
            return "employee";

        if (content.Contains("Rekap Lembur:", StringComparison.OrdinalIgnoreCase))
            return "overtime";

        if (content.Contains("Log Maintenance:", StringComparison.OrdinalIgnoreCase))
            return "maintenance";

        if (content.Contains("Profil Perusahaan:", StringComparison.OrdinalIgnoreCase))
            return "profile";

        // Tightened: require the dummy SOP heading, not bare "SOP" anywhere.
        if (content.Contains("SOP Keamanan", StringComparison.OrdinalIgnoreCase))
            return "sop";

        // Tightened: require the dummy audit section heading. The previous loose
        // triggers ("Audit internal", "backup otomatis", "logbook") caused
        // generic documents to be misclassified as audit.
        if (content.Contains("Catatan Audit", StringComparison.OrdinalIgnoreCase))
            return "audit";

        return "document";
    }

    // Content-based fallback for chunkType when provenance is not available
    // (e.g. the QdrantPointWriter Guid overload). The ingestion pipeline sets
    // the precise chunkType from the chunker instead.
    public static string DetectChunkType(string content)
    {
        if (content.Contains("Data Karyawan:", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Rekap Lembur:", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Log Maintenance:", StringComparison.OrdinalIgnoreCase))
            return "structured_row";

        if (content.Contains("Profil Perusahaan:", StringComparison.OrdinalIgnoreCase))
            return "structured_fact";

        return "narrative_section";
    }

    // Returns the leading generic heading if content STARTS with one, else "".
    public static string TryExtractGenericHeading(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return "";

        var firstLine = content
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0) ?? "";

        if (firstLine.Length == 0)
            return "";

        return SingleLineHeadingRegex.IsMatch(firstLine)
            ? Regex.Replace(firstLine, @"\s+", " ").Trim()
            : "";
    }

    public static string ExtractNik(string content)
    {
        return ExtractByPattern(content, @"NIK:\s*(RU6-\d{4})");
    }

    public static string ExtractName(string content)
    {
        return ExtractByLabel(content, "Nama");
    }

    public static string ExtractDivision(string content)
    {
        return ExtractByLabel(content, "Divisi");
    }

    public static string ExtractPosition(string content)
    {
        return ExtractByLabel(content, "Jabatan");
    }

    public static string ExtractShift(string content)
    {
        return ExtractByLabel(content, "Shift");
    }

    public static string ExtractEmployeeStatus(string content)
    {
        return ExtractByLabel(content, "Status");
    }

    public static string ExtractDate(string content)
    {
        return ExtractByLabel(content, "Tanggal");
    }

    public static string ExtractDuration(string content)
    {
        return ExtractByLabel(content, "Durasi");
    }

    public static string ExtractApproval(string content)
    {
        return ExtractByLabel(content, "Approval");
    }

    public static string ExtractMaintenanceCode(string content)
    {
        return ExtractByPattern(content, @"Kode:\s*(MT-\d{3})");
    }

    public static string ExtractEquipment(string content)
    {
        return ExtractByLabel(content, "Peralatan");
    }

    public static string ExtractLocation(string content)
    {
        return ExtractByLabel(content, "Lokasi");
    }

    public static string ExtractMaintenanceStatus(string content)
    {
        return ExtractByLabel(content, "Status");
    }

    public static string ExtractTechnician(string content)
    {
        return ExtractByLabel(content, "Teknisi");
    }

    public static string ExtractSectionTitle(string content)
    {
        if (content.Contains("Data Karyawan:", StringComparison.OrdinalIgnoreCase))
            return "Data Karyawan";

        if (content.Contains("Rekap Lembur:", StringComparison.OrdinalIgnoreCase))
            return "Rekap Lembur";

        if (content.Contains("Log Maintenance:", StringComparison.OrdinalIgnoreCase))
            return "Log Maintenance";

        if (content.Contains("Profil Perusahaan:", StringComparison.OrdinalIgnoreCase))
            return "Profil Perusahaan";

        if (content.Contains("SOP Keamanan", StringComparison.OrdinalIgnoreCase))
            return "SOP Keamanan Area Kilang";

        if (content.Contains("Catatan Audit", StringComparison.OrdinalIgnoreCase))
            return "Catatan Audit dan Keamanan";

        // Generic documents: if the chunk starts with a recognizable heading
        // (SECTION N - ..., N.N ..., BAB ...), use it as the section title.
        return TryExtractGenericHeading(content);
    }

    private static string ExtractByLabel(string content, string label)
    {
        var match = Regex.Match(
            content,
            $@"(?im)^\s*{Regex.Escape(label)}:\s*(.+?)\s*$",
            RegexOptions.IgnoreCase
        );

        return match.Success
            ? match.Groups[1].Value.Trim()
            : "";
    }

    private static string ExtractByPattern(string content, string pattern)
    {
        var match = Regex.Match(
            content,
            pattern,
            RegexOptions.IgnoreCase
        );

        return match.Success
            ? match.Groups[1].Value.Trim()
            : "";
    }
}
