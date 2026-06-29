using be_service.Models;
using Microsoft.Extensions.Options;
using be_service.Abstractions;
using Microsoft.Extensions.Logging;

namespace be_service.Services;

public class DocumentIngestionOrchestrator
{
    private readonly TextNormalizer _textNormalizer;
    private readonly ChunkingService _chunkingService;
    private readonly EmbeddingIngestionService _embeddingIngestionService;
    private readonly IVectorStore _qdrantService;
    private readonly IChunkStore _chunkStore;
    private readonly StorageModeOptions _storageModeOptions;
    private readonly DatasetSchemaOptions _schema;
    private readonly bool _hybridSearchEnabled;
    private readonly IDocumentRepository _documentRepository;
    private readonly ILogger<DocumentIngestionOrchestrator> _logger;

    public DocumentIngestionOrchestrator(
        TextNormalizer textNormalizer,
        ChunkingService chunkingService,
        EmbeddingIngestionService embeddingIngestionService,
        IVectorStore qdrantService,
        IChunkStore chunkStore,
        IDocumentRepository documentRepository,
        IOptions<StorageModeOptions> storageModeOptions,
        IOptions<RetrievalOptions> retrievalOptions,
        IOptions<DatasetSchemaOptions> datasetSchema,
        ILogger<DocumentIngestionOrchestrator> logger)
    {
        _textNormalizer = textNormalizer;
        _chunkingService = chunkingService;
        _embeddingIngestionService = embeddingIngestionService;
        _qdrantService = qdrantService;
        _documentRepository = documentRepository;
        _chunkStore = chunkStore;
        _storageModeOptions = storageModeOptions.Value;
        _schema = datasetSchema.Value;
        _hybridSearchEnabled = retrievalOptions.Value.HybridSearchEnabled;
        _logger = logger;
    }

    public async Task<Guid> IngestAsync(IngestRequest request)
    {
        await _qdrantService.EnsureCollectionAsync();

        var documentId = await _documentRepository.CreateAsync(
            request.Title,
            request.Department);

        await TryUpdateDocumentStorageMetadataAsync(documentId, request);

        var normalizedContent = _textNormalizer.Normalize(request.Content);
        var chunks = _chunkingService.Chunk(normalizedContent);
        _logger.LogDebug("TOTAL CHUNKS: {ChunkCount}", chunks.Count);

        for (int i = 0; i < chunks.Count; i++)
        {
            _logger.LogDebug(
                "CHUNK {Index}: recordType={RecordType}, chunkType={ChunkType}, sectionTitle={SectionTitle}, length={Length}",
                i,
                ChunkMetadataExtractor.DetectRecordType(chunks[i].Content),
                chunks[i].ChunkType,
                chunks[i].SectionTitle,
                chunks[i].Content.Length);
        }

        _logger.LogInformation(
            "INGESTION_STORAGE_MODE writeDocumentChunksToPostgres={WriteDocumentChunksToPostgres}",
            _storageModeOptions.WriteDocumentChunksToPostgres);

        for (int i = 0; i < chunks.Count; i++)
        {
            var chunkId = Guid.NewGuid();
            var chunk = CreateRetrievedChunk(
                chunkId,
                documentId,
                request.Title,
                request.Department,
                chunks[i],
                i);

            if (_storageModeOptions.WriteDocumentChunksToPostgres)
            {
                await _chunkStore.InsertChunkAsync(chunk);
            }

            var embedding = await _embeddingIngestionService.GenerateEmbeddingAsync(chunks[i].Content);

            Dictionary<uint, float>? sparseVector = null;
            if (_hybridSearchEnabled)
                sparseVector = _embeddingIngestionService.GenerateSparseVector(chunks[i].Content);

            await _qdrantService.UpsertChunkAsync(chunk, embedding, sparseVector);
        }

        return documentId;
    }

    private async Task TryUpdateDocumentStorageMetadataAsync(
        Guid documentId,
        IngestRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.StorageBucket) ||
            string.IsNullOrWhiteSpace(request.StorageObjectKey))
        {
            return;
        }

        await _documentRepository.UpdateStorageMetadataAsync(
            documentId,
            request.StorageBucket,
            request.StorageObjectKey,
            request.ContentType);
    }

    private RetrievedChunk CreateRetrievedChunk(
        Guid chunkId,
        Guid documentId,
        string documentTitle,
        string department,
        ContentChunk piece,
        int chunkIndex)
    {
        var content = piece.Content;
        var recordType = ChunkMetadataExtractor.DetectRecordType(content);

        // Prefer the chunker-provided section title / chunk type (provenance);
        // fall back to content-based detection when the chunker left them blank.
        var sectionTitle = string.IsNullOrWhiteSpace(piece.SectionTitle)
            ? ChunkMetadataExtractor.ExtractSectionTitle(content)
            : piece.SectionTitle;

        var chunkType = string.IsNullOrWhiteSpace(piece.ChunkType)
            ? ChunkMetadataExtractor.DetectChunkType(content)
            : piece.ChunkType;

        // Schema-driven extraction: only the fields belonging to this recordType.
        var datasetFields = ChunkMetadataExtractor.ExtractFields(
            content,
            _schema.Find(recordType)?.Fields ?? Enumerable.Empty<DatasetField>());

        var chunk = new RetrievedChunk
        {
            Id = chunkId,
            DocumentId = documentId,
            DocumentTitle = documentTitle,
            Content = content,
            Similarity = 1.0f,
            RecordType = recordType,
            Department = department,
            SectionTitle = sectionTitle,
            ChunkType = chunkType,
            ChunkIndex = chunkIndex,
            DatasetFields = datasetFields
        };

        // Mirror dataset fields onto the typed legacy properties so the optional
        // Postgres chunk store still works. Unknown-for-recordType fields stay
        // empty (the desired cleanup — no cross-type values).
        chunk.Nik = datasetFields.GetValueOrDefault("nik", "");
        chunk.Name = datasetFields.GetValueOrDefault("name", "");
        chunk.MaintenanceCode = datasetFields.GetValueOrDefault("maintenanceCode", "");
        chunk.Date = datasetFields.GetValueOrDefault("date", "");
        chunk.Division = datasetFields.GetValueOrDefault("division", "");
        chunk.Position = datasetFields.GetValueOrDefault("position", "");
        chunk.Shift = datasetFields.GetValueOrDefault("shift", "");
        chunk.EmployeeStatus = datasetFields.GetValueOrDefault("employeeStatus", "");
        chunk.Duration = datasetFields.GetValueOrDefault("duration", "");
        chunk.Approval = datasetFields.GetValueOrDefault("approval", "");
        chunk.Equipment = datasetFields.GetValueOrDefault("equipment", "");
        chunk.Location = datasetFields.GetValueOrDefault("location", "");
        chunk.MaintenanceStatus = datasetFields.GetValueOrDefault("maintenanceStatus", "");
        chunk.Technician = datasetFields.GetValueOrDefault("technician", "");

        return chunk;
    }
}
