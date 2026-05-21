using System.Text.RegularExpressions;

namespace be_service.Services;

public static class ChunkMetadataExtractor
{
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

        if (content.Contains("SOP", StringComparison.OrdinalIgnoreCase))
            return "sop";

        if (content.Contains("Catatan Audit", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("Audit internal", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("backup otomatis", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("logbook", StringComparison.OrdinalIgnoreCase))
            return "audit";

        return "document";
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

        if (content.Contains("Audit", StringComparison.OrdinalIgnoreCase))
            return "Catatan Audit dan Keamanan";

        return "";
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
