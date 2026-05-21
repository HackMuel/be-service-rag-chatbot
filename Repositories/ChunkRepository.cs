using System.Text.Json;
using be_service.Models;
using Npgsql;
using NpgsqlTypes;

namespace be_service.Repositories;

public class ChunkRepository
{
    private static readonly HashSet<string> AllowedMetadataKeys = new(StringComparer.Ordinal)
    {
        "recordType",
        "nik",
        "name",
        "nameNormalized",
        "maintenanceCode",
        "date",
        "division",
        "department",
        "position",
        "shift",
        "employeeStatus",
        "duration",
        "approval",
        "equipment",
        "location",
        "maintenanceStatus",
        "technician",
        "sectionTitle",
        "documentTitle"
    };

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

    public async Task<List<RetrievedChunk>> GetChunksByIdsAsync(List<Guid> ids)
    {
        if (!ids.Any())
        {
            return new List<RetrievedChunk>();
        }

        await using var conn = await OpenConnectionAsync();

        const string sql = @"
            select
                dc.id,
                dc.document_id,
                dc.chunk_index,
                dc.content,
                dc.metadata,
                coalesce(dc.metadata->>'documentTitle', d.title, '') as document_title,
                1.0::real as similarity
            from unnest(@ids::uuid[]) with ordinality as input(id, ord)
            join document_chunks dc on dc.id = input.id
            left join documents d on d.id = dc.document_id
            order by input.ord;
        ";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add("ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid).Value = ids.ToArray();

