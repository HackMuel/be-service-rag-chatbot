using System.Text.RegularExpressions;
using be_service.Models;
using Microsoft.Extensions.Options;

namespace be_service.Services;

public class ChunkingService
{
    private readonly int _maxLength;
    private readonly int _overlap;

    // Minimum chars before a break is considered valid — prevents tiny fragments.
    private const int MinChunkLength = 300;

    public ChunkingService(IOptions<RetrievalOptions> retrievalOptions)
    {
        _maxLength = retrievalOptions.Value.GenericChunkMaxLength;
        _overlap   = retrievalOptions.Value.GenericChunkOverlap;
    }

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

        // Final pass: any chunk exceeding _maxLength is split further.
        // This handles generic documents that produce no section matches above,
        // preventing Ollama embedding from rejecting oversized input.
        var finalChunks = new List<string>(chunks.Count);
        foreach (var chunk in chunks)
        {
            if (chunk.Length > _maxLength)
                finalChunks.AddRange(SplitBySize(chunk));
            else
                finalChunks.Add(chunk);
        }

        return finalChunks;
    }

    // Splits text into overlapping chunks bounded by _maxLength.
    // Break priority: paragraph boundary (\n\n) → sentence boundary → word boundary → hard cut.
    public List<string> SplitBySize(string text)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return chunks;

        int start = 0;
        while (start < text.Length)
        {
            int end = Math.Min(start + _maxLength, text.Length);

            if (end < text.Length)
                end = FindBestBreak(text, start, end);

            var chunk = text[start..end].Trim();
            if (!string.IsNullOrWhiteSpace(chunk))
                chunks.Add(chunk);

            if (end >= text.Length)
                break;

            // Advance at least MinChunkLength to prevent near-infinite loops on
            // text with no suitable break point near the window boundary.
            start = Math.Max(start + MinChunkLength, end - _overlap);
        }

        return chunks;
    }

    // Returns the best split position in (start + MinChunkLength, end].
    private static int FindBestBreak(string text, int start, int end)
    {
        int earliest = start + MinChunkLength;
        if (earliest >= end)
            return end;

        int searchLen = end - earliest;

        // Priority 1: paragraph boundary (\n\n)
        int paraBreak = text.LastIndexOf("\n\n", end - 1, searchLen);
        if (paraBreak >= earliest)
            return paraBreak;

        // Priority 2: sentence boundary (. ! ? followed by whitespace)
        for (int i = end - 1; i >= earliest + 1; i--)
        {
            char c = text[i - 1];
            if ((c == '.' || c == '!' || c == '?') && char.IsWhiteSpace(text[i]))
                return i + 1;
        }

        // Priority 3: word boundary (space)
        int spaceBreak = text.LastIndexOf(' ', end - 1, searchLen);
        if (spaceBreak >= earliest)
            return spaceBreak + 1;

        return end; // hard cut — no suitable boundary found in window
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
