using System.Text.RegularExpressions;

namespace be_service.Services;

public class ChunkingService
{
    public List<string> SplitBySections(string text)
    {
        var pattern = @"(?=\n\n(?:1\. Profil Perusahaan|2\. Data Karyawan Internal|3\. SOP Keamanan Area Kilang|4\. Rekap Lembur Karyawan|5\. Log Maintenance Peralatan|6\. Catatan Audit dan Keamanan))";

        var sections = Regex
            .Split(text, pattern)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var chunks = new List<string>();

        foreach (var section in sections)
        {
            if (section.Contains("Profil Perusahaan", StringComparison.OrdinalIgnoreCase))
            {
                chunks.AddRange(SplitProfileSection(section));
            }
            else if (section.Contains("Data Karyawan:", StringComparison.OrdinalIgnoreCase))
            {
                var employeeChunks = Regex
                    .Split(section, @"(?=Data Karyawan:\s*NIK:)")
                    .Select(x => x.Trim())
                    .Where(x => x.StartsWith("Data Karyawan:"))
                    .ToList();

                chunks.AddRange(employeeChunks);
            }
            else if (section.Contains("Rekap Lembur:", StringComparison.OrdinalIgnoreCase))
            {
                var overtimeChunks = Regex
                    .Split(section, @"(?=Rekap Lembur:\s*Tanggal:)")
                    .Select(x => x.Trim())
                    .Where(x => x.StartsWith("Rekap Lembur:"))
                    .ToList();

                chunks.AddRange(overtimeChunks);
            }
            else if (section.Contains("Log Maintenance:", StringComparison.OrdinalIgnoreCase))
            {
                var maintenanceChunks = Regex
                    .Split(section, @"(?=Log Maintenance:\s*Kode:)")
                    .Select(x => x.Trim())
                    .Where(x => x.StartsWith("Log Maintenance:"))
                    .ToList();

                chunks.AddRange(maintenanceChunks);
            }
            else
            {
                chunks.Add(section);
            }
        }

        return chunks;
    }

    private static List<string> SplitProfileSection(string section)
    {
        var normalizedChunks = Regex
            .Split(section, @"(?=Profil Perusahaan:\s*Field:)")
            .Select(x => x.Trim())
            .Where(x => x.StartsWith("Profil Perusahaan:", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (normalizedChunks.Any())
        {
            return normalizedChunks;
        }

        var profileFields = new[]
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

        var chunks = new List<string>();

        foreach (var field in profileFields)
        {
            var value = ExtractProfileValue(section, field, profileFields);

            if (string.IsNullOrWhiteSpace(value))
                continue;

            chunks.Add($@"Profil Perusahaan:
Field: {field}
Value: {value}");
        }

        if (!chunks.Any())
        {
            chunks.Add(section);
        }

        return chunks;
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