        return await ReadChunksAsync(cmd);
    }

    public async Task<List<RetrievedChunk>> SearchByNikAsync(string nik)
    {
        return await SearchByMetadataAsync(new Dictionary<string, string>
        {
            ["nik"] = nik
        }, 5);
    }

    public async Task<List<RetrievedChunk>> SearchByMaintenanceCodeAsync(string code)
    {
        return await SearchByMetadataAsync(new Dictionary<string, string>
        {
            ["maintenanceCode"] = code
        }, 5);
    }

    public async Task<List<RetrievedChunk>> SearchByDateAsync(string date)
    {
        return await SearchByMetadataAsync(new Dictionary<string, string>
        {
            ["date"] = date
        }, 50);
    }

    public async Task<List<RetrievedChunk>> SearchByNameAsync(string name, int limit = 10)
    {
        return await SearchByMetadataAsync(new Dictionary<string, string>
        {
            ["nameNormalized"] = NormalizeKeyword(name)
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchEmployeesByDivisionAsync(string division, int limit = 50)
    {
        return await SearchByMetadataAsync(new Dictionary<string, string>
        {
            ["recordType"] = "employee",
            ["division"] = division
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchEmployeesByShiftAsync(string shift, int limit = 50)
    {
        return await SearchByMetadataAsync(new Dictionary<string, string>
        {
            ["recordType"] = "employee",
            ["shift"] = shift
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchEmployeesByStatusAsync(string status, int limit = 50)
    {
        return await SearchByMetadataAsync(new Dictionary<string, string>
        {
            ["recordType"] = "employee",
            ["employeeStatus"] = status
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchEmployeesByPositionAsync(string position, int limit = 50)
    {
        return await SearchByMetadataAsync(new Dictionary<string, string>
        {
            ["recordType"] = "employee",
            ["position"] = position
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchOvertimeByApprovalAsync(string approval, int limit = 50)
    {
        return await SearchByMetadataAsync(new Dictionary<string, string>
        {
            ["recordType"] = "overtime",
            ["approval"] = approval
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchOvertimeByDivisionAsync(string division, int limit = 50)
    {
        return await SearchByMetadataAsync(new Dictionary<string, string>
        {
            ["recordType"] = "overtime",
            ["division"] = division
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchOvertimeByNameAsync(string name, int limit = 50)
    {
        return await SearchByMetadataAsync(new Dictionary<string, string>
        {
            ["recordType"] = "overtime",
            ["nameNormalized"] = NormalizeKeyword(name)
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchMaintenanceByStatusAsync(string status, int limit = 50)
    {
        return await SearchByMetadataAsync(new Dictionary<string, string>
        {
            ["recordType"] = "maintenance",
            ["maintenanceStatus"] = status
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchMaintenanceByLocationAsync(string location, int limit = 50)
    {
        return await SearchByMetadataAsync(new Dictionary<string, string>
        {
            ["recordType"] = "maintenance",
            ["location"] = location
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchMaintenanceByTechnicianAsync(string technician, int limit = 50)
    {
        return await SearchByMetadataAsync(new Dictionary<string, string>
        {
            ["recordType"] = "maintenance",
            ["technician"] = technician
        }, limit);
    }

    public async Task<List<RetrievedChunk>> SearchByRecordTypeAsync(
        string recordType,
        string keyword,
        int limit = 10)
    {
        if (string.IsNullOrWhiteSpace(recordType))
        {
            return new List<RetrievedChunk>();
        }

        await using var conn = await OpenConnectionAsync();

        const string sqlWithoutKeyword = @"
            select
                dc.id,
                dc.document_id,
                dc.chunk_index,
                dc.content,
                dc.metadata,
                coalesce(dc.metadata->>'documentTitle', d.title, '') as document_title,
                1.0::real as similarity
            from document_chunks dc
            left join documents d on d.id = dc.document_id
            where dc.metadata->>'recordType' = @recordType
            order by dc.chunk_index asc
            limit @limit;
        ";

        const string sqlWithKeyword = @"
            select
                dc.id,
                dc.document_id,
                dc.chunk_index,
                dc.content,
                dc.metadata,
                coalesce(dc.metadata->>'documentTitle', d.title, '') as document_title,
                1.0::real as similarity
            from document_chunks dc
            left join documents d on d.id = dc.document_id
            where dc.metadata->>'recordType' = @recordType
              and dc.content ilike @keyword
            order by dc.chunk_index asc
            limit @limit;
        ";

        var hasKeyword = !string.IsNullOrWhiteSpace(keyword);
        var sql = hasKeyword ? sqlWithKeyword : sqlWithoutKeyword;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add("recordType", NpgsqlDbType.Text).Value = recordType.Trim().ToLowerInvariant();
        cmd.Parameters.Add("limit", NpgsqlDbType.Integer).Value = limit;

        if (hasKeyword)
        {
            cmd.Parameters.Add("keyword", NpgsqlDbType.Text).Value = $"%{keyword.Trim()}%";
        }

        return await ReadChunksAsync(cmd);
    }

    private async Task<List<RetrievedChunk>> SearchByMetadataAsync(
        Dictionary<string, string> filters,
        int limit)
    {
        var cleanFilters = filters
            .Where(x => !string.IsNullOrWhiteSpace(x.Key) &&
                        !string.IsNullOrWhiteSpace(x.Value))
            .ToList();

        if (!cleanFilters.Any())
        {
            return new List<RetrievedChunk>();
        }

        await using var conn = await OpenConnectionAsync();

        var whereClauses = new List<string>();
        await using var cmd = conn.CreateCommand();

        for (var i = 0; i < cleanFilters.Count; i++)
        {
            var filter = cleanFilters[i];

            if (!AllowedMetadataKeys.Contains(filter.Key))
            {
                throw new ArgumentException($"Unsupported metadata key: {filter.Key}");
            }

            var keyParameter = $"key{i}";
            var valueParameter = $"value{i}";

            whereClauses.Add($"dc.metadata ->> @{keyParameter} = @{valueParameter}");
            cmd.Parameters.Add(keyParameter, NpgsqlDbType.Text).Value = filter.Key;
            cmd.Parameters.Add(valueParameter, NpgsqlDbType.Text).Value = filter.Value;
        }

        cmd.CommandText = $@"
            select
                dc.id,
                dc.document_id,
                dc.chunk_index,
                dc.content,
                dc.metadata,
                coalesce(dc.metadata->>'documentTitle', d.title, '') as document_title,
                1.0::real as similarity
            from document_chunks dc
            left join documents d on d.id = dc.document_id
            where {string.Join(" and ", whereClauses)}
            order by dc.chunk_index asc
            limit @limit;
        ";

        cmd.Parameters.Add("limit", NpgsqlDbType.Integer).Value = limit;

        return await ReadChunksAsync(cmd);
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

    private static async Task<List<RetrievedChunk>> ReadChunksAsync(NpgsqlCommand cmd)
    {
        var chunks = new List<RetrievedChunk>();

        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            chunks.Add(MapChunk(reader));
        }

        return chunks;
    }

    private static RetrievedChunk MapChunk(NpgsqlDataReader reader)
    {
        var metadataJson = reader.IsDBNull(4)
            ? "{}"
            : reader.GetFieldValue<string>(4);

        using var metadata = JsonDocument.Parse(metadataJson);
        var root = metadata.RootElement;
        var documentTitle = GetMetadataString(root, "documentTitle");

        if (string.IsNullOrWhiteSpace(documentTitle) && !reader.IsDBNull(5))
        {
            documentTitle = reader.GetString(5);
        }

        return new RetrievedChunk
        {
            Id = reader.GetGuid(0),
            DocumentId = reader.GetGuid(1),
            ChunkIndex = reader.IsDBNull(2) ? null : reader.GetInt32(2),
            Content = reader.IsDBNull(3) ? "" : reader.GetString(3),
            DocumentTitle = documentTitle,
            Similarity = reader.IsDBNull(6) ? 1.0f : reader.GetFloat(6),
            RecordType = GetMetadataString(root, "recordType"),
            Nik = GetMetadataString(root, "nik"),
            Name = GetMetadataString(root, "name"),
            MaintenanceCode = GetMetadataString(root, "maintenanceCode"),
            Date = GetMetadataString(root, "date"),
            Division = GetMetadataString(root, "division"),
            Department = GetMetadataString(root, "department"),
            Position = GetMetadataString(root, "position"),
            Shift = GetMetadataString(root, "shift"),
            EmployeeStatus = GetMetadataString(root, "employeeStatus"),
            Duration = GetMetadataString(root, "duration"),
            Approval = GetMetadataString(root, "approval"),
            Equipment = GetMetadataString(root, "equipment"),
            Location = GetMetadataString(root, "location"),
            MaintenanceStatus = GetMetadataString(root, "maintenanceStatus"),
            Technician = GetMetadataString(root, "technician"),
            SectionTitle = GetMetadataString(root, "sectionTitle")
        };
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

    private static string GetMetadataString(JsonElement metadata, string propertyName)
    {
        return metadata.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
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
