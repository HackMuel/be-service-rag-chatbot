using System.Text.RegularExpressions;
using be_service.Models;
using Microsoft.Extensions.Options;

using be_service.Abstractions;

namespace be_service.Services;

public class RagChatService
{
    private readonly IChatService _ollamaService;
    private readonly ILogger<RagChatService> _logger;
    private readonly QueryAnalyzerService _queryAnalyzerService;
    private readonly QueryUnderstandingService _queryUnderstandingService;
    private readonly AnswerFormatterService _answerFormatterService;
    private readonly PromptBuilderService _promptBuilderService;
    private readonly RetrievalService _retrievalService;
    // NEW: resolver injected for the grounded abstention gate (Option A).
    private readonly StructuredEntityResolver _structuredEntityResolver;
    private readonly RagModeOptions _ragMode;

    public RagChatService(
        IChatService ollamaService,
        ILogger<RagChatService> logger,
        QueryAnalyzerService queryAnalyzerService,
        QueryUnderstandingService queryUnderstandingService,
        AnswerFormatterService answerFormatterService,
        PromptBuilderService promptBuilderService,
        RetrievalService retrievalService,
        StructuredEntityResolver structuredEntityResolver, // NEW: corpus grounding for the gate
        IOptions<RagModeOptions> ragMode)
    {
        _ollamaService = ollamaService;
        _logger = logger;
        _queryAnalyzerService = queryAnalyzerService;
        _queryUnderstandingService = queryUnderstandingService;
        _answerFormatterService = answerFormatterService;
        _promptBuilderService = promptBuilderService;
        _retrievalService = retrievalService;
        _structuredEntityResolver = structuredEntityResolver; // NEW
        _ragMode = ragMode.Value;
    }

    public async Task<ChatResponse> AskAsync(string question)
    {
        question = (question ?? "").Trim();

        if (string.IsNullOrWhiteSpace(question))
            return NotFoundResponse();

        // Mode="Semantic" → LLM-based analysis; everything else → keyword cascade
        RagQueryAnalysis analysis;
        if (_ragMode.IsSemanticQueryMode)
        {
            // NEW: deterministic fast-path BEFORE the LLM. Hard identifiers and
            // real person-name lookups are answered by the keyword analyzer in ~ms
            // and correctly; calling the LLM for "siapa <nama>" is both slow (2-5s)
            // and unreliable (it mislabels them as profile/policy). Only falls
            // through to the LLM when the query is genuinely ambiguous/natural.
            var fastPath = await TryFastStructuralAsync(question);
            if (fastPath is not null)
            {
                analysis = fastPath;
            }
            else
            {
                analysis = await _queryUnderstandingService.AnalyzeAsync(question);
                LogFallbackUsage(analysis);
                if (_ragMode.ShadowCompare)
                    await ShadowCompareLegacyAsync(analysis);
                // Drop hallucinated hard identifiers, then run the corpus-grounding
                // gate (slot values) and the intent-sanity gate (no anchor/slot/id).
                StripPhantomIdentifiers(analysis);
                await ApplyGroundingGateAsync(analysis);
                ApplyIntentSanityGate(analysis);
            }
        }
        else
        {
            analysis = await _queryAnalyzerService.AnalyzeAsync(question);
        }

        // Blocked check before any retrieval path
        if (analysis.IsBlocked)
            return new ChatResponse
            {
                Answer = string.IsNullOrWhiteSpace(analysis.BlockReason)
                    ? "Maaf, saya tidak menemukan informasi tersebut."
                    : analysis.BlockReason,
                Sources = []
            };

        // Hybrid & Semantic: SemanticGrounded queries go to pure vector search
        if (_ragMode.UsesSemanticDispatch && analysis.AnswerLevel == AnswerLevel.SemanticGrounded)
            return await AskSemanticAsync(analysis.Question);

        return await AskLegacyAsync(analysis);
    }

    // --- Semantic-mode measurement: fallback rate + Semantic-vs-Legacy diff ---

    private void LogFallbackUsage(RagQueryAnalysis analysis)
    {
        var fellBack = analysis.AnalysisSource is "fallback-parse" or "fallback-error";
        _logger.LogInformation(
            "QUS_SOURCE source={Source}, fellBack={FellBack}",
            analysis.AnalysisSource,
            fellBack);
    }

