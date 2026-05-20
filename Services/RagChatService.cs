using be_service.Models;
using System.Text.RegularExpressions;

namespace be_service.Services;

public class RagChatService
{
    private readonly OllamaService _ollamaService;
    private readonly QdrantService _qdrantService;
    private readonly ILogger<RagChatService> _logger;
    private const float MinimumSemanticSimilarity = 0.50f;

    public RagChatService(
        OllamaService ollamaService,
        QdrantService qdrantService,
        ILogger<RagChatService> logger)
    {
        _ollamaService = ollamaService;
        _qdrantService = qdrantService;
        _logger = logger;
    }

    public async Task<ChatResponse> AskAsync(string question)
    {
        List<RetrievedChunk> chunks = new();
        question = (question ?? "").Trim();
        var retrievalMode = "semantic";
        var contextLimit = 5;

        if (string.IsNullOrWhiteSpace(question))
        {
            return NotFoundResponse();
        }

        _logger.LogInformation("RAG query={Query}", TruncateForLog(question, 160));

        var nikMatch = Regex.Match(
            question,
            @"\bRU\s*6\s*-?\s*\d{4}\b",
            RegexOptions.IgnoreCase

        );
        var maintenanceMatch = Regex.Match(
            question,
            @"\bMT\s*-?\s*\d{3}\b",
            RegexOptions.IgnoreCase
        );

        var dateMatch = Regex.Match(
            question,
            @"\b\d{2}-\d{2}-\d{4}\b",
            RegexOptions.IgnoreCase
        );

        if (nikMatch.Success)
        {
            var normalizedNik = QdrantService.NormalizeNik(nikMatch.Value);

            chunks = await _qdrantService.SearchByNikAsync(normalizedNik);
            retrievalMode = "exact-nik";
            contextLimit = 1;
        }
        else if (maintenanceMatch.Success)
        {
            var normalizedCode = QdrantService.NormalizeMaintenanceCode(maintenanceMatch.Value);

            chunks = await _qdrantService.SearchByMaintenanceCodeAsync(normalizedCode);
            retrievalMode = "exact-maintenance-code";
            contextLimit = 1;
        }
        else if (dateMatch.Success)
        {
            chunks = await _qdrantService.SearchByDateAsync(dateMatch.Value);
            retrievalMode = "exact-date";
            contextLimit = 10;
        }
        else if (IsSopQuery(question))
        {
            chunks = await _qdrantService.SearchByRecordTypeAsync(
                "sop",
                BuildSopKeyword(question),
                5);
            retrievalMode = "sop";
            contextLimit = 5;
        }
        else if (IsProfileQuery(question))
        {
            var profileKeyword = BuildProfileKeyword(question);

            chunks = await _qdrantService.SearchByRecordTypeAsync(
                "profile",
                profileKeyword,
                5);

            if (!chunks.Any() && !string.IsNullOrWhiteSpace(profileKeyword))
            {
                chunks = await _qdrantService.SearchByRecordTypeAsync("profile", "", 5);
            }

            retrievalMode = "profile";
            contextLimit = 5;
        }
        else
        {
            var personKeyword = ExtractPersonKeyword(question);

            if (LooksLikePersonName(personKeyword))
            {
                if (IsOvertimeQuery(question))
                {
                    chunks = await _qdrantService.SearchByRecordTypeAsync(
                        "overtime",
                        personKeyword,
                        10);
                    retrievalMode = "exact-name-overtime";
                }

                if (!chunks.Any())
                {
                    chunks = await _qdrantService.SearchByNameAsync(personKeyword, 10);
                    retrievalMode = "exact-name";
                }

                contextLimit = 10;
            }
        }

        if (!chunks.Any())
        {
            var embedding =
                await _ollamaService.GenerateEmbeddingAsync(question);

            chunks =
                await _qdrantService.SearchSemanticAsync(embedding, 5);

            chunks = chunks
                .Where(x => x.Similarity >= MinimumSemanticSimilarity)
                .ToList();

            retrievalMode = "semantic";
            contextLimit = 5;
        }

        var relevantChunks = chunks
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .OrderByDescending(x => x.Similarity)
            .ThenBy(x => x.ChunkIndex ?? int.MaxValue)
            .Take(contextLimit)
            .ToList();

        _logger.LogInformation(
            "RAG retrieval mode={RetrievalMode}, chunks={ChunkCount}",
            retrievalMode,
            relevantChunks.Count);

        foreach (var chunk in relevantChunks)
        {
            _logger.LogInformation(
                "RAG chunk type={RecordType}, score={Score:F2}, source={DocumentTitle}, chunkIndex={ChunkIndex}",
                ResolveRecordType(chunk),
                chunk.Similarity,
                chunk.DocumentTitle,
                chunk.ChunkIndex);
        }

        if (!relevantChunks.Any())
        {
            return NotFoundResponse();
        }

        var deterministicAnswer = TryBuildDeterministicAnswer(
            relevantChunks,
            retrievalMode,
            question);

        if (!string.IsNullOrWhiteSpace(deterministicAnswer))
        {
            return new ChatResponse
            {
                Answer = deterministicAnswer,
                Sources = GetSources(relevantChunks),
            };
        }

        var context = string.Join(
            "\n\n",
            relevantChunks.Select((x, index) =>
                $@"[Sumber {index + 1}]
DocumentTitle: {x.DocumentTitle}
RecordType: {ResolveRecordType(x)}
SectionTitle: {ValueOrFallback(x.SectionTitle, QdrantService.ExtractSectionTitle(x.Content))}
Similarity: {x.Similarity:F2}
ChunkIndex: {(x.ChunkIndex.HasValue ? x.ChunkIndex.Value.ToString() : "-")}
Content:
{x.Content}"
            ));

        var prompt = $@"
Kamu adalah AI assistant internal perusahaan.

ATURAN:
- Selalu jawab dalam Bahasa Indonesia.
- Jawab HANYA berdasarkan context.
- Jangan membuat informasi sendiri.
- Jika informasi tidak ada di context, katakan:
  'Maaf, saya tidak menemukan informasi tersebut.'
- Jangan mengubah nama unit, NIK, kode, angka, tanggal, status, atau nilai yang ada di context.
- Setiap blok context adalah record yang berbeda. Jangan menggabungkan beberapa record menjadi satu record.
- Jika ditemukan lebih dari satu orang atau lebih dari satu record dengan nama yang sama, tampilkan semuanya.
- Jangan menggabungkan Data Karyawan, Rekap Lembur, SOP, dan Log Maintenance menjadi satu record.
- Kelompokkan jawaban berdasarkan RecordType jika context berisi employee, overtime, maintenance, sop, profile, atau document.
- Jika context berisi Data Karyawan, tampilkan semua field: NIK, Nama, Divisi, Jabatan, Shift, Status.
- Jika context berisi Rekap Lembur, tampilkan semua field: Tanggal, Nama, Divisi, Durasi, Approval.
- Jika context berisi Log Maintenance, tampilkan semua field: Kode, Peralatan, Lokasi, Status, Teknisi.
- Jika context berisi SOP, tampilkan poin-poin SOP yang tersedia.
- Jika user menanyakan NIK seseorang, cari field NIK pada context.
- Untuk pertanyaan exact seperti NIK, kode maintenance, dan tanggal, jawab hanya dari record yang ada di context.
- Jangan menambahkan informasi yang tidak tertulis di context.
- Jawaban harus jelas, informatif, dan profesional.


=====================
CONTEXT:
{context}
=====================

PERTANYAAN:
{question}

JAWABAN:
";

        var answer =
            await _ollamaService.GenerateChatAsync(prompt);

        if (IsNotFoundAnswer(answer))
        {
            return NotFoundResponse();
        }

        return new ChatResponse
        {
            Answer = answer,
            Sources = GetSources(relevantChunks),
        };
    }

