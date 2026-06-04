using be_service.Models;
using be_service.Repositories;
using Microsoft.Extensions.Options;
using Npgsql;

namespace be_service.Services;

public class DocumentIngestionOrchestrator
{
    private readonly IConfiguration _configuration;
    private readonly TextNormalizer _textNormalizer;
    private readonly ChunkingService _chunkingService;
    private readonly EmbeddingIngestionService _embeddingIngestionService;
    private readonly QdrantService _qdrantService;
    private readonly ChunkRepository _chunkRepository;
    private readonly StorageModeOptions _storageModeOptions;
    private readonly bool _hybridSearchEnabled;
    private readonly ILogger<DocumentIngestionOrchestrator> _logger;

    public DocumentIngestionOrchestrator(
        IConfiguration configuration,
        TextNormalizer textNormalizer,
        ChunkingService chunkingService,
        EmbeddingIngestionService embeddingIngestionService,
        QdrantService qdrantService,
        ChunkRepository chunkRepository,
        IOptions<StorageModeOptions> storageModeOptions,
        IOptions<RetrievalOptions> retrievalOptions,
        ILogger<DocumentIngestionOrchestrator> logger)
    {
        _configuration = configuration;
        _textNormalizer = textNormalizer;
        _chunkingService = chunkingService;
        _embeddingIngestionService = embeddingIngestionService;
        _qdrantService = qdrantService;
        _chunkRepository = chunkRepository;
        _storageModeOptions = storageModeOptions.Value;
        _hybridSearchEnabled = retrievalOptions.Value.HybridSearchEnabled;
        _logger = logger;
    }

    public async Task<Guid> IngestAsync(IngestRequest request)
    {
        var connectionString =
            _configuration.GetConnectionString("SupabaseDb");

        await _qdrantService.EnsureCollectionAsync();
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        Guid documentId;

        var insertDocumentSql = @"
            insert into documents (title, source_type, department)
            values (@title, 'text', @department)
            returning id;
        ";

        await using (var cmd = new NpgsqlCommand(insertDocumentSql, conn))
        {
            cmd.Parameters.AddWithValue("title", request.Title);
            cmd.Parameters.AddWithValue("department", request.Department);

            documentId = (Guid)(await cmd.ExecuteScalarAsync())!;
        }

        await TryUpdateDocumentStorageMetadataAsync(conn, documentId, request);

        var normalizedContent = _textNormalizer.Normalize(request.Content);
        var chunks = _chunkingService.SplitBySections(normalizedContent);
        _logger.LogDebug("TOTAL CHUNKS: {ChunkCount}", chunks.Count);

        for (int i = 0; i < chunks.Count; i++)
        {
            _logger.LogDebug(
                "CHUNK {Index}: type={RecordType}, length={Length}",
                i,
                ChunkMetadataExtractor.DetectRecordType(chunks[i]),
                chunks[i].Length);
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
                await _chunkRepository.InsertChunkAsync(chunk);
            }

            var embedding = await _embeddingIngestionService.GenerateEmbeddingAsync(chunks[i]);

            Dictionary<uint, float>? sparseVector = null;
            if (_hybridSearchEnabled)
                sparseVector = _embeddingIngestionService.GenerateSparseVector(chunks[i]);

            await _qdrantService.UpsertChunkAsync(chunk, embedding, sparseVector);
        }

        return documentId;
    }

    private async Task TryUpdateDocumentStorageMetadataAsync(
        NpgsqlConnection conn,
        Guid documentId,
        IngestRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.StorageBucket) ||
            string.IsNullOrWhiteSpace(request.StorageObjectKey))
        {
            return;
        }

        const string updateDocumentStorageSql = @"
            update documents
            set
                storage_bucket = @storage_bucket,
                storage_object_key = @storage_object_key,
                content_type = @content_type
            where id = @id;
        ";

        try
        {
            await using var cmd = new NpgsqlCommand(updateDocumentStorageSql, conn);
            cmd.Parameters.AddWithValue("id", documentId);
            cmd.Parameters.AddWithValue("storage_bucket", request.StorageBucket);
            cmd.Parameters.AddWithValue("storage_object_key", request.StorageObjectKey);
            cmd.Parameters.AddWithValue("content_type", request.ContentType);

            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedColumn)
        {
            _logger.LogWarning(
                "Document storage metadata columns are missing. Run Sql/add_object_storage_columns.sql to persist object storage metadata.");
        }
    }

    private static RetrievedChunk CreateRetrievedChunk(
        Guid chunkId,
        Guid documentId,
        string documentTitle,
        string department,
        string content,
        int chunkIndex)
    {
        return new RetrievedChunk
        {
            Id = chunkId,
            DocumentId = documentId,
            DocumentTitle = documentTitle,
            Content = content,
            Similarity = 1.0f,
            RecordType = ChunkMetadataExtractor.DetectRecordType(content),
            Nik = ChunkMetadataExtractor.ExtractNik(content),
            Name = ChunkMetadataExtractor.ExtractName(content),
            MaintenanceCode = ChunkMetadataExtractor.ExtractMaintenanceCode(content),
            Date = ChunkMetadataExtractor.ExtractDate(content),
            Division = ChunkMetadataExtractor.ExtractDivision(content),
            Department = department,
            Position = ChunkMetadataExtractor.ExtractPosition(content),
            Shift = ChunkMetadataExtractor.ExtractShift(content),
            EmployeeStatus = ChunkMetadataExtractor.ExtractEmployeeStatus(content),
            Duration = ChunkMetadataExtractor.ExtractDuration(content),
            Approval = ChunkMetadataExtractor.ExtractApproval(content),
            Equipment = ChunkMetadataExtractor.ExtractEquipment(content),
            Location = ChunkMetadataExtractor.ExtractLocation(content),
            MaintenanceStatus = ChunkMetadataExtractor.ExtractMaintenanceStatus(content),
            Technician = ChunkMetadataExtractor.ExtractTechnician(content),
            SectionTitle = ChunkMetadataExtractor.ExtractSectionTitle(content),
            ChunkIndex = chunkIndex
        };
    }
}
