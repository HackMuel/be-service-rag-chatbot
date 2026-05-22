using be_service.Models;
using be_service.Repositories;

namespace be_service.Services;

public class RetrievalService
{
    private readonly QdrantService _qdrantService;
    private readonly OllamaService _ollamaService;
    private readonly ChunkRepository _chunkRepository;
    private readonly ILogger<RetrievalService> _logger;

    private const float MinimumSemanticSimilarity = 0.55f;
    private const int SemanticContextLimit = 3;

    public RetrievalService(
        QdrantService qdrantService,
        OllamaService ollamaService,
        ChunkRepository chunkRepository,
        ILogger<RetrievalService> logger)
    {
        _qdrantService = qdrantService;
        _ollamaService = ollamaService;
        _chunkRepository = chunkRepository;
        _logger = logger;
    }

    public async Task<RagRetrievalResult> RetrieveAsync(RagQueryAnalysis analysis)
    {
        var chunks = new List<RetrievedChunk>();
        var retrievalMode = "semantic";
        var contextLimit = 5;
        var retrievalSource = "";
        var qdrantVectorSearch = false;

        /*
         * Routing order matters:
         * Before: SOP was checked before Audit, so queries such as "kepatuhan APD"
         * could be routed to SOP because APD is also a SOP keyword.
         * After: Audit is checked before SOP because audit/compliance queries are
         * more specific than broad SOP/security queries.
        */
        if (!string.IsNullOrWhiteSpace(analysis.Nik))
        {
            chunks = await _chunkRepository.SearchByNikAsync(analysis.Nik);
            retrievalMode = "exact-nik";
            retrievalSource = "postgres_exact";
            contextLimit = 1;
        }
        else if (!string.IsNullOrWhiteSpace(analysis.MaintenanceCode))
        {
            chunks = await _chunkRepository.SearchByMaintenanceCodeAsync(analysis.MaintenanceCode);
            retrievalMode = "exact-maintenance-code";
            retrievalSource = "postgres_exact";
            contextLimit = 1;
        }
        else if (!string.IsNullOrWhiteSpace(analysis.Date))
        {
            chunks = await _chunkRepository.SearchByDateAsync(analysis.Date);
            retrievalMode = "exact-date";
            retrievalSource = "postgres_exact";
            contextLimit = 10;
        }
        else if (analysis.IsAuditQuery)
        {
            chunks = await _chunkRepository.SearchByRecordTypeAsync(
                "audit",
                "",
                3);

            retrievalMode = "audit";
            retrievalSource = "postgres_record_type";
            contextLimit = 3;
        }
        else if (analysis.IsSopQuery)
        {
            chunks = await _chunkRepository.SearchByRecordTypeAsync(
                "sop",
                analysis.SopKeyword,
                5);

            if (!chunks.Any() && !string.IsNullOrWhiteSpace(analysis.SopKeyword))
            {
                chunks = await _chunkRepository.SearchByRecordTypeAsync(
                    "sop",
                    "",
                    5);
            }

            retrievalMode = "sop";
            retrievalSource = "postgres_record_type";
            contextLimit = 5;
        }
        else if (analysis.IsProfileQuery)
        {
            chunks = await _chunkRepository.SearchByRecordTypeAsync(
                "profile",
                analysis.ProfileKeyword,
                5);

            if (!chunks.Any() && !string.IsNullOrWhiteSpace(analysis.ProfileKeyword))
            {
                chunks = await _chunkRepository.SearchByRecordTypeAsync(
                    "profile",
                    "",
                    5);
            }

            retrievalMode = "profile";
            retrievalSource = "postgres_record_type";
            contextLimit = 5;
        }
        else
        {
            if (analysis.IsEmployeeQuery && !string.IsNullOrWhiteSpace(analysis.Division))
            {
                chunks = await _chunkRepository.SearchEmployeesByDivisionAsync(analysis.Division);
                retrievalMode = "employee_by_division";
                retrievalSource = "postgres_exact";
                contextLimit = 50;
            }
            else if (analysis.IsEmployeeQuery && !string.IsNullOrWhiteSpace(analysis.Shift))
            {
                chunks = await _chunkRepository.SearchEmployeesByShiftAsync(analysis.Shift);
                retrievalMode = "employee_by_shift";
                retrievalSource = "postgres_exact";
                contextLimit = 50;
            }
            else if (analysis.IsEmployeeQuery && !string.IsNullOrWhiteSpace(analysis.EmployeeStatus))
            {
                chunks = await _chunkRepository.SearchEmployeesByStatusAsync(analysis.EmployeeStatus);
                retrievalMode = "employee_by_status";
                retrievalSource = "postgres_exact";
                contextLimit = 50;
            }
            else if (analysis.IsEmployeeQuery && !string.IsNullOrWhiteSpace(analysis.Position))
            {
                chunks = await _chunkRepository.SearchEmployeesByPositionAsync(analysis.Position);
                retrievalMode = "employee_by_position";
                retrievalSource = "postgres_exact";
                contextLimit = 50;
            }
            else if ((analysis.IsOvertimeQuery || analysis.Question.Contains("approval", StringComparison.OrdinalIgnoreCase)) &&
                     !string.IsNullOrWhiteSpace(analysis.Approval))
            {
                chunks = await _chunkRepository.SearchOvertimeByApprovalAsync(analysis.Approval);
                retrievalMode = "overtime_by_approval";
                retrievalSource = "postgres_exact";
                contextLimit = 50;
            }
            else if (analysis.IsOvertimeQuery && !string.IsNullOrWhiteSpace(analysis.Division))
            {
                chunks = await _chunkRepository.SearchOvertimeByDivisionAsync(analysis.Division);
                retrievalMode = "overtime_by_division";
                retrievalSource = "postgres_exact";
                contextLimit = 50;
            }
            else if (analysis.IsMaintenanceQuery && !string.IsNullOrWhiteSpace(analysis.MaintenanceStatus))
            {
                chunks = await _chunkRepository.SearchMaintenanceByStatusAsync(analysis.MaintenanceStatus);
                retrievalMode = "maintenance_by_status";
                retrievalSource = "postgres_exact";
                contextLimit = 50;
            }
            else if (analysis.IsMaintenanceQuery && !string.IsNullOrWhiteSpace(analysis.Location))
            {
                chunks = await _chunkRepository.SearchMaintenanceByLocationAsync(analysis.Location);
                retrievalMode = "maintenance_by_location";
                retrievalSource = "postgres_exact";
                contextLimit = 50;
            }
            else if (analysis.IsMaintenanceQuery &&
                     analysis.Question.Contains("teknisi", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(analysis.Technician))
            {
                chunks = await _chunkRepository.SearchMaintenanceByTechnicianAsync(analysis.Technician);
                retrievalMode = "maintenance_by_technician";
                retrievalSource = "postgres_exact";
                contextLimit = 50;
            }
            else if (analysis.LooksLikePersonName)
            {
                if (analysis.IsOvertimeQuery)
                {
                    chunks = await _chunkRepository.SearchOvertimeByNameAsync(
                        analysis.PersonKeyword,
                        10);

                    retrievalMode = "exact-name-overtime";
                    retrievalSource = "postgres_exact";
                }

                if (!chunks.Any())
                {
                    chunks = await _chunkRepository.SearchByNameAsync(
                        analysis.PersonKeyword,
                        10);

                    retrievalMode = "exact-name";
                    retrievalSource = "postgres_exact";
                }

                contextLimit = 10;
            }
        }

        if (!chunks.Any() && analysis.AnswerLevel != AnswerLevel.ExactStructured)
        {
            var embedding = await _ollamaService.GenerateEmbeddingAsync(analysis.Question);

            var vectorHits = await _qdrantService.SearchSemanticAsync(embedding, 5);
            qdrantVectorSearch = true;

            vectorHits = vectorHits
                .Where(x => x.Similarity >= MinimumSemanticSimilarity)
                .ToList();

            chunks = await GetSemanticChunksFromRepositoryAsync(vectorHits);
            chunks = FilterSemanticContext(analysis, chunks);

            retrievalMode = "semantic";
            retrievalSource = "qdrant_vector_then_postgres";
            contextLimit = SemanticContextLimit;
        }

        var relevantChunks = chunks
            .GroupBy(x => x.Id)
            .Select(x => x.First())
            .OrderByDescending(x => x.Similarity)
            .ThenBy(x => x.ChunkIndex ?? int.MaxValue)
            .Take(contextLimit)
            .ToList();

        _logger.LogInformation(
            "RETRIEVAL_TRACE answerLevel={AnswerLevel}, mode={RetrievalMode}, source={RetrievalSource}, qdrantVectorSearch={QdrantVectorSearch}, chunks={ChunkCount}",
            analysis.AnswerLevel,
            retrievalMode,
            retrievalSource,
            qdrantVectorSearch,
            relevantChunks.Count);

        return new RagRetrievalResult
        {
            Chunks = relevantChunks,
            RetrievalMode = retrievalMode,
            ContextLimit = contextLimit,
            AnswerLevel = analysis.AnswerLevel,
            RetrievalSource = retrievalSource,
            QdrantVectorSearch = qdrantVectorSearch
        };
    }

    private async Task<List<RetrievedChunk>> GetSemanticChunksFromRepositoryAsync(
        List<RetrievedChunk> vectorHits)
    {
        if (!vectorHits.Any())
        {
            return new List<RetrievedChunk>();
        }

        var ids = vectorHits
            .Select(x => x.Id)
            .Where(x => x != Guid.Empty)
            .ToList();

        if (!ids.Any())
        {
            return new List<RetrievedChunk>();
        }

        var scoreMap = vectorHits
            .Where(x => x.Id != Guid.Empty)
            .GroupBy(x => x.Id)
            .ToDictionary(x => x.Key, x => x.First().Similarity);

        var dbChunks = await _chunkRepository.GetChunksByIdsAsync(ids);

        foreach (var chunk in dbChunks)
        {
            if (scoreMap.TryGetValue(chunk.Id, out var score))
            {
                chunk.Similarity = score;
            }
        }

        return dbChunks
            .Where(x => scoreMap.ContainsKey(x.Id))
            .ToList();
    }

    private List<RetrievedChunk> FilterSemanticContext(
        RagQueryAnalysis analysis,
        List<RetrievedChunk> chunks)
    {
        var allowedTypes = GetAllowedSemanticRecordTypes(analysis);

        if (!chunks.Any())
        {
            LogSemanticFilter(0, 0, allowedTypes);
            return chunks;
        }

        var filteredChunks = chunks;

        if (allowedTypes.Any())
        {
            var matchingChunks = chunks
                .Where(chunk => allowedTypes.Contains(ResolveRecordType(chunk)))
                .ToList();

            if (matchingChunks.Any())
            {
                filteredChunks = matchingChunks;
            }
        }

        var result = filteredChunks
            .Where(x => x.Similarity >= MinimumSemanticSimilarity)
            .OrderByDescending(x => x.Similarity)
            .ThenBy(x => x.ChunkIndex ?? int.MaxValue)
            .Take(SemanticContextLimit)
            .ToList();

        LogSemanticFilter(chunks.Count, result.Count, allowedTypes);

        return result;
    }

    private static HashSet<string> GetAllowedSemanticRecordTypes(RagQueryAnalysis analysis)
    {
        if (IsGeneralSummaryQuery(analysis.Question))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "sop",
                "audit",
                "profile"
            };
        }

