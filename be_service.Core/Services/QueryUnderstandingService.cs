using System.Text.Json;
using System.Text.RegularExpressions;
using be_service.Models;
using Microsoft.AspNetCore.Identity;
using be_service.Abstractions;

namespace be_service.Services;

// LLM-based query understanding — drop-in replacement for QueryAnalyzerService.
// Same output contract (RagQueryAnalysis) but uses a chat prompt instead of
// keyword arrays. Falls back to QueryAnalyzerService on any LLM/parse failure.
public class QueryUnderstandingService
{
    private readonly IChatService _ollamaService;
    private readonly QueryAnalyzerService _fallback;
    private readonly ILogger<QueryUnderstandingService> _logger;

    private const string SystemPrompt = """
        Kamu adalah query classifier. Respond ONLY with raw JSON. No explanation, no markdown, no backticks.

        Schema: {"intent":"employee|overtime|maintenance|sop|audit|profile|policy|semantic","entities":{"name":"","nik":"","code":"","date":"","division":"","shift":"","status":"","position":"","location":"","approval":"","technician":"","equipment":""},"is_policy":false,"is_access":false}

        Intent: employee=data karyawan, overtime=rekap lembur, maintenance=log peralatan, sop=aturan/SOP, audit=audit/kepatuhan, profile=profil perusahaan, policy=siapa yang boleh/tidak boleh, semantic=pertanyaan umum/penjelasan.
        Isi hanya entities yang disebutkan eksplisit. Kosongkan yang tidak ada.

        BATAS sop vs audit:
        - Pertanyaan tentang cara/aturan/prosedur/kewajiban → intent=sop. Contoh: "aturan APD", "bagaimana prosedur", "apa SOP".
        - Pertanyaan tentang angka/persentase/tingkat kepatuhan/hasil audit → intent=audit. Contoh: "berapa persen kepatuhan", "tingkat kepatuhan APD".

        NILAI VALID (petakan bahasa informal ke nilai eksak ini):
        - position: Supervisor | Operator | Manager | Coordinator | Analyst | Engineer | Staff. Contoh: "siapa supervisor" → position="Supervisor"; sinonim "mandor"/"kepala regu"/"atasan" → position="Supervisor".
        - approval: Disetujui | Ditolak | Pending. Contoh: "sudah disetujui"/"yang di-approve" → approval="Disetujui"; "ditolak" → "Ditolak"; "belum diproses"/"menunggu" → "Pending".
        - status: Tetap | Kontrak.
        - shift: A | B | C.

        Q: "Apa aturan akun Laboratorium Aurora?" → {"intent":"semantic","entities":{},"is_policy":false,"is_access":false}
        Q: "Apa aturan login Laboratorium Aurora?" → {"intent":"semantic","entities":{},"is_policy":false,"is_access":false}
        Q: "sinta lestari bekerja di divisi apa?" → {"intent":"employee","entities":{"name":"sinta lestari"},"is_policy":false,"is_access":false}
        Q: "siapa yang boleh masuk area tangki?" → {"intent":"sop","entities":{},"is_policy":true,"is_access":true}
        Q: "karyawan shift A divisi IT" → {"intent":"employee","entities":{"division":"IT & Digitalisasi","shift":"A"},"is_policy":false,"is_access":false}
        Q: "siapa supervisor di perusahaan ini?" → {"intent":"employee","entities":{"position":"Supervisor"},"is_policy":false,"is_access":false}
        Q: "ada mandor ga di sini?" → {"intent":"employee","entities":{"position":"Supervisor"},"is_policy":false,"is_access":false}
        Q: "jelaskan tentang perusahaan ini" → {"intent":"profile","entities":{},"is_policy":false,"is_access":false}
        Q: "siapa teknisi generator?" → {"intent":"maintenance","entities":{"equipment":"Generator"},"is_policy":false,"is_access":false}
        Q: "rekap lembur yang sudah disetujui" → {"intent":"overtime","entities":{"approval":"Disetujui"},"is_policy":false,"is_access":false}
        Q: "rekap lembur yang ditolak" → {"intent":"overtime","entities":{"approval":"Ditolak"},"is_policy":false,"is_access":false}
        Q: "apa aturan APD di area produksi?" → {"intent":"sop","entities":{},"is_policy":false,"is_access":false}
        Q: "berapa persen kepatuhan APD?" → {"intent":"audit","entities":{},"is_policy":false,"is_access":false}
        Q: "berapa tingkat kepatuhan APD?" → {"intent":"audit","entities":{},"is_policy":false,"is_access":false}
        Q: "bagaimana prosedur operasional kilang?" → {"intent":"semantic","entities":{},"is_policy":false,"is_access":false}
        Q: "log maintenance kode MT-001" → {"intent":"maintenance","entities":{"code":"MT-001"},"is_policy":false,"is_access":false}
        Q: "apa nama unit perusahaan?" → {"intent":"profile","entities":{},"is_policy":false,"is_access":false}
        """;

