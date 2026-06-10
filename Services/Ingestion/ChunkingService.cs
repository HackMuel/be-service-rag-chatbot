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

    // Dummy dataset section headers. Accepts one or more leading newlines so it
    // works whether the headers are separated by a single line break (coordinate-
    // based PDF extraction) or a blank line (legacy flattened extraction).
    private const string DummySectionPattern =
        @"(?=\n+[ \t]*(?:1\. Profil Perusahaan|2\. Data Karyawan Internal|3\. SOP Keamanan Area Kilang|4\. Rekap Lembur Karyawan|5\. Log Maintenance Peralatan|6\. Catatan Audit dan Keamanan))";

    public ChunkingService(IOptions<RetrievalOptions> retrievalOptions)
    {
        _maxLength = retrievalOptions.Value.GenericChunkMaxLength;
        _overlap   = retrievalOptions.Value.GenericChunkOverlap;
    }

    // Primary entry: produces typed chunks (content + chunkType + sectionTitle).
    // Dummy dataset → legacy per-record splitting; generic documents → heading-
    // aware section splitting; oversized chunks → size-bounded fallback.
    public List<ContentChunk> Chunk(string text)
    {
        var sections = Regex
            .Split(text, DummySectionPattern)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var pieces = new List<ContentChunk>();

        foreach (var section in sections)
        {
            if (section.Contains("Data Karyawan:", StringComparison.OrdinalIgnoreCase))
            {
                pieces.AddRange(SplitStructuredRows(
                    section, @"(?=Data Karyawan:\s*NIK:)", "Data Karyawan:", "Data Karyawan"));
            }
            else if (section.Contains("Rekap Lembur:", StringComparison.OrdinalIgnoreCase))
            {
                pieces.AddRange(SplitStructuredRows(
                    section, @"(?=Rekap Lembur:\s*Tanggal:)", "Rekap Lembur:", "Rekap Lembur"));
            }
            else if (section.Contains("Log Maintenance:", StringComparison.OrdinalIgnoreCase))
            {
                pieces.AddRange(SplitStructuredRows(
                    section, @"(?=Log Maintenance:\s*Kode:)", "Log Maintenance:", "Log Maintenance"));
            }
            else if (section.Contains("Profil Perusahaan", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var fact in SplitProfileSection(section))
                    pieces.Add(new ContentChunk(fact, "structured_fact", "Profil Perusahaan"));
            }
            else
            {
                // Generic / dummy SOP & audit narrative sections.
                pieces.AddRange(SplitGenericSections(section));
            }
        }

        // Final pass: any chunk exceeding _maxLength is split further (handles
        // generic documents that produce large blocks and prevents Ollama from
        // rejecting oversized embedding input). sectionTitle is preserved; the
        // size-split fragments are tagged narrative_chunk.
        var finalChunks = new List<ContentChunk>(pieces.Count);
        foreach (var piece in pieces)
        {
            if (piece.Content.Length > _maxLength)
            {
                foreach (var sub in SplitBySize(piece.Content))
                    finalChunks.Add(new ContentChunk(sub, "narrative_chunk", piece.SectionTitle));
            }
            else
            {
                finalChunks.Add(piece);
            }
        }

        return finalChunks;
    }

    // Backward-compatible: returns chunk contents only. Old dummy behavior is
    // preserved (delegates to Chunk, which contains the same splitting logic).
    public List<string> SplitBySections(string text)
    {
        return Chunk(text).Select(c => c.Content).ToList();
    }

    private static List<ContentChunk> SplitStructuredRows(
        string section,
        string splitPattern,
        string rowPrefix,
        string sectionTitle)
    {
        return Regex
            .Split(section, splitPattern)
            .Select(x => x.Trim())
            .Where(x => x.StartsWith(rowPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(row => new ContentChunk(row, "structured_row", sectionTitle))
            .ToList();
    }

    // Splits a generic section into one chunk per heading-delimited block.
    // Recognized headings (SECTION N - ..., N.N ..., BAB ...) become sectionTitle.
    // If no heading is found, the whole section is a single narrative_section.
    private List<ContentChunk> SplitGenericSections(string section)
    {
        var matches = ChunkMetadataExtractor.GenericHeadingRegex.Matches(section);

        if (matches.Count == 0)
        {
            return new List<ContentChunk>
            {
                new ContentChunk(
                    section,
                    "narrative_section",
                    ChunkMetadataExtractor.ExtractSectionTitle(section))
            };
        }

        var result = new List<ContentChunk>();

        // Text before the first heading (intro/preface).
        var firstIdx = matches[0].Index;
        if (firstIdx > 0)
        {
            var preface = section[..firstIdx].Trim();
            if (!string.IsNullOrWhiteSpace(preface))
            {
                result.Add(new ContentChunk(
                    preface,
                    "narrative_section",
                    ChunkMetadataExtractor.ExtractSectionTitle(preface)));
            }
        }

        for (int i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = (i + 1 < matches.Count) ? matches[i + 1].Index : section.Length;

            var block = section[start..end].Trim();
            if (string.IsNullOrWhiteSpace(block))
                continue;

            var heading = Regex.Replace(matches[i].Value.Trim(), @"\s+", " ");
            result.Add(new ContentChunk(block, "narrative_section", heading));
        }

        return result;
    }

    // Splits text into overlapping chunks bounded by _maxLength.
    // Break priority: paragraph boundary (\n\n) → sentence boundary → word boundary → hard cut.
    // The next window start is snapped forward to a word boundary so no chunk
    // ever begins in the middle of a word.
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

            // Advance into the overlap region, then snap forward to a word start
            // so the next chunk does not begin mid-word.
            int nextStart = Math.Max(start + MinChunkLength, end - _overlap);
            nextStart = SnapToWordStart(text, nextStart);

            // Guarantee forward progress (avoids near-infinite loops).
            if (nextStart <= start)
                nextStart = end;

            start = nextStart;
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

    // Moves pos forward to the first character of the next whole word so a chunk
    // never starts in the middle of a word. Monotonic — only moves forward.
    private static int SnapToWordStart(string text, int pos)
    {
        if (pos <= 0) return 0;
        if (pos >= text.Length) return text.Length;

        // Already at a word start (previous character is whitespace).
        if (char.IsWhiteSpace(text[pos - 1]))
            return pos;

        // Inside a word: skip the rest of the partial word, then the whitespace,
        // landing on the next word's first character.
        int i = pos;
        while (i < text.Length && !char.IsWhiteSpace(text[i]))
            i++;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;

        return i;
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
