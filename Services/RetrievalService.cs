using be_service.Models;

namespace be_service.Services;

public class RetrievalService
{
    private readonly QdrantService _qdrantService;
    private readonly OllamaService _ollamaService;
    private readonly ILogger<RetrievalService> _logger;

    private const float MinimumSemanticSimilarity = 0.50f;

    public RetrievalService(
        QdrantService qdrantService,
        OllamaService ollamaService,
        ILogger<RetrievalService> logger)
    {
        _qdrantService = qdrantService;
        _ollamaService = ollamaService;
        _logger = logger;
    }

    public async Task<RagRetrievalResult> RetrieveAsync(RagQueryAnalysis analysis)
    {
        var chunks = new List<RetrievedChunk>();
        var retrievalMode = "semantic";
        var contextLimit = 5;

        /*
         * Routing order matters:
         * Before: SOP was checked before Audit, so queries such as "kepatuhan APD"
         * could be routed to SOP because APD is also a SOP keyword.
         * After: Audit is checked before SOP because audit/compliance queries are
         * more specific than broad SOP/security queries.
         */
        if (!string.IsNullOrWhiteSpace(analysis.Nik))
        {
            chunks = await _qdrantService.SearchByNikAsync(analysis.Nik);
            retrievalMode = "exact-nik";
            contextLimit = 1;
        }
        else if (!string.IsNullOrWhiteSpace(analysis.MaintenanceCode))
        {
            chunks = await _qdrantService.SearchByMaintenanceCodeAsync(analysis.MaintenanceCode);
            retrievalMode = "exact-maintenance-code";
            contextLimit = 1;
        }
        else if (!string.IsNullOrWhiteSpace(analysis.Date))
        {
            chunks = await _qdrantService.SearchByDateAsync(analysis.Date);
            retrievalMode = "exact-date";
            contextLimit = 10;
        }
        else if (analysis.IsAuditQuery)
        {
            chunks = await _qdrantService.SearchByRecordTypeAsync(
                "audit",
                "",
                3);

            retrievalMode = "audit";
            contextLimit = 3;
        }
        else if (analysis.IsSopQuery)
        {
            chunks = await _qdrantService.SearchByRecordTypeAsync(
                "sop",
                analysis.SopKeyword,
                5);

            retrievalMode = "sop";
            contextLimit = 5;
        }
        else if (analysis.IsProfileQuery)
        {
            chunks = await _qdrantService.SearchByRecordTypeAsync(
                "profile",
                analysis.ProfileKeyword,
                5);

            if (!chunks.Any() && !string.IsNullOrWhiteSpace(analysis.ProfileKeyword))
            {
                chunks = await _qdrantService.SearchByRecordTypeAsync(
                    "profile",
                    "",
                    5);
            }

            retrievalMode = "profile";
            contextLimit = 5;
        }
        else
        {
            if (analysis.IsEmployeeQuery && !string.IsNullOrWhiteSpace(analysis.Division))
            {
                chunks = await _qdrantService.SearchEmployeesByDivisionAsync(analysis.Division);
                retrievalMode = "employee_by_division";
                contextLimit = 50;
            }
            else if (analysis.IsEmployeeQuery && !string.IsNullOrWhiteSpace(analysis.Shift))
            {
                chunks = await _qdrantService.SearchEmployeesByShiftAsync(analysis.Shift);
                retrievalMode = "employee_by_shift";
                contextLimit = 50;
            }
            else if (analysis.IsEmployeeQuery && !string.IsNullOrWhiteSpace(analysis.EmployeeStatus))
            {
                chunks = await _qdrantService.SearchEmployeesByStatusAsync(analysis.EmployeeStatus);
                retrievalMode = "employee_by_status";
                contextLimit = 50;
            }
            else if (analysis.IsEmployeeQuery && !string.IsNullOrWhiteSpace(analysis.Position))
            {
                chunks = await _qdrantService.SearchEmployeesByPositionAsync(analysis.Position);
                retrievalMode = "employee_by_position";
                contextLimit = 50;
            }
            else if ((analysis.IsOvertimeQuery || analysis.Question.Contains("approval", StringComparison.OrdinalIgnoreCase)) &&
                     !string.IsNullOrWhiteSpace(analysis.Approval))
            {
                chunks = await _qdrantService.SearchOvertimeByApprovalAsync(analysis.Approval);
                retrievalMode = "overtime_by_approval";
                contextLimit = 50;
            }
            else if (analysis.IsOvertimeQuery && !string.IsNullOrWhiteSpace(analysis.Division))
            {
                chunks = await _qdrantService.SearchOvertimeByDivisionAsync(analysis.Division);
                retrievalMode = "overtime_by_division";
                contextLimit = 50;
            }
            else if (analysis.IsMaintenanceQuery && !string.IsNullOrWhiteSpace(analysis.MaintenanceStatus))
            {
                chunks = await _qdrantService.SearchMaintenanceByStatusAsync(analysis.MaintenanceStatus);
                retrievalMode = "maintenance_by_status";
                contextLimit = 50;
            }
            else if (analysis.IsMaintenanceQuery && !string.IsNullOrWhiteSpace(analysis.Location))
            {
                chunks = await _qdrantService.SearchMaintenanceByLocationAsync(analysis.Location);
                retrievalMode = "maintenance_by_location";
                contextLimit = 50;
            }
            else if (analysis.IsMaintenanceQuery &&
                     analysis.Question.Contains("teknisi", StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(analysis.Technician))
            {
                chunks = await _qdrantService.SearchMaintenanceByTechnicianAsync(analysis.Technician);
                retrievalMode = "maintenance_by_technician";
                contextLimit = 50;
            }
            else if (analysis.LooksLikePersonName)
            {
                if (analysis.IsOvertimeQuery)
                {
                    chunks = await _qdrantService.SearchOvertimeByNameAsync(
                        analysis.PersonKeyword,
                        10);

                    retrievalMode = "exact-name-overtime";
                }

                if (!chunks.Any())
                {
                    chunks = await _qdrantService.SearchByNameAsync(
                        analysis.PersonKeyword,
                        10);

                    retrievalMode = "exact-name";
                }

                contextLimit = 10;
            }
        }

        if (!chunks.Any())
        {
            var embedding = await _ollamaService.GenerateEmbeddingAsync(analysis.Question);

            chunks = await _qdrantService.SearchSemanticAsync(embedding, 5);

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
            "Retrieval mode={RetrievalMode}, chunks={ChunkCount}",
            retrievalMode,
            relevantChunks.Count);

        return new RagRetrievalResult
        {
            Chunks = relevantChunks,
            RetrievalMode = retrievalMode,
            ContextLimit = contextLimit
        };
    }
}