    public QueryUnderstandingService(
        IChatService ollamaService,
        QueryAnalyzerService fallback,
        ILogger<QueryUnderstandingService> logger)
    {
        _ollamaService = ollamaService;
        _fallback = fallback;
        _logger = logger;
    }

    public async Task<RagQueryAnalysis> AnalyzeAsync(string question)
    {
        question = (question ?? "").Trim();
        if (string.IsNullOrWhiteSpace(question))
            return new RagQueryAnalysis { Question = question };

        // Security field check — keyword-based, always runs regardless of LLM result
        var requestedFields = FieldKeywordMap.ExtractFieldKeys(question);
        var fieldValidation = ValidateFields(requestedFields);

        try
        {
            var raw = await _ollamaService.CompleteAsync(SystemPrompt, question, temperature: 0, format: "json");
            var llmResult = ParseLlmResult(raw);

            // One retry on unparseable output before falling back to the keyword analyzer.
            if (llmResult is null)
            {
                _logger.LogInformation("QUS_RETRY length={Length}, JSON unparseable, retrying", question.Length);
                raw = await _ollamaService.CompleteAsync(SystemPrompt, question, temperature: 0, format: "json");
                llmResult = ParseLlmResult(raw);
            }

            if (llmResult is null)
            {
                _logger.LogWarning(
                    "QUS_FALLBACK reason=parse length={Length}, falling back to keyword analyzer",
                    question.Length);
                return ApplyFieldValidation(await _fallback.AnalyzeAsync(question), fieldValidation, "fallback-parse");
            }

            var analysis = MapToRagQueryAnalysis(question, llmResult, requestedFields, fieldValidation);
            analysis.AnalysisSource = "llm";

            _logger.LogInformation(
                "QUS_TRACE source=llm intent={Intent}, entities={Count}, isPolicyQ={IsPolicy}, answerLevel={Level}",
                llmResult.Intent,
                CountEntities(llmResult),
                llmResult.IsPolicy,
                analysis.AnswerLevel);

            return analysis;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QUS_FALLBACK reason=error length={Length}, falling back", question.Length);
            return ApplyFieldValidation(await _fallback.AnalyzeAsync(question), fieldValidation, "fallback-error");
        }
    }

    // Ensure field-block result is applied even on fallback path
    private static RagQueryAnalysis ApplyFieldValidation(
        RagQueryAnalysis analysis,
        FieldValidationResult fieldValidation,
        string source)
    {
        analysis.AnalysisSource = source;
        if (fieldValidation.IsBlocked)
        {
            analysis.IsBlocked = true;
            analysis.BlockReason = fieldValidation.BlockReason;
            analysis.AnswerLevel = AnswerLevel.Blocked;
        }
        return analysis;
    }

