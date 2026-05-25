using be_service.Models;
using be_service.Repositories;

namespace be_service.Services;

public class RetrievalService
{
    private readonly QdrantService _qdrantService;
    private readonly OllamaService _ollamaService;
    private readonly ChunkRepository _chunkRepository;
    private readonly StructuredEntityResolver _structuredEntityResolver;
    private readonly ILogger<RetrievalService> _logger;

    private const float MinimumSemanticSimilarity = 0.55f;
    private const int SemanticContextLimit = 3;

    public RetrievalService(
        QdrantService qdrantService,
        OllamaService ollamaService,
        ChunkRepository chunkRepository,
        StructuredEntityResolver structuredEntityResolver,
        ILogger<RetrievalService> logger)
    {
        _qdrantService = qdrantService;
        _ollamaService = ollamaService;
        _chunkRepository = chunkRepository;
        _structuredEntityResolver = structuredEntityResolver;
        _logger = logger;
    }

    public async Task<RagRetrievalResult> RetrieveAsync(RagQueryAnalysis analysis)
    {
        var chunks = new List<RetrievedChunk>();
        var retrievalMode = "semantic";
        var contextLimit = 5;
        var retrievalSource = "";
        var qdrantVectorSearch = false;
        var hasHardExactIdentifier =
            !string.IsNullOrWhiteSpace(analysis.Nik) ||
            !string.IsNullOrWhiteSpace(analysis.MaintenanceCode) ||
            !string.IsNullOrWhiteSpace(analysis.Date);

        /*
         * Routing order matters:
         * Before: SOP was checked before Audit, so queries such as "kepatuhan APD"
         * could be routed to SOP because APD is also a SOP keyword.
         * After: Audit is checked before SOP because audit/compliance queries are
         * more specific than broad SOP/security queries.
        */
        if (!string.IsNullOrWhiteSpace(analysis.Nik))
        {
            chunks = NormalizeQdrantExactChunks(
                "exact-nik",
                await _qdrantService.SearchByNikAsync(analysis.Nik));
            retrievalMode = "exact-nik";
            retrievalSource = "qdrant_payload";
            contextLimit = 1;
        }
        else if (!string.IsNullOrWhiteSpace(analysis.MaintenanceCode))
        {
            chunks = NormalizeQdrantExactChunks(
                "exact-maintenance-code",
                await _qdrantService.SearchByMaintenanceCodeAsync(analysis.MaintenanceCode));
            retrievalMode = "exact-maintenance-code";
            retrievalSource = "qdrant_payload";
            contextLimit = 1;
        }
        else if (!string.IsNullOrWhiteSpace(analysis.Date))
        {
            chunks = NormalizeQdrantExactChunks(
                "exact-date",
                await _qdrantService.SearchByDateAsync(analysis.Date));
            retrievalMode = "exact-date";
            retrievalSource = "qdrant_payload";
            contextLimit = 10;
        }
        else if (analysis.IsAuditQuery)
        {
            chunks = await SearchQdrantRecordTypeAsync("audit", "", 3);

            retrievalMode = analysis.GenericRecordType == "audit" ? "audit_general" : "audit";
            retrievalSource = "qdrant_payload";
            contextLimit = 3;
        }
        else if (analysis.IsSopQuery)
        {
            chunks = await SearchQdrantRecordTypeAsync(
                "sop",
                analysis.SopKeyword,
                5);

            if (!chunks.Any() && !string.IsNullOrWhiteSpace(analysis.SopKeyword))
            {
                chunks = await SearchQdrantRecordTypeAsync("sop", 5);
            }

            retrievalMode = analysis.GenericRecordType == "sop" ? "sop_general" : "sop";
            retrievalSource = "qdrant_payload";
            contextLimit = 5;
        }
        else if (analysis.IsProfileQuery)
        {
            chunks = await SearchQdrantRecordTypeAsync(
                "profile",
                analysis.ProfileKeyword,
                5);

            if (!chunks.Any() && !string.IsNullOrWhiteSpace(analysis.ProfileKeyword))
            {
                chunks = await SearchQdrantRecordTypeAsync("profile", 5);
            }

            retrievalMode = "profile";
            retrievalSource = "qdrant_payload";
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

        if (!chunks.Any() &&
            !hasHardExactIdentifier &&
            ShouldTryStructuredEntityResolver(analysis))
        {
            var resolved = await TryRetrieveByStructuredEntityAsync(analysis);

            if (resolved.Chunks.Any())
            {
                chunks = resolved.Chunks;
                retrievalMode = resolved.RetrievalMode;
                retrievalSource = "postgres_exact";
                contextLimit = resolved.ContextLimit;
            }
        }

        if (!chunks.Any() &&
            !hasHardExactIdentifier &&
            !string.IsNullOrWhiteSpace(analysis.GenericRecordType))
        {
            chunks = await SearchGenericRecordTypeAsync(analysis.GenericRecordType);
            retrievalMode = $"{analysis.GenericRecordType}_general";
            retrievalSource = "postgres_record_type";
            contextLimit = GetGenericContextLimit(analysis.GenericRecordType);
        }

        if (!chunks.Any() && analysis.AnswerLevel != AnswerLevel.ExactStructured)
        {
            var embedding = await _ollamaService.GenerateEmbeddingAsync(analysis.Question);

            var vectorHits = await _qdrantService.SearchSemanticAsync(embedding, 5);
            qdrantVectorSearch = true;

            vectorHits = vectorHits
                .Where(x => x.Similarity >= MinimumSemanticSimilarity)
                .ToList();

            chunks = GetSemanticChunksFromQdrantPayload(vectorHits);
            chunks = FilterSemanticContext(analysis, chunks);

            retrievalMode = "semantic";
            retrievalSource = "qdrant_vector_payload";
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

    private async Task<(List<RetrievedChunk> Chunks, string RetrievalMode, int ContextLimit)> TryRetrieveByStructuredEntityAsync(
        RagQueryAnalysis analysis)
    {
        var entity = await _structuredEntityResolver.ResolveAsync(
            analysis.Question,
            analysis);

        if (entity is null)
        {
            return (new List<RetrievedChunk>(), "", 0);
        }

        var chunks = entity.FieldName switch
        {
            "name" when analysis.IsOvertimeQuery =>
                await _chunkRepository.SearchOvertimeByNameAsync(entity.Value, 10),

            "name" =>
                await _chunkRepository.SearchByNameAsync(entity.Value, 10),

            "division" when analysis.IsOvertimeQuery =>
                await _chunkRepository.SearchOvertimeByDivisionAsync(entity.Value),

            "division" =>
                await _chunkRepository.SearchEmployeesByDivisionAsync(entity.Value),

            "shift" =>
                await _chunkRepository.SearchEmployeesByShiftAsync(entity.Value),

            "employeeStatus" =>
                await _chunkRepository.SearchEmployeesByStatusAsync(entity.Value),

            "position" =>
                await _chunkRepository.SearchEmployeesByPositionAsync(entity.Value),

            "approval" =>
                await _chunkRepository.SearchOvertimeByApprovalAsync(entity.Value),

            "maintenanceStatus" =>
                await _chunkRepository.SearchMaintenanceByStatusAsync(entity.Value),

            "location" =>
                await _chunkRepository.SearchMaintenanceByLocationAsync(entity.Value),

            "technician" =>
                await _chunkRepository.SearchMaintenanceByTechnicianAsync(entity.Value),

            "equipment" =>
                await _chunkRepository.SearchMaintenanceByEquipmentAsync(entity.Value),

            _ => new List<RetrievedChunk>()
        };

        if (!chunks.Any())
        {
            return (chunks, "", 0);
        }

        _logger.LogInformation(
            "STRUCTURED_ENTITY_TRACE field={FieldName}, recordType={RecordType}, chunks={ChunkCount}",
            entity.FieldName,
            entity.RecordType,
            chunks.Count);

        return (
            chunks,
            BuildRetrievalMode(entity, analysis),
            GetStructuredEntityContextLimit(entity.FieldName));
    }

    private static bool ShouldTryStructuredEntityResolver(RagQueryAnalysis analysis)
    {
        return !analysis.IsSopQuery &&
               !analysis.IsAuditQuery &&
               !analysis.IsProfileQuery &&
               !analysis.IsPolicyQuestion;
    }

    private async Task<List<RetrievedChunk>> SearchGenericRecordTypeAsync(string recordType)
    {
        var limit = GetGenericContextLimit(recordType);

        return await _chunkRepository.SearchByRecordTypeAsync(
            recordType,
            "",
            limit);
    }

    private async Task<List<RetrievedChunk>> SearchQdrantRecordTypeAsync(
        string recordType,
        int limit)
    {
        var chunks = await _qdrantService.SearchByRecordTypeAsync(recordType, limit);

        return NormalizeQdrantRecordTypeChunks(recordType, chunks);
    }

    private async Task<List<RetrievedChunk>> SearchQdrantRecordTypeAsync(
        string recordType,
        string keyword,
        int limit)
    {
        var chunks = await _qdrantService.SearchByRecordTypeAsync(recordType, keyword, limit);

        return NormalizeQdrantRecordTypeChunks(recordType, chunks);
    }

    private List<RetrievedChunk> NormalizeQdrantRecordTypeChunks(
        string recordType,
        List<RetrievedChunk> chunks)
    {
        if (!chunks.Any())
        {
            return chunks;
        }

        var chunksWithPayload = chunks
            .Where(HasQdrantPayload)
            .ToList();

        if (chunksWithPayload.Count != chunks.Count)
        {
            _logger.LogWarning(
                "Qdrant recordType payload missing. Re-ingest required. recordType={RecordType}, hits={HitCount}, withPayload={PayloadCount}",
                recordType,
                chunks.Count,
                chunksWithPayload.Count);
        }

        return chunksWithPayload
            .GroupBy(GetQdrantRecordDedupKey)
            .Select(x => x.First())
            .ToList();
    }

    private List<RetrievedChunk> NormalizeQdrantExactChunks(
        string lookupName,
        List<RetrievedChunk> chunks)
    {
        if (!chunks.Any())
        {
            return chunks;
        }

        var chunksWithPayload = chunks
            .Where(HasQdrantPayload)
            .ToList();

        if (chunksWithPayload.Count != chunks.Count)
        {
            _logger.LogWarning(
                "Qdrant exact payload missing. Re-ingest required. lookup={LookupName}, hits={HitCount}, withPayload={PayloadCount}",
                lookupName,
                chunks.Count,
                chunksWithPayload.Count);
        }

        return chunksWithPayload
            .GroupBy(GetQdrantRecordDedupKey)
            .Select(x => x.First())
            .ToList();
    }

    private static string GetQdrantRecordDedupKey(RetrievedChunk chunk)
    {
        if (chunk.Id != Guid.Empty)
        {
            return chunk.Id.ToString();
        }

        if (chunk.DocumentId != Guid.Empty && chunk.ChunkIndex.HasValue)
        {
            return $"{chunk.DocumentId}:{chunk.ChunkIndex.Value}";
        }

        return $"{chunk.DocumentTitle}:{chunk.ChunkIndex?.ToString() ?? "-"}";
    }

    private static string BuildRetrievalMode(
        StructuredEntityMatch entity,
        RagQueryAnalysis analysis)
    {
        return entity.FieldName switch
        {
            "name" when analysis.IsOvertimeQuery => "exact-name-overtime",
            "name" => "exact-name",
            "division" when analysis.IsOvertimeQuery => "overtime_by_division",
            "division" => "employee_by_division",
            "shift" => "employee_by_shift",
            "employeeStatus" => "employee_by_status",
            "position" => "employee_by_position",
            "approval" => "overtime_by_approval",
            "maintenanceStatus" => "maintenance_by_status",
            "location" => "maintenance_by_location",
            "technician" => "maintenance_by_technician",
            "equipment" => "maintenance_by_equipment",
            _ => "structured_entity"
        };
    }

    private static int GetStructuredEntityContextLimit(string fieldName)
    {
        return fieldName == "name" ? 10 : 50;
    }

    private static int GetGenericContextLimit(string recordType)
    {
        return recordType is "employee" or "overtime" or "maintenance"
            ? 50
            : 5;
    }

    private List<RetrievedChunk> GetSemanticChunksFromQdrantPayload(
        List<RetrievedChunk> vectorHits)
    {
        if (!vectorHits.Any())
        {
            return new List<RetrievedChunk>();
        }

        var chunksWithPayload = vectorHits
            .Where(HasQdrantPayload)
            .ToList();

        if (chunksWithPayload.Count != vectorHits.Count)
        {
            _logger.LogWarning(
                "Qdrant semantic result missing payload. Re-ingest required. hits={HitCount}, withPayload={PayloadCount}",
                vectorHits.Count,
                chunksWithPayload.Count);
        }

        return chunksWithPayload;
    }

    private static bool HasQdrantPayload(RetrievedChunk chunk)
    {
        return !string.IsNullOrWhiteSpace(chunk.Content);
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