        if (IsSafetyRiskOperationalQuery(analysis.Question))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "sop",
                "audit"
            };
        }

        if (!string.IsNullOrWhiteSpace(analysis.TargetRecordType))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                analysis.TargetRecordType
            };
        }

        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsGeneralSummaryQuery(string question)
    {
        return ContainsAny(question, "ringkas", "rangkum", "gambaran umum");
    }

    private static bool IsSafetyRiskOperationalQuery(string question)
    {
        return ContainsAny(
            question,
            "keselamatan",
            "risiko",
            "pengawasan",
            "operasional",
            "prosedur",
            "insiden",
            "pencegahan",
            "pengendalian",
            "keamanan");
    }

    private static string ResolveRecordType(RetrievedChunk chunk)
    {
        return string.IsNullOrWhiteSpace(chunk.RecordType)
            ? ChunkMetadataExtractor.DetectRecordType(chunk.Content)
            : chunk.RecordType;
    }

    private void LogSemanticFilter(
        int before,
        int after,
        HashSet<string> allowedTypes)
    {
        var allowedTypesText = allowedTypes.Any()
            ? string.Join(",", allowedTypes.OrderBy(x => x))
            : "any";

        _logger.LogInformation(
            "SEMANTIC_FILTER before={BeforeCount} after={AfterCount} allowedTypes={AllowedTypes}",
            before,
            after,
            allowedTypesText);
    }

    private static bool ContainsAny(string value, params string[] keywords)
    {
        return keywords.Any(keyword =>
            value.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }
}