    // NEW: deterministic fast-path for high-precision structured queries — returns
    // a ready analysis (skipping the LLM) for hard identifiers (NIK/MT/date) and
    // person-name lookups whose name actually exists in the corpus; returns null
    // when the query should go to the LLM. The name check is grounded against real
    // corpus names so it does NOT hijack generic questions that merely look
    // name-shaped (e.g. "siapa supervisor di perusahaan ini").
    private async Task<RagQueryAnalysis?> TryFastStructuralAsync(string question)
    {
        // Keyword analyzer is ~1ms and deterministic — cheap enough to probe first.
        var ka = await _queryAnalyzerService.AnalyzeAsync(question);

        var hasHardIdentifier =
            !string.IsNullOrWhiteSpace(ka.Nik) ||
            !string.IsNullOrWhiteSpace(ka.MaintenanceCode) ||
            !string.IsNullOrWhiteSpace(ka.Date);

        if (hasHardIdentifier)
        {
            ka.AnalysisSource = "fast-path"; // mark provenance for logs/metrics
            _logger.LogInformation("QUS_FASTPATH reason=identifier");
            return ka;
        }

        // Person-name lookup, but only when the name is real in the corpus.
        if (ka.LooksLikePersonName &&
            !string.IsNullOrWhiteSpace(ka.PersonKeyword) &&
            await _structuredEntityResolver.IsKnownValueAsync("name", ka.PersonKeyword))
        {
            ka.AnalysisSource = "fast-path";
            _logger.LogInformation("QUS_FASTPATH reason=known-name name={Name}", ka.PersonKeyword);
            return ka;
        }

        return null;
    }

    // NEW: grounded abstention gate (Option A). Prevents the planner from blindly
    // trusting an LLM-predicted structured route. If the extracted categorical
    // slots don't exist in the live Qdrant corpus AND there is no hard identifier,
    // downgrade AnswerLevel to SemanticGrounded so the query goes to hybrid vector
    // search instead of an empty/dummy-biased structured filter. Grounded against
    // real corpus values → dataset-agnostic, no hardcoded vocabulary.
    private async Task ApplyGroundingGateAsync(RagQueryAnalysis analysis)
    {
        // Blocked queries never reach retrieval — skip the corpus lookup.
        if (analysis.IsBlocked)
            return;

        // Hard identifiers (NIK / MT code / date) are exact — always trust them.
        var hasHardIdentifier =
            !string.IsNullOrWhiteSpace(analysis.Nik) ||
            !string.IsNullOrWhiteSpace(analysis.MaintenanceCode) ||
            !string.IsNullOrWhiteSpace(analysis.Date);
        if (hasHardIdentifier)
            return;

        // Only entity-filtered structured intents are gated. sop/audit/profile,
        // semantic, and name-only routes are left untouched.
        var isEntityFilteredStructured =
            (analysis.IsEmployeeQuery || analysis.IsOvertimeQuery || analysis.IsMaintenanceQuery) &&
            analysis.AnswerLevel == AnswerLevel.ExactStructured;
        if (!isEntityFilteredStructured)
            return;

        // Collect filled categorical slots (open-ended person name is excluded —
        // an unknown name legitimately means "not found", not a domain mismatch).
        var slots = new List<(string Field, string Value)>();
        void AddSlot(string field, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                slots.Add((field, value));
        }
        AddSlot("division", analysis.Division);
        AddSlot("shift", analysis.Shift);
        AddSlot("employeeStatus", analysis.EmployeeStatus);
        AddSlot("position", analysis.Position);
        AddSlot("approval", analysis.Approval);
        AddSlot("location", analysis.Location);
        AddSlot("maintenanceStatus", analysis.MaintenanceStatus);
        AddSlot("technician", analysis.Technician);
        // NOTE: equipment not gated — RagQueryAnalysis has no Equipment slot yet
        // (LLM equipment entity is currently dropped; revisit in Option B).

        // No categorical slot to verify (e.g. "list semua karyawan") → leave as is;
        // an empty structured result still falls through to vector search downstream.
        if (slots.Count == 0)
            return;

        // Keep the structured route if ANY slot resolves to a real corpus value.
        foreach (var (field, value) in slots)
        {
            if (await _structuredEntityResolver.IsKnownValueAsync(field, value))
                return;
        }

        // None resolved → the LLM invented a structured route the corpus can't
        // serve → abstain to hybrid vector search.
        _logger.LogInformation(
            "GROUNDING_GATE downgrade=ExactStructured->SemanticGrounded, unresolvedSlots={Slots}",
            string.Join(",", slots.Select(s => $"{s.Field}={s.Value}")));

        analysis.AnswerLevel = AnswerLevel.SemanticGrounded;
    }

