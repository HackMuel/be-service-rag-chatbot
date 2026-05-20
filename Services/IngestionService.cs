using be_service.Models;
using Npgsql;
using UglyToad.PdfPig;

namespace be_service.Services;

public class IngestionService
{
    private readonly IConfiguration _configuration;
    private readonly OllamaService _ollamaService;
    private readonly QdrantService _qdrantService;

    public IngestionService(
        IConfiguration configuration,
        OllamaService ollamaService,
        QdrantService qdrantService)
    {
        _configuration = configuration;
        _ollamaService = ollamaService;
        _qdrantService = qdrantService;
    }

    public async Task<Guid> IngestPdfAsync(IFormFile file, string department = "General")
    {
        using var stream = file.OpenReadStream();

        using var pdf = PdfDocument.Open(stream);

        var text = string.Join("\n\n",
    pdf.GetPages().Select(page =>
        string.Join(" ", page.GetWords().Select(w => w.Text))
    ));

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new Exception("PDF tidak memiliki text yang bisa dibaca. Kemungkinan PDF berupa scan/gambar.");
        }

        var request = new IngestRequest
        {
            Title = file.FileName,
            Department = department,
            Content = text
        };

        return await IngestAsync(request);
    }

    public async Task<Guid> IngestAsync(IngestRequest request)
    {
        var connectionString =
            _configuration.GetConnectionString("SupabaseDb");

        await _qdrantService.EnsureCollectionAsync();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        Guid documentId;

        var insertDocumentSql = @"
            insert into documents (title, source_type, department)
            values (@title, 'text', @department)
            returning id;
        ";

        await using (var cmd = new NpgsqlCommand(insertDocumentSql, conn))
        {
            cmd.Parameters.AddWithValue("title", request.Title);
            cmd.Parameters.AddWithValue("department", request.Department);

            documentId = (Guid)(await cmd.ExecuteScalarAsync())!;
        }

        var chunks = SplitBySections(request.Content);
        Console.WriteLine($"TOTAL CHUNKS: {chunks.Count}");

        for (int i = 0; i < chunks.Count; i++)
        {
            Console.WriteLine(
                $"CHUNK {i}: type={QdrantService.DetectRecordType(chunks[i])}, length={chunks[i].Length}");
        }

        for (int i = 0; i < chunks.Count; i++)
        {
            var embedding = await _ollamaService.GenerateEmbeddingAsync(chunks[i]);
            var embeddingString = "[" + string.Join(",", embedding) + "]";

            var chunkId = Guid.NewGuid();

            await _qdrantService.UpsertChunkAsync(
                chunkId,
                documentId,
                request.Title,
                chunks[i],
                embedding,
                i,
                request.Department
            );
        }

        return documentId;
    }

    private static List<string> SplitBySections(string text)
    {
        text = CleanExtractedText(text);

        var pattern = @"(?=\n\n(?:1\. Profil Perusahaan|2\. Data Karyawan Internal|3\. SOP Keamanan Area Kilang|4\. Rekap Lembur Karyawan|5\. Log Maintenance Peralatan|6\. Catatan Audit dan Keamanan))";

        var sections = System.Text.RegularExpressions.Regex
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
                var employeeChunks = System.Text.RegularExpressions.Regex
                    .Split(section, @"(?=Data Karyawan:\s*NIK:)")
                    .Select(x => x.Trim())
                    .Where(x => x.StartsWith("Data Karyawan:"))
                    .ToList();

                chunks.AddRange(employeeChunks);
            }
            else if (section.Contains("Rekap Lembur:", StringComparison.OrdinalIgnoreCase))
            {
                var overtimeChunks = System.Text.RegularExpressions.Regex
                    .Split(section, @"(?=Rekap Lembur:\s*Tanggal:)")
                    .Select(x => x.Trim())
                    .Where(x => x.StartsWith("Rekap Lembur:"))
                    .ToList();

                chunks.AddRange(overtimeChunks);
            }
            else if (section.Contains("Log Maintenance:", StringComparison.OrdinalIgnoreCase))
            {
                var maintenanceChunks = System.Text.RegularExpressions.Regex
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
        var normalizedChunks = System.Text.RegularExpressions.Regex
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

    private static string NormalizeProfileSection(string text)
    {
        if (text.Contains("Profil Perusahaan:\nField:", StringComparison.OrdinalIgnoreCase))
            return text;

        var match = System.Text.RegularExpressions.Regex.Match(
            text,
            @"1\.\s*Profil Perusahaan\s+(.*?)(?=\s*2\.\s*Data Karyawan Internal)",
            System.Text.RegularExpressions.RegexOptions.Singleline |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
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

    private static string ExtractProfileValue(
        string section,
        string field,
        string[] allFields)
    {
        var nextFields = string.Join(
            "|",
            allFields
                .Where(x => !x.Equals(field, StringComparison.OrdinalIgnoreCase))
                .Select(System.Text.RegularExpressions.Regex.Escape));

        var match = System.Text.RegularExpressions.Regex.Match(
            section,
            $@"\b{System.Text.RegularExpressions.Regex.Escape(field)}\b\s*:?\s*(.+?)(?=\s+(?:{nextFields})\b\s*:?\s*|$)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Singleline);

        if (!match.Success)
            return "";

        return System.Text.RegularExpressions.Regex
            .Replace(match.Groups[1].Value, @"\s+", " ")
            .Trim();
    }

    private static string CleanExtractedText(string text)
    {
        text = text
            .Replace("\r\n", "\n")
            .Replace("\r", "\n");

        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"(?<!^)(?<!\n)(\d+\.\s*[A-Z])",
            "\n\n$1"
        );

        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"RU\s*6-",
            "RU6-"
        );

        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\b(HSSE)(Analyst|Manager|Engineer|Operator|Staff|Coordinator|Supervisor)\b",
            "$1 $2"
        );

        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\b([ABC])\s*(Tetap|Kontrak)\b",
            "$1 $2"
        );

        text = NormalizeProfileSection(text);
        text = NormalizeEmployeeTable(text);
        text = NormalizeOvertimeTable(text);
        text = NormalizeMaintenanceTable(text);

        text = System.Text.RegularExpressions.Regex.Replace(
            text,
            @"\n{3,}",
            "\n\n"
        );

        return text.Trim();
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

        return System.Text.RegularExpressions.Regex.Replace(
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

        return System.Text.RegularExpressions.Regex.Replace(
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

        return System.Text.RegularExpressions.Regex.Replace(
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
}