    private static LlmQueryResult? ParseLlmResult(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;

        try
        {
            using var doc = JsonDocument.Parse(raw[start..(end + 1)]);
            var root = doc.RootElement;

            var intent = root.TryGetProperty("intent", out var intentProp)
                ? intentProp.GetString()?.ToLowerInvariant() ?? ""
                : "";

            if (string.IsNullOrWhiteSpace(intent)) return null;

            var r = new LlmQueryResult
            {
                Intent = intent,
                IsPolicy = root.TryGetProperty("is_policy", out var pol) && pol.GetBoolean(),
                IsAccess = root.TryGetProperty("is_access", out var acc) && acc.GetBoolean()
            };

            if (root.TryGetProperty("entities", out var ents) &&
                ents.ValueKind == JsonValueKind.Object)
            {
                r.Name = GetEntityStr(ents, "name");
                r.Nik = GetEntityStr(ents, "nik");
                r.Code = GetEntityStr(ents, "code");
                r.Date = GetEntityStr(ents, "date");
                r.Division = GetEntityStr(ents, "division");
                r.Shift = GetEntityStr(ents, "shift");
                r.Status = GetEntityStr(ents, "status");
                r.Position = GetEntityStr(ents, "position");
                r.Location = GetEntityStr(ents, "location");
                r.Approval = GetEntityStr(ents, "approval");
                r.Technician = GetEntityStr(ents, "technician");
                r.Equipment = GetEntityStr(ents, "equipment");
            }

            return r;
        }
        catch
        {
            return null;
        }
    }

