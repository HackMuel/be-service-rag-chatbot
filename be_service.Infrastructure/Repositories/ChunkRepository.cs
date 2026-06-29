using be_service.Abstractions;
using be_service.Models;
using Npgsql;
using NpgsqlTypes;
using System.Text.Json;

namespace be_service.Repositories;

public class ChunkRepository : IChunkStore
{
    private readonly IConfiguration _configuration;

    public ChunkRepository(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task InsertChunkAsync(RetrievedChunk chunk)
    {
        await using var conn = await OpenConnectionAsync();

        const string sql = @"
            insert into document_chunks (
                id,
                document_id,
                chunk_index,
                content,
                metadata
            )
            values (
                @id,
                @document_id,
                @chunk_index,
                @content,
                @metadata
            );
        ";

        await using var cmd = new NpgsqlCommand(sql, conn);

        cmd.Parameters.Add("id", NpgsqlDbType.Uuid).Value = chunk.Id;
        cmd.Parameters.Add("document_id", NpgsqlDbType.Uuid).Value = chunk.DocumentId;
        cmd.Parameters.Add("chunk_index", NpgsqlDbType.Integer).Value = chunk.ChunkIndex ?? 0;
        cmd.Parameters.Add("content", NpgsqlDbType.Text).Value = chunk.Content;
        cmd.Parameters.Add("metadata", NpgsqlDbType.Jsonb).Value = BuildMetadataJson(chunk);

        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connectionString =
            _configuration.GetConnectionString("SupabaseDb");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Connection string 'SupabaseDb' is not configured.");
        }
        var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static string BuildMetadataJson(RetrievedChunk chunk)
    {
        var metadata = new Dictionary<string, string>
        {
            ["recordType"] = chunk.RecordType,
            ["nik"] = chunk.Nik,
            ["name"] = chunk.Name,
            ["nameNormalized"] = NormalizeKeyword(chunk.Name),
            ["maintenanceCode"] = chunk.MaintenanceCode,
            ["date"] = chunk.Date,
            ["division"] = chunk.Division,
            ["department"] = chunk.Department,
            ["position"] = chunk.Position,
            ["shift"] = chunk.Shift,
            ["employeeStatus"] = chunk.EmployeeStatus,
            ["duration"] = chunk.Duration,
            ["approval"] = chunk.Approval,
            ["equipment"] = chunk.Equipment,
            ["location"] = chunk.Location,
            ["maintenanceStatus"] = chunk.MaintenanceStatus,
            ["technician"] = chunk.Technician,
            ["sectionTitle"] = chunk.SectionTitle,
            ["documentTitle"] = chunk.DocumentTitle
        };

        var cleanMetadata = metadata
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .ToDictionary(x => x.Key, x => x.Value);

        return JsonSerializer.Serialize(cleanMetadata);
    }

    private static string NormalizeKeyword(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        return string.Join(
                " ",
                value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToUpperInvariant();
    }
}