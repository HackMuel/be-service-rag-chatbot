using be_service.Models;
using Npgsql;

namespace be_service.Services;

public class SupabaseRagService
{
    private readonly IConfiguration _configuration;

    public SupabaseRagService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<List<RetrievedChunk>> SearchRelevantChunksAsync(
        List<float> embedding,
        int matchCount = 5)
    {
        var results = new List<RetrievedChunk>();

        var connectionString =
            _configuration.GetConnectionString("SupabaseDb");

        await using var conn = new NpgsqlConnection(connectionString);

        await conn.OpenAsync();

        var sql = @"
    select *
    from match_document_chunks(@query_embedding::vector, @match_count);
";

        await using var cmd = new NpgsqlCommand(sql, conn);

        var embeddingString = "[" + string.Join(",", embedding) + "]";

        cmd.Parameters.AddWithValue("query_embedding", embeddingString);

        cmd.Parameters.AddWithValue("match_count", matchCount);

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            results.Add(new RetrievedChunk
            {
                Id = reader.GetGuid(0),
                DocumentId = reader.GetGuid(1),
                DocumentTitle = reader.GetString(2),
                Content = reader.GetString(3),
                Similarity = reader.GetFloat(4)
            });
        }

        return results;
    }

    public async Task<List<RetrievedChunk>> SearchByKeywordAsync(string keyword)
    {
        var results = new List<RetrievedChunk>();

        var connectionString =
            _configuration.GetConnectionString("SupabaseDb");

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();

        var sql = @"
        select
            dc.id,
            dc.document_id,
            d.title as document_title,
            dc.content,
            1.0::real as similarity
        from document_chunks dc
        join documents d on d.id = dc.document_id
        where dc.content ilike @keyword
        limit 10;
    ";

        await using var cmd = new NpgsqlCommand(sql, conn);

        cmd.Parameters.AddWithValue("keyword", $"%{keyword}%");

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            results.Add(new RetrievedChunk
            {
                Id = reader.GetGuid(0),
                DocumentId = reader.GetGuid(1),
                DocumentTitle = reader.GetString(2),
                Content = reader.GetString(3),
                Similarity = reader.GetFloat(4)
            });
        }

        return results;
    }
}