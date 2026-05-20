using be_service.Models;
using System.Text.RegularExpressions;

namespace be_service.Services;

public class RagChatService
{
    private readonly OllamaService _ollamaService;
    private readonly QdrantService _qdrantService;
    private readonly ILogger<RagChatService> _logger;
    private readonly QueryAnalyzerService _queryAnalyzerService;
    private readonly AnswerFormatterService _answerFormatterService;
    private readonly PromptBuilderService _promptBuilderService;
    private readonly RetrievalService _retrievalService;
    private const float MinimumSemanticSimilarity = 0.50f;

    public RagChatService(
        OllamaService ollamaService,
        QdrantService qdrantService,
        ILogger<RagChatService> logger,
         QueryAnalyzerService queryAnalyzerService,
         AnswerFormatterService answerFormatterService,
         PromptBuilderService promptBuilderService,
         RetrievalService retrievalService)
    {
        _ollamaService = ollamaService;
        _qdrantService = qdrantService;
        _logger = logger;
        _queryAnalyzerService = queryAnalyzerService;
        _answerFormatterService = answerFormatterService;
        _promptBuilderService = promptBuilderService;
        _retrievalService = retrievalService;
    }

    public async Task<ChatResponse> AskAsync(string question)
    {
        question = (question ?? "").Trim();

        if (string.IsNullOrWhiteSpace(question))
        {
            return NotFoundResponse();
        }

        _logger.LogInformation("RAG query={Query}", TruncateForLog(question, 160));

        var analysis = _queryAnalyzerService.Analyze(question);

        var retrievalResult = await _retrievalService.RetrieveAsync(analysis);

        var relevantChunks = retrievalResult.Chunks;
        var retrievalMode = retrievalResult.RetrievalMode;

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

        var deterministicAnswer = _answerFormatterService.TryBuildDeterministicAnswer(
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

        var prompt = _promptBuilderService.Build(question, relevantChunks);

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
            "jumlah shift",
            "shift operasional",
            "direktur operasi",
            "perusahaan dalam dokumen");
    }

    private static bool IsOvertimeQuery(string question)
    {
        return ContainsAny(question, "lembur", "rekap lembur");
    }

    private static bool IsEmployeeQuery(string question)
    {
        return ContainsAny(question, "karyawan", "pegawai");
    }

    private static bool IsMaintenanceQuery(string question)
    {
        return ContainsAny(
            question,
            "maintenance",
            "peralatan",
            "teknisi",
            "log maintenance");
    }

    private static string ExtractDivisionFromQuestion(string question)
    {
        if (ContainsAny(question, "it & digitalisasi", "it digitalisasi", "digitalisasi"))
            return "IT & Digitalisasi";

        if (ContainsAny(question, "operasional kilang"))
            return "Operasional Kilang";

        if (ContainsAny(question, "human capital"))
            return "Human Capital";

        if (ContainsAny(question, "maintenance"))
            return "Maintenance";

        if (ContainsAny(question, "distribusi"))
            return "Distribusi";

        if (ContainsAny(question, "keuangan"))
            return "Keuangan";

        if (ContainsAny(question, "security"))
            return "Security";

        if (ContainsAny(question, "hsse"))
            return "HSSE";

        return "";
    }

    private static string ExtractShiftFromQuestion(string question)
    {
        var match = Regex.Match(question, @"\bshift\s*([ABC])\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : "";
    }

    private static string ExtractEmployeeStatusFromQuestion(string question)
    {
        if (ContainsAny(question, "kontrak"))
            return "Kontrak";

        if (ContainsAny(question, "tetap"))
            return "Tetap";

        return "";
    }

    private static string ExtractPositionFromQuestion(string question)
    {
        var positions = new[]
        {
            "Supervisor",
            "Operator",
            "Manager",
            "Coordinator",
            "Analyst",
            "Engineer",
            "Staff"
        };

        return ExtractCanonicalValue(question, positions);
    }

    private static string ExtractMaintenanceStatusFromQuestion(string question)
    {
        if (ContainsAny(question, "maintenance berkala", "berkala"))
            return "Maintenance Berkala";

        if (ContainsAny(question, "perbaikan"))
            return "Perbaikan";

        if (Regex.IsMatch(question, @"\bnormal\b", RegexOptions.IgnoreCase))
            return "Normal";

        return "";
    }

    private static string ExtractApprovalFromQuestion(string question)
    {
        if (ContainsAny(question, "pending"))
            return "Pending";

        if (ContainsAny(question, "disetujui"))
            return "Disetujui";

        if (ContainsAny(question, "ditolak"))
            return "Ditolak";

        return "";
    }

    private static string ExtractLocationFromQuestion(string question)
    {
        var locations = new[]
        {
            "Gate Utama",
            "Utility Plant",
            "Area Produksi B",
            "Area Tanki A",
            "Warehouse"
        };

        return ExtractCanonicalValue(question, locations);
    }

    private static bool IsAuditQuery(string question)
    {
        return ContainsAny(
            question,
            "audit",
            "backup",
            "server",
            "logbook",
            "anomali suhu",
            "kepatuhan apd",
            "pelanggaran minor");
    }

    private static string ExtractTechnicianFromQuestion(string question)
    {
        var cleaned = Regex.Replace(question, @"[?.,!]", " ");
        var stopWords = new[]
        {
            "siapa",
            "saja",
            "teknisi",
            "maintenance",
            "peralatan",
            "log",
            "tampilkan",
            "berikan",
            "data",
            "di",
            "lokasi",
            "yang",
            "bernama",
            "dengan",
            "nama"
        };

        foreach (var stopWord in stopWords)
        {
            cleaned = Regex.Replace(
                cleaned,
                $@"\b{Regex.Escape(stopWord)}\b",
                " ",
                RegexOptions.IgnoreCase);
        }

        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

        return LooksLikePersonName(cleaned) ? cleaned : "";
    }

    private static string ExtractCanonicalValue(string question, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (Regex.IsMatch(
                    question,
                    $@"\b{Regex.Escape(value)}\b",
                    RegexOptions.IgnoreCase))
            {
                return value;
            }
        }

        return "";
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

        if (ContainsAny(question, "jumlah shift", "shift operasional"))
            return "Jumlah Shift Operasional";

        if (ContainsAny(question, "direktur operasi"))
            return "Direktur Operasi";

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