    // Guard 1: the LLM sometimes invents hard identifiers (e.g. a "date" for
    // "kapan ..."). Keep Nik/MaintenanceCode/Date only when the pattern actually
    // appears in the query text — otherwise it would misroute to an exact lookup.
    private static void StripPhantomIdentifiers(RagQueryAnalysis analysis)
    {
        var q = analysis.Question ?? "";

        if (!string.IsNullOrWhiteSpace(analysis.Nik) &&
            !Regex.IsMatch(q, @"\bRU\s*6\s*-?\s*\d{4}\b", RegexOptions.IgnoreCase))
            analysis.Nik = "";

        if (!string.IsNullOrWhiteSpace(analysis.MaintenanceCode) &&
            !Regex.IsMatch(q, @"\bMT\s*-?\s*\d{3}\b", RegexOptions.IgnoreCase))
            analysis.MaintenanceCode = "";

        if (!string.IsNullOrWhiteSpace(analysis.Date) &&
            !Regex.IsMatch(q, @"\b\d{2}-\d{2}-\d{4}\b"))
            analysis.Date = "";
    }

    // Anchor keywords per intent — a query routed to a structured/dummy intent is
    // expected to contain at least one of these. Used by the intent-sanity guard.
    private static readonly Dictionary<string, string[]> IntentAnchors =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["employee"] = new[] { "karyawan", "pegawai", "staff", "jabatan", "divisi", "shift" },
            ["overtime"] = new[] { "lembur", "overtime" },
            ["maintenance"] = new[] { "maintenance", "perawatan", "teknisi", "peralatan", "alat" },
            ["audit"] = new[] { "audit", "kepatuhan", "pelanggaran", "anomali", "logbook" },
            ["sop"] = new[] { "sop", "prosedur", "apd", "keselamatan", "safety",
                                      "briefing", "evakuasi", "kecepatan", "area", "tangki", "akses", "kilang", "gate" },
            ["profile"] = new[] { "profil", "perusahaan", "unit", "kapasitas", "produksi", "direktur" },
        };

    // Guard 2: if the LLM picked a structured/dummy intent but the query carries no
    // anchoring evidence for it (no domain keyword, no resolvable slot, no hard ID),
    // the intent is likely hallucinated → route to hybrid semantic instead of a
    // dummy structured path. Behavior-preserving for dummy queries (which always
    // carry a domain keyword, a slot, or an identifier).
    private void ApplyIntentSanityGate(RagQueryAnalysis analysis)
    {
        if (analysis.IsBlocked || analysis.AnswerLevel == AnswerLevel.SemanticGrounded)
            return;

        var hasHardIdentifier =
            !string.IsNullOrWhiteSpace(analysis.Nik) ||
            !string.IsNullOrWhiteSpace(analysis.MaintenanceCode) ||
            !string.IsNullOrWhiteSpace(analysis.Date);
        if (hasHardIdentifier)
            return;

        var intent = ResolveIntentName(analysis);
        if (intent is null || !IntentAnchors.TryGetValue(intent, out var anchors))
            return;

        var q = analysis.Question ?? "";

        // Keep the structured route when the query carries a domain keyword …
        if (anchors.Any(k => q.Contains(k, StringComparison.OrdinalIgnoreCase)))
            return;

        // … or a *grounded* slot, i.e. a slot whose value actually appears in the
        // query. A slot the LLM invented (value not in the text — e.g. approval
        // "Disetujui" for a procurement-limit question) does NOT count.
        if (HasGroundedSlot(analysis))
            return;

        _logger.LogInformation(
            "INTENT_SANITY downgrade intent={Intent}->SemanticGrounded (no anchor/grounded-slot/id)",
            intent);

        analysis.AnswerLevel = AnswerLevel.SemanticGrounded;
    }

    // A slot is "grounded" when one of its value tokens appears in the query text.
    private static bool HasGroundedSlot(RagQueryAnalysis a)
    {
        var q = a.Question ?? "";
        var values = new[]
        {
            a.Division, a.Shift, a.EmployeeStatus, a.Position, a.Approval,
            a.Location, a.MaintenanceStatus, a.Technician, a.PersonKeyword
        };

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            // Significant tokens only (>=2 letters) to avoid matching on "A"/"B".
            foreach (Match token in Regex.Matches(value, @"[A-Za-z]{2,}"))
            {
                if (q.Contains(token.Value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static string? ResolveIntentName(RagQueryAnalysis a)
    {
        if (a.IsEmployeeQuery) return "employee";
        if (a.IsOvertimeQuery) return "overtime";
        if (a.IsMaintenanceQuery) return "maintenance";
        if (a.IsAuditQuery) return "audit";
        if (a.IsSopQuery) return "sop";
        if (a.IsProfileQuery) return "profile";
        return null;
    }

    // Runs the legacy keyword analyzer alongside the LLM analyzer and logs how
    // their outputs differ. Skipped when the LLM already fell back (the result
    // would be identical by construction). Measurement only — does not affect
    // the response returned to the caller.
    private async Task ShadowCompareLegacyAsync(RagQueryAnalysis semantic)
    {
        if (semantic.AnalysisSource is "fallback-parse" or "fallback-error")
        {
            _logger.LogInformation("QUS_VS_LEGACY skipped=fallback diffs=(identical-by-construction)");
            return;
        }

        try
        {
            var legacy = await _queryAnalyzerService.AnalyzeAsync(semantic.Question);
            var diffs = DiffAnalyses(semantic, legacy);

            _logger.LogInformation(
                "QUS_VS_LEGACY match={Match}, diffCount={Count}, diffs={Diffs}",
                diffs.Count == 0,
                diffs.Count,
                diffs.Count == 0 ? "(none)" : string.Join("; ", diffs));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "QUS_VS_LEGACY shadow-compare failed");
        }
    }

    // Returns "field:semanticValue|legacyValue" for each field that differs.
    private static List<string> DiffAnalyses(RagQueryAnalysis s, RagQueryAnalysis l)
    {
        var diffs = new List<string>();

        void Cmp(string field, object? a, object? b)
        {
            var av = a?.ToString() ?? "";
            var bv = b?.ToString() ?? "";
            if (!string.Equals(av, bv, StringComparison.OrdinalIgnoreCase))
                diffs.Add($"{field}:{av}|{bv}");
        }

        Cmp("answerLevel", s.AnswerLevel, l.AnswerLevel);
        Cmp("isEmployee", s.IsEmployeeQuery, l.IsEmployeeQuery);
        Cmp("isOvertime", s.IsOvertimeQuery, l.IsOvertimeQuery);
        Cmp("isMaintenance", s.IsMaintenanceQuery, l.IsMaintenanceQuery);
        Cmp("isSop", s.IsSopQuery, l.IsSopQuery);
        Cmp("isProfile", s.IsProfileQuery, l.IsProfileQuery);
        Cmp("isAudit", s.IsAuditQuery, l.IsAuditQuery);
        Cmp("isPolicy", s.IsPolicyQuestion, l.IsPolicyQuestion);
        Cmp("nik", s.Nik, l.Nik);
        Cmp("code", s.MaintenanceCode, l.MaintenanceCode);
        Cmp("date", s.Date, l.Date);
        Cmp("division", s.Division, l.Division);
        Cmp("shift", s.Shift, l.Shift);
        Cmp("status", s.EmployeeStatus, l.EmployeeStatus);
        Cmp("position", s.Position, l.Position);
        Cmp("approval", s.Approval, l.Approval);
        Cmp("location", s.Location, l.Location);
        Cmp("technician", s.Technician, l.Technician);
        Cmp("person", s.PersonKeyword, l.PersonKeyword);
        Cmp("targetType", s.TargetRecordType, l.TargetRecordType);
        Cmp("genericType", s.GenericRecordType, l.GenericRecordType);
        Cmp("blocked", s.IsBlocked, l.IsBlocked);

        return diffs;
    }

    // --- Semantic path (Hybrid mode only) ---

    private async Task<ChatResponse> AskSemanticAsync(string question)
    {
        _logger.LogInformation(
            "SEMANTIC_PATH query length={Length}", question.Length);

        var result = await _retrievalService.SearchSemanticOnlyAsync(question);

        if (!result.Chunks.Any())
            return NotFoundResponse();

        var prompt = _promptBuilderService.BuildSemanticPrompt(question, result.Chunks);
        var answer = await _ollamaService.GenerateChatAsync(prompt);

        if (IsNotFoundAnswer(answer))
            return NotFoundResponse();

        return new ChatResponse
        {
            Answer = answer,
            Sources = GetSources(result.Chunks)
        };
    }

    // --- Legacy path (default; unchanged behaviour) ---

    private async Task<ChatResponse> AskLegacyAsync(RagQueryAnalysis analysis)
    {
        var question = analysis.Question;

        _logger.LogInformation(
            "RAG query received, length={QueryLength}",
            question.Length);

        var retrievalResult = await _retrievalService.RetrieveAsync(analysis);

        if (retrievalResult.AnswerLevel == AnswerLevel.Blocked ||
            retrievalResult.RetrievalMode == "blocked")
        {
            return new ChatResponse
            {
                Answer = string.IsNullOrWhiteSpace(retrievalResult.BlockReason)
                    ? "Maaf, saya tidak menemukan informasi tersebut."
                    : retrievalResult.BlockReason,
                Sources = new List<string>()
            };
        }

        var relevantChunks = retrievalResult.Chunks;
        var retrievalMode = retrievalResult.RetrievalMode;

        _logger.LogInformation(
            "RAG retrieval mode={RetrievalMode}, chunks={ChunkCount}",
            retrievalMode,
            relevantChunks.Count);

        foreach (var chunk in relevantChunks)
        {
            _logger.LogInformation(
                "RAG chunk type={RecordType}, score={Score:F2}, chunkIndex={ChunkIndex}",
                ResolveRecordType(chunk),
                chunk.Similarity,
                chunk.ChunkIndex);
        }

        var personalTypes = relevantChunks
            .Select(ResolveRecordType)
            .Where(t => t is "employee" or "overtime")
            .Distinct()
            .OrderBy(t => t)
            .ToList();

        if (personalTypes.Any())
        {
            _logger.LogInformation(
                "PERSONAL_DATA_ACCESS queryLength={QueryLength}, recordTypes={RecordTypes}, chunkCount={ChunkCount}, isBlocked=false",
                question.Length,
                string.Join(",", personalTypes),
                relevantChunks.Count);
        }

        if (!relevantChunks.Any())
        {
            LogAnswerTrace(
                retrievalResult,
                usedDeterministicAnswer: false,
                usedLlm: false);

            return NotFoundResponse();
        }

        var deterministicAnswer = _answerFormatterService.TryBuildDeterministicAnswer(
            relevantChunks,
            retrievalMode,
            question);

        if (!string.IsNullOrWhiteSpace(deterministicAnswer))
        {
            LogAnswerTrace(
                retrievalResult,
                usedDeterministicAnswer: true,
                usedLlm: false);

            return new ChatResponse
            {
                Answer = deterministicAnswer,
                Sources = GetSources(relevantChunks),
            };
        }

        var prompt = retrievalResult.AnswerLevel == AnswerLevel.PolicyGrounded
            ? _promptBuilderService.BuildPolicyGroundedPrompt(question, relevantChunks)
            : _promptBuilderService.Build(question, relevantChunks);

        var answer = await _ollamaService.GenerateChatAsync(prompt);

        if (IsNotFoundAnswer(answer))
        {
            LogAnswerTrace(
                retrievalResult,
                usedDeterministicAnswer: false,
                usedLlm: true);

            return NotFoundResponse();
        }

        LogAnswerTrace(
            retrievalResult,
            usedDeterministicAnswer: false,
            usedLlm: true);

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

    private static string ResolveRecordType(RetrievedChunk chunk)
    {
        return string.IsNullOrWhiteSpace(chunk.RecordType)
            ? ChunkMetadataExtractor.DetectRecordType(chunk.Content)
            : chunk.RecordType;
    }

    private void LogAnswerTrace(
        RagRetrievalResult retrievalResult,
        bool usedDeterministicAnswer,
        bool usedLlm)
    {
        _logger.LogInformation(
            "ANSWER_TRACE answerLevel={AnswerLevel}, retrievalMode={RetrievalMode}, source={RetrievalSource}, qdrantVectorSearch={QdrantVectorSearch}, usedDeterministicAnswer={UsedDeterministicAnswer}, usedLlm={UsedLlm}",
            retrievalResult.AnswerLevel,
            retrievalResult.RetrievalMode,
            retrievalResult.RetrievalSource,
            retrievalResult.QdrantVectorSearch,
            usedDeterministicAnswer,
            usedLlm);
    }

}