    private static ChatResponse NotFoundResponse()
    {
        return new ChatResponse
        {
            Answer = "Maaf, saya tidak menemukan informasi tersebut.",
            Sources = new List<string>(),
        };
    }

    private static string ExtractPersonKeyword(string question)
    {
        var cleaned = Regex.Replace(question, @"[?.,!]", " ");
        var stopWords = new[]
        {
            "apa",
            "itu",
            "siapa",
            "saja",
            "semua",
            "berikan",
            "beri",
            "tampilkan",
            "data",
            "karyawan",
            "pegawai",
            "dengan",
            "nama",
            "nik",
            "berapa",
            "nya",
            "yang",
            "memiliki",
            "punya",
            "tolong",
            "carikan",
            "info",
            "informasi",
            "tentang",
            "dari",
            "pada",
            "untuk",
            "rekap",
            "lembur",
            "maintenance",
            "log",
            "kode"
        };

        foreach (var stopWord in stopWords)
        {
            cleaned = Regex.Replace(
                cleaned,
                $@"\b{Regex.Escape(stopWord)}\b",
                " ",
                RegexOptions.IgnoreCase);
        }

        return Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    private static bool LooksLikePersonName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var lowerValue = value.ToLowerInvariant();
        var domainWords = new[]
        {
            "sop",
            "apd",
            "aturan",
            "area",
            "produksi",
            "tangki",
            "penyimpanan",
            "akses",
            "kilang",
            "maintenance",
            "lembur",
            "rekap",
            "log",
            "tanggal",
            "status",
            "peralatan",
            "teknisi",
            "kode",
            "ru6",
            "mt"
        };

        if (domainWords.Any(x => lowerValue.Contains(x)))
            return false;

        if (Regex.IsMatch(value, @"\d"))
            return false;

        var words = Regex.Matches(value, @"[A-Za-z]+")
            .Select(x => x.Value)
            .ToList();

        return words.Count is >= 2 and <= 4 &&
               words.All(x => x.Length >= 2);
    }

