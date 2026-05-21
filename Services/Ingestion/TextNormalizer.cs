using System.Text.RegularExpressions;

namespace be_service.Services;

public class TextNormalizer
{
    public string Normalize(string text)
    {
        text = text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");

        text = Regex.Replace(
            text,
            @"(?<!^)(?<!\n)(\d+\.\s*[A-Z])",
            "\n\n$1"
        );

        text = Regex.Replace(
            text,
            @"RU\s*6-",
            "RU6-"
        );

        text = Regex.Replace(
            text,
            @"\b(HSSE)(Analyst|Manager|Engineer|Operator|Staff|Coordinator|Supervisor)\b",
            "$1 $2"
        );

        text = Regex.Replace(
            text,
            @"\b([ABC])\s*(Tetap|Kontrak)\b",
            "$1 $2"
        );

        text = NormalizeProfileSection(text);
        text = NormalizeEmployeeTable(text);
        text = NormalizeOvertimeTable(text);
        text = NormalizeMaintenanceTable(text);

        text = Regex.Replace(
            text,
            @"\n{3,}",
            "\n\n"
        );

        return text.Trim();
    }

    private static string NormalizeProfileSection(string text)
    {
        if (text.Contains("Profil Perusahaan:\nField:", StringComparison.OrdinalIgnoreCase))
            return text;

        var match = Regex.Match(
            text,
            @"1\.\s*Profil Perusahaan\s+(.*?)(?=\s*2\.\s*Data Karyawan Internal)",
            RegexOptions.Singleline |
            RegexOptions.IgnoreCase
        );

        if (!match.Success)
            return text;

        var profileText = match.Groups[1].Value;

        var fields = new[]
        {
            "Nama Unit",
            "Lokasi",
            "Bidang",
            "Jumlah Karyawan",
            "Jumlah Shift Operasional",
            "Jam Operasional",
            "Direktur Operasi",
            "Kapasitas Produksi"
        };

        var normalizedChunks = new List<string>
        {
            "1. Profil Perusahaan"
        };

        foreach (var field in fields)
        {
            var value = ExtractProfileValue(profileText, field, fields);

            if (!string.IsNullOrWhiteSpace(value))
            {
                normalizedChunks.Add($@"Profil Perusahaan:
Field: {field}
Value: {value}");
            }
        }

        var normalizedProfile = string.Join("\n\n", normalizedChunks);

        return text.Remove(match.Index, match.Length)
            .Insert(match.Index, normalizedProfile);
    }

    private static string NormalizeEmployeeTable(string text)
    {
        var divisions = "IT & Digitalisasi|Operasional Kilang|Human Capital|Maintenance|Distribusi|Keuangan|Security|HSSE";
        var positions = "Supervisor|Operator|Manager|Coordinator|Analyst|Engineer|Staff";

        var pattern =
            $@"(RU6-\d{{4}})\s+" +
            $@"([A-Za-z\s]+?)\s+" +
            $@"({divisions})\s+" +
            $@"({positions})\s+" +
            $@"([ABC])\s+" +
            $@"(Tetap|Kontrak)";

        return Regex.Replace(
            text,
            pattern,
            match =>
            {
                return $@"

            Data Karyawan:
            NIK: {match.Groups[1].Value}
            Nama: {match.Groups[2].Value.Trim()}
            Divisi: {match.Groups[3].Value}
            Jabatan: {match.Groups[4].Value}
            Shift: {match.Groups[5].Value}
            Status: {match.Groups[6].Value}
            ";
            }
        );
    }

    private static string NormalizeOvertimeTable(string text)
    {
        var divisions = "IT & Digitalisasi|Operasional Kilang|Human Capital|Maintenance|Distribusi|Keuangan|Security|HSSE";
        var approvals = "Disetujui|Ditolak|Pending";

        var pattern =
            $@"(\d{{2}}-\d{{2}}-\d{{4}})\s+" +
            $@"([A-Za-z\s]+?)\s+" +
            $@"({divisions})\s+" +
            $@"(\d+\s+Jam)\s+" +
            $@"({approvals})";

        return Regex.Replace(
            text,
            pattern,
            match =>
            {
                return $@"

            Rekap Lembur:
            Tanggal: {match.Groups[1].Value}
            Nama: {match.Groups[2].Value.Trim()}
            Divisi: {match.Groups[3].Value}
            Durasi: {match.Groups[4].Value}
            Approval: {match.Groups[5].Value}
            ";
            }
        );
    }

    private static string NormalizeMaintenanceTable(string text)
    {
        var equipments = "Generator Cadangan|Sensor Gas|Pompa Tekanan|Compressor|Valve Control|CCTV Thermal|Boiler Unit";
        var locations = "Gate Utama|Utility Plant|Area Produksi B|Area Tanki A|Warehouse";
        var statuses = "Normal|Perbaikan|Maintenance Berkala";

        var pattern =
            $@"(MT-\d{{3}})\s+" +
            $@"({equipments})\s+" +
            $@"({locations})\s+" +
            $@"({statuses})\s+" +
            $@"([A-Za-z\s]+?)(?=\s+MT-\d{{3}}|\s*$)";

        return Regex.Replace(
            text,
            pattern,
            match =>
            {
                return $@"

            Log Maintenance:
            Kode: {match.Groups[1].Value}
            Peralatan: {match.Groups[2].Value}
            Lokasi: {match.Groups[3].Value}
            Status: {match.Groups[4].Value}
            Teknisi: {match.Groups[5].Value.Trim()}
            ";
            }
        );
    }

    private static string ExtractProfileValue(
        string section,
        string field,
        string[] allFields)
    {
        var nextFields = string.Join(
            "|",
            allFields
                .Where(x => !x.Equals(field, StringComparison.OrdinalIgnoreCase))
                .Select(Regex.Escape));

        var match = Regex.Match(
            section,
            $@"\b{Regex.Escape(field)}\b\s*:?\s*(.+?)(?=\s+(?:{nextFields})\b\s*:?\s*|$)",
            RegexOptions.IgnoreCase |
            RegexOptions.Singleline);

        if (!match.Success)
            return "";

        return Regex
            .Replace(match.Groups[1].Value, @"\s+", " ")
            .Trim();
    }
}
