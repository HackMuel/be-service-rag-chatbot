using be_service.Models;
using Npgsql;

namespace be_service.Services;

public class DocumentIngestionOrchestrator
{
    private readonly IConfiguration _configuration;
    private readonly TextNormalizer _textNormalizer;
    private readonly ChunkingService _chunkingService;
    private readonly EmbeddingIngestionService _embeddingIngestionService;
    private readonly QdrantService _qdrantService;

    public DocumentIngestionOrchestrator(
        IConfiguration configuration,
        TextNormalizer textNormalizer,
        ChunkingService chunkingService,
        EmbeddingIngestionService embeddingIngestionService,
        QdrantService qdrantService)
    {
        _configuration = configuration;
        _textNormalizer = textNormalizer;
        _chunkingService = chunkingService;
        _embeddingIngestionService = embeddingIngestionService;
        _qdrantService = qdrantService;
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
            var embedding = await _embeddingIngestionService.GenerateEmbeddingAsync(chunks[i]);
            var chunkId = Guid.NewGuid();

            await _qdrantService.UpsertChunkAsync(
                chunkId,
                documentId,
                request.Title,
                chunks[i],
                embedding,
                i,
                request.Department
            );
        }

        return documentId;
    }
}
