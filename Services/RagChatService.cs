using be_service.Models;

namespace be_service.Services;

public class RagChatService
{
    private readonly OllamaService _ollamaService;
    private readonly ILogger<RagChatService> _logger;
    private readonly QueryAnalyzerService _queryAnalyzerService;
    private readonly AnswerFormatterService _answerFormatterService;
    private readonly PromptBuilderService _promptBuilderService;
    private readonly RetrievalService _retrievalService;

    public RagChatService(
        OllamaService ollamaService,
        ILogger<RagChatService> logger,
         QueryAnalyzerService queryAnalyzerService,
         AnswerFormatterService answerFormatterService,
         PromptBuilderService promptBuilderService,
         RetrievalService retrievalService)
    {
        _ollamaService = ollamaService;
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

        _logger.LogInformation(
            "RAG query received, length={QueryLength}",
            question.Length);

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
                "RAG chunk type={RecordType}, score={Score:F2}, chunkIndex={ChunkIndex}",
                ResolveRecordType(chunk),
                chunk.Similarity,
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

}