    private static string? TryBuildDeterministicAnswer(
        List<RetrievedChunk> chunks,
        string retrievalMode,
        string question)
    {
        if (retrievalMode == "profile")
        {
            return BuildProfileAnswer(chunks, question);
        }

        if (!retrievalMode.StartsWith("exact-", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return BuildStructuredAnswer(chunks);
    }

    private static string? BuildStructuredAnswer(List<RetrievedChunk> chunks)
    {
        var lines = new List<string>();

        var employeeChunks = chunks
            .Where(x => ResolveRecordType(x) == "employee")
            .ToList();

        if (employeeChunks.Any())
        {
            lines.Add("Data Karyawan:");

            foreach (var chunk in employeeChunks)
            {
                lines.Add($@"- NIK: {ValueOrFallback(chunk.Nik, QdrantService.ExtractNik(chunk.Content))}
  Nama: {ValueOrFallback(chunk.Name, QdrantService.ExtractName(chunk.Content))}
  Divisi: {ValueOrFallback(chunk.Division, QdrantService.ExtractDivision(chunk.Content))}
  Jabatan: {ValueOrFallback(chunk.Position, QdrantService.ExtractPosition(chunk.Content))}
  Shift: {ValueOrFallback(chunk.Shift, QdrantService.ExtractShift(chunk.Content))}
  Status: {ValueOrFallback(chunk.EmployeeStatus, QdrantService.ExtractEmployeeStatus(chunk.Content))}");
            }
        }

        var overtimeChunks = chunks
            .Where(x => ResolveRecordType(x) == "overtime")
            .ToList();

        if (overtimeChunks.Any())
        {
            if (lines.Any())
                lines.Add("");

            lines.Add("Rekap Lembur:");

            foreach (var chunk in overtimeChunks)
            {
                lines.Add($@"- Tanggal: {ValueOrFallback(chunk.Date, QdrantService.ExtractDate(chunk.Content))}
  Nama: {ValueOrFallback(chunk.Name, QdrantService.ExtractName(chunk.Content))}
  Divisi: {ValueOrFallback(chunk.Division, QdrantService.ExtractDivision(chunk.Content))}
  Durasi: {ValueOrFallback(chunk.Duration, QdrantService.ExtractDuration(chunk.Content))}
  Approval: {ValueOrFallback(chunk.Approval, QdrantService.ExtractApproval(chunk.Content))}");
            }
        }

        var maintenanceChunks = chunks
            .Where(x => ResolveRecordType(x) == "maintenance")
            .ToList();

        if (maintenanceChunks.Any())
        {
            if (lines.Any())
                lines.Add("");

            lines.Add("Log Maintenance:");

            foreach (var chunk in maintenanceChunks)
            {
                lines.Add($@"- Kode: {ValueOrFallback(chunk.MaintenanceCode, QdrantService.ExtractMaintenanceCode(chunk.Content))}
  Peralatan: {ValueOrFallback(chunk.Equipment, QdrantService.ExtractEquipment(chunk.Content))}
  Lokasi: {ValueOrFallback(chunk.Location, QdrantService.ExtractLocation(chunk.Content))}
  Status: {ValueOrFallback(chunk.MaintenanceStatus, QdrantService.ExtractMaintenanceStatus(chunk.Content))}
  Teknisi: {ValueOrFallback(chunk.Technician, QdrantService.ExtractTechnician(chunk.Content))}");
            }
        }

        return lines.Any() ? string.Join("\n", lines) : null;
    }

    private static string? BuildProfileAnswer(List<RetrievedChunk> chunks, string question)
    {
        var fields = chunks
            .Select(x => (
                Field: ExtractContentLabel(x.Content, "Field"),
                Value: ExtractContentLabel(x.Content, "Value")))
            .Where(x => !string.IsNullOrWhiteSpace(x.Field) &&
                        !string.IsNullOrWhiteSpace(x.Value))
            .ToList();

        if (!fields.Any())
            return null;

        if (ContainsAny(question, "nama unit", "unit perusahaan"))
            return FindProfileValue(fields, "Nama Unit");

        if (ContainsAny(question, "kapasitas", "kapasitas produksi"))
            return FindProfileValue(fields, "Kapasitas Produksi");

        if (ContainsAny(question, "lokasi", "alamat"))
            return FindProfileValue(fields, "Lokasi");

        if (ContainsAny(question, "jumlah karyawan"))
            return FindProfileValue(fields, "Jumlah Karyawan");

        return string.Join(
            "\n",
            fields.Select(x => $"{x.Field}: {x.Value}"));
    }

    private static string? FindProfileValue(
        IEnumerable<(string Field, string Value)> fields,
        string fieldName)
    {
        return fields
            .FirstOrDefault(x => x.Field.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
            .Value;
    }

    private static string ExtractContentLabel(string content, string label)
    {
        var match = Regex.Match(
            content,
            $@"(?im)^\s*{Regex.Escape(label)}:\s*(.+?)\s*$");

        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    private static bool IsSopQuery(string question)
    {
        return ContainsAny(
            question,
            "sop",
            "keamanan",
            "apd",
            "area kilang",
            "area produksi",
            "tangki",
            "penyimpanan");
    }

    private static bool IsProfileQuery(string question)
    {
        return ContainsAny(
            question,
            "profil",
            "nama unit",
            "unit perusahaan",
            "kapasitas",
            "kapasitas produksi",
            "lokasi",
            "jumlah karyawan",
            "perusahaan dalam dokumen");
    }

    private static bool IsOvertimeQuery(string question)
    {
        return ContainsAny(question, "lembur", "rekap lembur");
    }

    private static string BuildSopKeyword(string question)
    {
        if (ContainsAny(question, "tangki", "penyimpanan"))
            return "tangki penyimpanan";

        if (ContainsAny(question, "apd"))
            return "APD";

        if (ContainsAny(question, "area produksi"))
            return "area produksi";

        if (ContainsAny(question, "akses", "mengakses"))
            return "akses";

        if (ContainsAny(question, "sop", "keamanan", "area kilang"))
            return "SOP Keamanan Area Kilang";

        return question;
    }

    private static string BuildProfileKeyword(string question)
    {
        if (ContainsAny(question, "nama unit", "unit perusahaan"))
            return "Nama Unit";

        if (ContainsAny(question, "kapasitas", "kapasitas produksi"))
            return "Kapasitas Produksi";

        if (ContainsAny(question, "lokasi", "alamat"))
            return "Lokasi";

        if (ContainsAny(question, "jumlah karyawan"))
            return "Jumlah Karyawan";

        return "";
    }

    private static bool ContainsAny(string value, params string[] keywords)
    {
        return keywords.Any(keyword =>
            value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static string ValueOrFallback(string value, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        return string.IsNullOrWhiteSpace(fallback) ? "-" : fallback;
    }

    private static List<string> GetSources(List<RetrievedChunk> chunks)
    {
        return chunks
            .Select(x => x.DocumentTitle)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();
    }

    private static bool IsNotFoundAnswer(string answer)
    {
        return answer.Contains(
            "Maaf, saya tidak menemukan informasi tersebut",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string TruncateForLog(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;

        return value[..maxLength];
    }

    private static string ResolveRecordType(RetrievedChunk chunk)
    {
        return string.IsNullOrWhiteSpace(chunk.RecordType)
            ? QdrantService.DetectRecordType(chunk.Content)
            : chunk.RecordType;
    }
}
