using be_service.Models;
using Microsoft.Extensions.Options;

namespace be_service.Services;

public class RagChatService
{
    private readonly OllamaService _ollamaService;
    private readonly ILogger<RagChatService> _logger;
    private readonly QueryAnalyzerService _queryAnalyzerService;
    private readonly QueryUnderstandingService _queryUnderstandingService;
    private readonly AnswerFormatterService _answerFormatterService;
    private readonly PromptBuilderService _promptBuilderService;
    private readonly RetrievalService _retrievalService;
    private readonly RagModeOptions _ragMode;

    public RagChatService(
        OllamaService ollamaService,
        ILogger<RagChatService> logger,
        QueryAnalyzerService queryAnalyzerService,
        QueryUnderstandingService queryUnderstandingService,
        AnswerFormatterService answerFormatterService,
        PromptBuilderService promptBuilderService,
        RetrievalService retrievalService,
        IOptions<RagModeOptions> ragMode)
    {
        _ollamaService = ollamaService;
        _logger = logger;
        _queryAnalyzerService = queryAnalyzerService;
        _queryUnderstandingService = queryUnderstandingService;
        _answerFormatterService = answerFormatterService;
        _promptBuilderService = promptBuilderService;
        _retrievalService = retrievalService;
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
            analysis = await _queryUnderstandingService.AnalyzeAsync(question);
            LogFallbackUsage(analysis);
            await ShadowCompareLegacyAsync(analysis);
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
