using be_service.Models;
using be_service.Repositories;
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

    public DocumentIngestionOrchestrator(
        IConfiguration configuration,
        TextNormalizer textNormalizer,
        ChunkingService chunkingService,
        EmbeddingIngestionService embeddingIngestionService,
        QdrantService qdrantService,
        ChunkRepository chunkRepository)
    {
        _configuration = configuration;
        _textNormalizer = textNormalizer;
        _chunkingService = chunkingService;
        _embeddingIngestionService = embeddingIngestionService;
        _qdrantService = qdrantService;
        _chunkRepository = chunkRepository;
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

        var normalizedContent = _textNormalizer.Normalize(request.Content);
        var chunks = _chunkingService.SplitBySections(normalizedContent);
        Console.WriteLine($"TOTAL CHUNKS: {chunks.Count}");

        for (int i = 0; i < chunks.Count; i++)
        {
            Console.WriteLine(
                $"CHUNK {i}: type={ChunkMetadataExtractor.DetectRecordType(chunks[i])}, length={chunks[i].Length}");
        }

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

            await _chunkRepository.InsertChunkAsync(chunk);

            var embedding = await _embeddingIngestionService.GenerateEmbeddingAsync(chunks[i]);

            await _qdrantService.UpsertChunkAsync(
                chunk,
                embedding);
        }

        return documentId;
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
