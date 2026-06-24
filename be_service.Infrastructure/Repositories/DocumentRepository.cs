using be_service.Abstractions;
using Npgsql;

namespace be_service.Repositories;

public class DocumentRepository : IDocumentRepository
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<DocumentRepository> _logger;

    public DocumentRepository(IConfiguration configuration, ILogger<DocumentRepository> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connectionString = _configuration.GetConnectionString("SupabaseDb");
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        return conn;
    }

    public async Task<Guid> CreateAsync(string title, string department)
    {
        const string sql = @"
            insert into documents (title, source_type, department)
            values (@title, 'text', @department)
            returning id;
        ";
        await using var conn = await OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("title", title);
        cmd.Parameters.AddWithValue("department", department);
        return (Guid)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task UpdateStorageMetadataAsync(
        Guid documentId,
        string storageBucket,
        string storageObjectKey,
        string contentType)
    {
        const string sql = @"
            update documents
            set
                storage_bucket = @storage_bucket,
                storage_object_key = @storage_object_key,
                content_type = @content_type
            where id = @id;
        ";
        try
        {
            await using var conn = await OpenConnectionAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", documentId);
            cmd.Parameters.AddWithValue("storage_bucket", storageBucket);
            cmd.Parameters.AddWithValue("storage_object_key", storageObjectKey);
            cmd.Parameters.AddWithValue("content_type", contentType);
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedColumn)
        {
            _logger.LogWarning(
                "Document storage metadata columns are missing. Run Sql/add_object_storage_columns.sql to persist object storage metadata.");
        }
    }
}