    private static string GetEntityStr(JsonElement entities, string key)
    {
        if (entities.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            return v.GetString()?.Trim() ?? "";
        return "";
    }

    private static RagQueryAnalysis MapToRagQueryAnalysis(
        string question,
        LlmQueryResult llm,
        List<string> requestedFields,
        FieldValidationResult fieldValidation)
    {
        var intent = llm.Intent;

        var isEmployee = intent == "employee";
        var isOvertime = intent == "overtime";
        var isMaintenance = intent == "maintenance";
        var isSop = intent == "sop";
        var isProfile = intent == "profile";
        var isAudit = intent == "audit";
        var isPolicyQ = llm.IsPolicy || intent == "policy";
        var isAccessQ = llm.IsAccess;

        var nik = string.IsNullOrWhiteSpace(llm.Nik) ? "" : ChunkMetadataExtractor.NormalizeNik(llm.Nik);
        var code = string.IsNullOrWhiteSpace(llm.Code) ? "" : ChunkMetadataExtractor.NormalizeMaintenanceCode(llm.Code);
        var division = NormalizeDivision(llm.Division);
        var shift = NormalizeShift(llm.Shift);
        var empStatus = NormalizeEmployeeStatus(llm.Status);
        var position = NormalizePosition(llm.Position);
        var mStatus = NormalizeMaintenanceStatus(llm.Status);
        var approval = NormalizeApproval(llm.Approval);
        var location = NormalizeLocation(llm.Location);

        var personKeyword = llm.Name?.Trim() ?? "";
        var looksLikeName = !string.IsNullOrWhiteSpace(personKeyword);
        var hasNik = !string.IsNullOrWhiteSpace(nik);
        var hasCode = !string.IsNullOrWhiteSpace(code);
        var hasDate = !string.IsNullOrWhiteSpace(llm.Date);

        var answerLevel = DetermineAnswerLevel(
            intent, hasNik, hasCode, hasDate,
            isEmployee, isOvertime, isMaintenance,
            division, shift, empStatus, position,
            approval, location, llm.Technician ?? "",
            looksLikeName, isPolicyQ);

        if (fieldValidation.IsBlocked)
            answerLevel = AnswerLevel.Blocked;

        return new RagQueryAnalysis
        {
            Question = question,
            AnswerLevel = answerLevel,
            Nik = nik,
            MaintenanceCode = code,
            Date = llm.Date?.Trim() ?? "",
            IsSopQuery = isSop,
            IsProfileQuery = isProfile,
            IsAuditQuery = isAudit,
            IsEmployeeQuery = isEmployee,
            IsOvertimeQuery = isOvertime,
            IsMaintenanceQuery = isMaintenance,
            SopKeyword = isSop ? question : "",
            ProfileKeyword = isProfile ? question : "",
            Division = division,
            Shift = shift,
            EmployeeStatus = empStatus,
            Position = position,
            MaintenanceStatus = mStatus,
            Approval = approval,
            Location = location,
            Technician = llm.Technician?.Trim() ?? "",
            PersonKeyword = personKeyword,
            LooksLikePersonName = looksLikeName,
            IsPolicyQuestion = isPolicyQ,
            IsAccessQuestion = isAccessQ,
            IsPermissionQuestion = isPolicyQ,
            TargetRecordType = GetTargetRecordType(intent),
            GenericRecordType = GetGenericRecordType(intent),
            RequiresGroundedLlm = answerLevel is AnswerLevel.PolicyGrounded or AnswerLevel.SemanticGrounded,
            RequestedFields = requestedFields,
            IsBlocked = fieldValidation.IsBlocked,
            BlockReason = fieldValidation.BlockReason
        };
    }

    private static AnswerLevel DetermineAnswerLevel(
        string intent,
        bool hasNik, bool hasCode, bool hasDate,
        bool isEmployee, bool isOvertime, bool isMaintenance,
        string division, string shift, string status, string position,
        string approval, string location, string technician,
        bool looksLikeName, bool isPolicyQ)
    {
        if (isPolicyQ) return AnswerLevel.PolicyGrounded;

        if (hasNik || hasCode || hasDate) return AnswerLevel.ExactStructured;

        var hasEmployeeFilter = isEmployee && (!string.IsNullOrWhiteSpace(division) || !string.IsNullOrWhiteSpace(shift) || !string.IsNullOrWhiteSpace(status) || !string.IsNullOrWhiteSpace(position));
        var hasOvertimeFilter = isOvertime && (!string.IsNullOrWhiteSpace(approval) || !string.IsNullOrWhiteSpace(division) || looksLikeName);
        var hasMaintenanceFilter = isMaintenance && (!string.IsNullOrWhiteSpace(location) || !string.IsNullOrWhiteSpace(technician));

        if (hasEmployeeFilter || hasOvertimeFilter || hasMaintenanceFilter || looksLikeName)
            return AnswerLevel.ExactStructured;

        // Bare structured intent with no filter (e.g. "berikan semua data karyawan")
        // → list-all via GenericRecordType scroll in RetrieveAsync. Mirrors legacy
        // DetermineAnswerLevel, which maps a generic employee/overtime/maintenance
        // record type to ExactStructured. Without this it falls through to
        // SemanticGrounded and gets diverted to pure vector search → empty result.
        if (intent is "employee" or "overtime" or "maintenance")
            return AnswerLevel.ExactStructured;

        if (intent is "sop" or "audit" or "profile" or "policy")
            return AnswerLevel.DeterministicTemplate;

        return AnswerLevel.SemanticGrounded;
    }

    // --- Normalizers: map LLM free-text to canonical payload values ---

    private static string NormalizeDivision(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (raw.Contains("IT", StringComparison.OrdinalIgnoreCase) || raw.Contains("digitalisasi", StringComparison.OrdinalIgnoreCase)) return "IT & Digitalisasi";
        if (raw.Contains("operasional kilang", StringComparison.OrdinalIgnoreCase)) return "Operasional Kilang";
        if (raw.Contains("human capital", StringComparison.OrdinalIgnoreCase)) return "Human Capital";
        if (raw.Contains("maintenance", StringComparison.OrdinalIgnoreCase)) return "Maintenance";
        if (raw.Contains("distribusi", StringComparison.OrdinalIgnoreCase)) return "Distribusi";
        if (raw.Contains("keuangan", StringComparison.OrdinalIgnoreCase)) return "Keuangan";
        if (raw.Contains("security", StringComparison.OrdinalIgnoreCase)) return "Security";
        if (raw.Contains("hsse", StringComparison.OrdinalIgnoreCase)) return "HSSE";
        return raw.Trim();
    }

    private static string NormalizeShift(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var m = Regex.Match(raw.Trim(), @"\b([ABC])\b", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : "";
    }

    private static string NormalizeEmployeeStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (raw.Contains("kontrak", StringComparison.OrdinalIgnoreCase)) return "Kontrak";
        if (raw.Contains("tetap", StringComparison.OrdinalIgnoreCase)) return "Tetap";
        return "";
    }

    private static string NormalizePosition(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string[] positions = ["Supervisor", "Operator", "Manager", "Coordinator", "Analyst", "Engineer", "Staff"];
        return positions.FirstOrDefault(p => raw.Contains(p, StringComparison.OrdinalIgnoreCase)) ?? "";
    }

    private static string NormalizeMaintenanceStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (raw.Contains("berkala", StringComparison.OrdinalIgnoreCase)) return "Maintenance Berkala";
        if (raw.Contains("perbaikan", StringComparison.OrdinalIgnoreCase)) return "Perbaikan";
        if (Regex.IsMatch(raw, @"\bnormal\b", RegexOptions.IgnoreCase)) return "Normal";
        return "";
    }

