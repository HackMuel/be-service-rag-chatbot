using be_service.Models;
using be_service.Repositories;
using Microsoft.Extensions.Options;

namespace be_service.Services;

public class RetrievalService
{
    private readonly QdrantService _qdrantService;
    private readonly OllamaService _ollamaService;
    private readonly StructuredEntityResolver _structuredEntityResolver;
    private readonly RetrievalOptions _retrievalOptions;
    private readonly ILogger<RetrievalService> _logger;

    public RetrievalService(
        QdrantService qdrantService,
        OllamaService ollamaService,
        ChunkRepository chunkRepository,
        StructuredEntityResolver structuredEntityResolver,
        IOptions<RetrievalOptions> retrievalOptions,
        ILogger<RetrievalService> logger)
    {
        _qdrantService = qdrantService;
        _ollamaService = ollamaService;
        _structuredEntityResolver = structuredEntityResolver;
        _retrievalOptions = retrievalOptions.Value;
        _logger = logger;
    }

    public async Task<RagRetrievalResult> RetrieveAsync(RagQueryAnalysis analysis)
    {
        if (analysis.IsBlocked)
        {
            _logger.LogInformation(
                "RETRIEVAL_BLOCKED reason={Reason}",
                analysis.BlockReason);

            return new RagRetrievalResult
            {
                Chunks = new(),
                RetrievalMode = "blocked",
                AnswerLevel = AnswerLevel.Blocked,
                RetrievalSource = "none",
                BlockReason = analysis.BlockReason
            };
        }

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
            chunks = NormalizeQdrantChunks(
                "exact-nik",
                await _qdrantService.SearchByNikAsync(analysis.Nik));
            retrievalMode = "exact-nik";
            retrievalSource = "qdrant_payload";
            contextLimit = 1;
        }
        else if (!string.IsNullOrWhiteSpace(analysis.MaintenanceCode))
        {
            chunks = NormalizeQdrantChunks(
                "exact-maintenance-code",
                await _qdrantService.SearchByMaintenanceCodeAsync(analysis.MaintenanceCode));
            retrievalMode = "exact-maintenance-code";
            retrievalSource = "qdrant_payload";
            contextLimit = 1;
        }
        else if (!string.IsNullOrWhiteSpace(analysis.Date))
        {
            chunks = NormalizeQdrantChunks(
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
                chunks = NormalizeQdrantChunks(
                    "employee_by_division",
                    await _qdrantService.SearchEmployeesByDivisionAsync(analysis.Division));
                retrievalMode = "employee_by_division";
                retrievalSource = "qdrant_payload";
                contextLimit = StructuredDefaultLimit;
            }
            else if (analysis.IsEmployeeQuery && !string.IsNullOrWhiteSpace(analysis.Shift))
            {
                chunks = NormalizeQdrantChunks(
                    "employee_by_shift",
                    await _qdrantService.SearchEmployeesByShiftAsync(analysis.Shift));
                retrievalMode = "employee_by_shift";
                retrievalSource = "qdrant_payload";
                contextLimit = StructuredDefaultLimit;
            }
            else if (analysis.IsEmployeeQuery && !string.IsNullOrWhiteSpace(analysis.EmployeeStatus))
            {
                chunks = NormalizeQdrantChunks(
                    "employee_by_status",
                    await _qdrantService.SearchEmployeesByStatusAsync(analysis.EmployeeStatus));
                retrievalMode = "employee_by_status";
                retrievalSource = "qdrant_payload";
                contextLimit = StructuredDefaultLimit;
            }
            else if (analysis.IsEmployeeQuery && !string.IsNullOrWhiteSpace(analysis.Position))
            {
                chunks = NormalizeQdrantChunks(
                    "employee_by_position",
                    await _qdrantService.SearchEmployeesByPositionAsync(analysis.Position));
                retrievalMode = "employee_by_position";
                retrievalSource = "qdrant_payload";
                contextLimit = StructuredDefaultLimit;
            }
            else if ((analysis.IsOvertimeQuery || analysis.Question.Contains("approval", StringComparison.OrdinalIgnoreCase)) &&
                     !string.IsNullOrWhiteSpace(analysis.Approval))
            {
                chunks = NormalizeQdrantChunks(
                    "overtime_by_approval",
                    await _qdrantService.SearchOvertimeByApprovalAsync(analysis.Approval));
                retrievalMode = "overtime_by_approval";
                retrievalSource = "qdrant_payload";
                contextLimit = StructuredDefaultLimit;
            }
            else if (analysis.IsOvertimeQuery && !string.IsNullOrWhiteSpace(analysis.Division))
            {
                chunks = NormalizeQdrantChunks(
                    "overtime_by_division",
                    await _qdrantService.SearchOvertimeByDivisionAsync(analysis.Division));
                retrievalMode = "overtime_by_division";
                retrievalSource = "qdrant_payload";
                contextLimit = StructuredDefaultLimit;
            }
            else if (analysis.IsMaintenanceQuery && !string.IsNullOrWhiteSpace(analysis.MaintenanceStatus))
            {
                chunks = NormalizeQdrantChunks(
                    "maintenance_by_status",
                    await _qdrantService.SearchMaintenanceByStatusAsync(analysis.MaintenanceStatus));
                retrievalMode = "maintenance_by_status";
                retrievalSource = "qdrant_payload";
                contextLimit = StructuredDefaultLimit;
            }
            else if (analysis.IsMaintenanceQuery && !string.IsNullOrWhiteSpace(analysis.Location))
            {
                chunks = NormalizeQdrantChunks(
                    "maintenance_by_location",
                    await _qdrantService.SearchMaintenanceByLocationAsync(analysis.Location));
                retrievalMode = "maintenance_by_location";
                retrievalSource = "qdrant_payload";
                contextLimit = StructuredDefaultLimit;
            }
            else if (analysis.IsMaintenanceQuery &&
                     analysis.Question.Contains("teknisi", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(analysis.Technician))
            {
                chunks = NormalizeQdrantChunks(
                    "maintenance_by_technician",
                    await _qdrantService.SearchMaintenanceByTechnicianAsync(analysis.Technician));
                retrievalMode = "maintenance_by_technician";
                retrievalSource = "qdrant_payload";
                contextLimit = StructuredDefaultLimit;
            }
            else if (analysis.LooksLikePersonName)
            {
                if (analysis.IsOvertimeQuery)
                {
                    chunks = NormalizeQdrantChunks(
                        "exact-name-overtime",
                        await _qdrantService.SearchOvertimeByNameAsync(
                            analysis.PersonKeyword,
                            10));

                    retrievalMode = "exact-name-overtime";
                    retrievalSource = "qdrant_payload";
                }

                if (!chunks.Any())
                {
                    chunks = NormalizeQdrantChunks(
                        "exact-name",
                        await _qdrantService.SearchByNameAsync(
                            analysis.PersonKeyword,
                            10));

                    retrievalMode = "exact-name";
                    retrievalSource = "qdrant_payload";
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
                retrievalSource = "qdrant_payload";
                contextLimit = resolved.ContextLimit;
            }
        }

        if (!chunks.Any() &&
            !hasHardExactIdentifier &&
            !string.IsNullOrWhiteSpace(analysis.GenericRecordType))
        {
            chunks = await SearchGenericRecordTypeAsync(analysis.GenericRecordType);
            retrievalMode = $"{analysis.GenericRecordType}_general";
            retrievalSource = "qdrant_payload";
            contextLimit = GetGenericContextLimit(analysis.GenericRecordType);
        }

        if (!chunks.Any() && analysis.AnswerLevel != AnswerLevel.ExactStructured)
        {
            var embedding = await _ollamaService.GenerateEmbeddingAsync(analysis.Question);

            var vectorHits = await _qdrantService.SearchSemanticAsync(embedding, SemanticTopK);
            qdrantVectorSearch = true;

            vectorHits = vectorHits
                .Where(x => x.Similarity >= SemanticScoreThreshold)
                .ToList();

            chunks = GetSemanticChunksFromQdrantPayload(vectorHits);
            chunks = FilterSemanticContext(analysis, chunks);

            retrievalMode = "semantic";
            retrievalSource = "qdrant_vector_payload";
            contextLimit = SemanticMaxContextChunks;
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
                NormalizeQdrantChunks(
                    "exact-name-overtime",
                    await _qdrantService.SearchOvertimeByNameAsync(entity.Value, 10)),

            "name" =>
                NormalizeQdrantChunks(
                    "exact-name",
                    await _qdrantService.SearchByNameAsync(entity.Value, 10)),

            "division" when analysis.IsOvertimeQuery =>
                NormalizeQdrantChunks(
                    "overtime_by_division",
                    await _qdrantService.SearchOvertimeByDivisionAsync(entity.Value)),

            "division" =>
                NormalizeQdrantChunks(
                    "employee_by_division",
                    await _qdrantService.SearchEmployeesByDivisionAsync(entity.Value)),

            "shift" =>
                NormalizeQdrantChunks(
                    "employee_by_shift",
                    await _qdrantService.SearchEmployeesByShiftAsync(entity.Value)),

            "employeeStatus" =>
                NormalizeQdrantChunks(
                    "employee_by_status",
                    await _qdrantService.SearchEmployeesByStatusAsync(entity.Value)),

            "position" =>
                NormalizeQdrantChunks(
                    "employee_by_position",
                    await _qdrantService.SearchEmployeesByPositionAsync(entity.Value)),

            "approval" =>
                NormalizeQdrantChunks(
                    "overtime_by_approval",
                    await _qdrantService.SearchOvertimeByApprovalAsync(entity.Value)),

            "maintenanceStatus" =>
                NormalizeQdrantChunks(
                    "maintenance_by_status",
                    await _qdrantService.SearchMaintenanceByStatusAsync(entity.Value)),

            "location" =>
                NormalizeQdrantChunks(
                    "maintenance_by_location",
                    await _qdrantService.SearchMaintenanceByLocationAsync(entity.Value)),

            "technician" =>
                NormalizeQdrantChunks(
                    "maintenance_by_technician",
                    await _qdrantService.SearchMaintenanceByTechnicianAsync(entity.Value)),

            "equipment" =>
                NormalizeQdrantChunks(
                    "maintenance_by_equipment",
                    await _qdrantService.SearchMaintenanceByEquipmentAsync(entity.Value)),

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
        var chunks = await _qdrantService.SearchByRecordTypeAsync(recordType, limit);

        return NormalizeQdrantChunks(recordType, chunks);
    }

    private async Task<List<RetrievedChunk>> SearchQdrantRecordTypeAsync(
        string recordType,
        int limit)
    {
        var chunks = await _qdrantService.SearchByRecordTypeAsync(recordType, limit);

        return NormalizeQdrantChunks(recordType, chunks);
    }

    private async Task<List<RetrievedChunk>> SearchQdrantRecordTypeAsync(
        string recordType,
        string keyword,
        int limit)
    {
        var chunks = await _qdrantService.SearchByRecordTypeAsync(recordType, keyword, limit);

        return NormalizeQdrantChunks(recordType, chunks);
    }

    private List<RetrievedChunk> NormalizeQdrantChunks(
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
                "Qdrant payload missing. Re-ingest required. lookup={LookupName}, hits={HitCount}, withPayload={PayloadCount}",
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

    private int GetStructuredEntityContextLimit(string fieldName)
    {
        return fieldName == "name" ? 10 : StructuredDefaultLimit;
    }

    private int GetGenericContextLimit(string recordType)
    {
        return recordType is "employee" or "overtime" or "maintenance"
            ? StructuredDefaultLimit
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
            .Where(x => x.Similarity >= SemanticScoreThreshold)
            .OrderByDescending(x => x.Similarity)
            .ThenBy(x => x.ChunkIndex ?? int.MaxValue)
            .Take(SemanticMaxContextChunks)
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

    private static string ResolveRecordType(RetrievedChunk chunk) =>
        QueryHelpers.ResolveRecordType(chunk);

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

    private static bool ContainsAny(string value, params string[] keywords) =>
        QueryHelpers.ContainsAny(value, keywords);

    private int SemanticTopK => _retrievalOptions.SemanticTopK > 0
        ? _retrievalOptions.SemanticTopK
        : RetrievalOptions.DefaultSemanticTopK;

    private float SemanticScoreThreshold => _retrievalOptions.SemanticScoreThreshold > 0
        ? _retrievalOptions.SemanticScoreThreshold
        : RetrievalOptions.DefaultSemanticScoreThreshold;

    private int SemanticMaxContextChunks => _retrievalOptions.SemanticMaxContextChunks > 0
        ? _retrievalOptions.SemanticMaxContextChunks
        : RetrievalOptions.DefaultSemanticMaxContextChunks;

    private int StructuredDefaultLimit => _retrievalOptions.StructuredDefaultLimit > 0
        ? _retrievalOptions.StructuredDefaultLimit
        : RetrievalOptions.DefaultStructuredDefaultLimit;
}