    private static string NormalizeApproval(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        if (raw.Contains("setuju", StringComparison.OrdinalIgnoreCase)) return "Disetujui";
        if (raw.Contains("tolak", StringComparison.OrdinalIgnoreCase)) return "Ditolak";
        if (raw.Contains("pending", StringComparison.OrdinalIgnoreCase)) return "Pending";
        return "";
    }

    private static string NormalizeLocation(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string[] locs = ["Gate Utama", "Utility Plant", "Area Produksi B", "Area Tanki A", "Warehouse"];
        return locs.FirstOrDefault(l => raw.Contains(l, StringComparison.OrdinalIgnoreCase)) ?? raw.Trim();
    }

    private static string GetTargetRecordType(string intent) => intent switch
    {
        "profile" => "profile",
        "audit" => "audit",
        "sop" => "sop",
        _ => string.Empty
    };

    private static string GetGenericRecordType(string intent) => intent switch
    {
        "employee" => "employee",
        "overtime" => "overtime",
        "maintenance" => "maintenance",
        "sop" => "sop",
        "audit" => "audit",
        _ => string.Empty
    };

    private static FieldValidationResult ValidateFields(List<string> fields)
    {
        foreach (var fieldKey in fields)
        {
            var access = FieldSchema.GetAccess(fieldKey);
            if (access == FieldAccess.Sensitive)
                return FieldValidationResult.Block(
                    $"Maaf, informasi tentang '{fieldKey.Replace('_', ' ')}' bersifat sensitif dan tidak dapat diakses melalui sistem ini.");
            if (access == FieldAccess.Unavailable)
                return FieldValidationResult.Block(
                    $"Maaf, informasi tentang '{fieldKey.Replace('_', ' ')}' tidak tersedia dalam sistem ini.");
        }
        return FieldValidationResult.Pass();
    }

    private static int CountEntities(LlmQueryResult r) =>
        new[] { r.Name, r.Nik, r.Code, r.Date, r.Division, r.Shift,
                r.Status, r.Position, r.Location, r.Approval, r.Technician, r.Equipment }
            .Count(v => !string.IsNullOrWhiteSpace(v));

    private sealed class LlmQueryResult
    {
        public string Intent { get; set; } = "";
        public string? Name { get; set; }
        public string? Nik { get; set; }
        public string? Code { get; set; }
        public string? Date { get; set; }
        public string? Division { get; set; }
        public string? Shift { get; set; }
        public string? Status { get; set; }
        public string? Position { get; set; }
        public string? Location { get; set; }
        public string? Approval { get; set; }
        public string? Technician { get; set; }
        public string? Equipment { get; set; }
        public bool IsPolicy { get; set; }
        public bool IsAccess { get; set; }
    }
}
