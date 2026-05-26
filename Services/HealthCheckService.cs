using System.Net;
using System.Text.Json;
using be_service.Models;
using Microsoft.Extensions.Options;
using Npgsql;

namespace be_service.Services;

public class HealthCheckService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ObjectStorageService _objectStorageService;
    private readonly QdrantOptions _qdrantOptions;
    private readonly OllamaOptions _ollamaOptions;
    private readonly ILogger<HealthCheckService> _logger;

    public HealthCheckService(
        HttpClient httpClient,
        IConfiguration configuration,
        ObjectStorageService objectStorageService,
        IOptions<QdrantOptions> qdrantOptions,
        IOptions<OllamaOptions> ollamaOptions,
        ILogger<HealthCheckService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _objectStorageService = objectStorageService;
        _qdrantOptions = qdrantOptions.Value;
        _ollamaOptions = ollamaOptions.Value;
        _logger = logger;
    }

    public async Task<HealthResponse> CheckHealthAsync()
    {
        var qdrant = await CheckQdrantDependencyAsync();
        var ollama = await CheckOllamaAsync();
        var minio = await CheckMinioAsync();
        var database = await CheckDatabaseAsync();

        var dependencies = new Dictionary<string, string>
        {
            ["qdrant"] = FormatDependency(qdrant),
            ["ollama"] = FormatDependency(ollama),
            ["minio"] = FormatDependency(minio),
            ["database"] = FormatDependency(database)
        };

        var allOk = new[] { qdrant, ollama, minio, database }
            .All(x => x.Status == "ok");

        return new HealthResponse
        {
            Status = allOk ? "ok" : "degraded",
            Service = "be_service",
            Timestamp = DateTimeOffset.UtcNow,
            Dependencies = dependencies
        };
    }

    public async Task<QdrantStatusResponse> CheckQdrantStatusAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"{QdrantBaseUrl}/collections/{QdrantCollectionName}");

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return new QdrantStatusResponse
                {
                    Status = "error",
                    CollectionName = QdrantCollectionName,
                    Message = $"Collection '{QdrantCollectionName}' tidak ditemukan. Jalankan /api/qdrant/init."
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new QdrantStatusResponse
                {
                    Status = "error",
                    CollectionName = QdrantCollectionName,
                    Message = $"Qdrant merespons dengan status {(int)response.StatusCode} ({response.StatusCode})."
                };
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var result = doc.RootElement.GetProperty("result");

            return new QdrantStatusResponse
            {
                Status = "ok",
                CollectionName = QdrantCollectionName,
                Message = "Qdrant dapat dihubungi dan collection tersedia.",
                PointsCount = TryGetLong(result, "points_count"),
                VectorsCount = TryGetLong(result, "vectors_count")
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Qdrant health check failed.");

            return new QdrantStatusResponse
            {
                Status = "error",
                CollectionName = QdrantCollectionName,
                Message = $"Qdrant tidak dapat dihubungi. Pastikan Qdrant berjalan di {QdrantBaseUrl}."
            };
        }
    }

    private async Task<DependencyCheck> CheckQdrantDependencyAsync()
    {
        var status = await CheckQdrantStatusAsync();

        return status.Status == "ok"
            ? DependencyCheck.Ok("collection tersedia")
            : DependencyCheck.Error(status.Message);
    }

    private async Task<DependencyCheck> CheckOllamaAsync()
    {
        try
        {
            var response = await _httpClient.GetAsync($"{OllamaBaseUrl}/api/tags");

            return response.IsSuccessStatusCode
                ? DependencyCheck.Ok("reachable")
                : DependencyCheck.Error($"status {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Ollama health check failed.");

            return DependencyCheck.Error("tidak dapat dihubungi");
        }
    }

    private async Task<DependencyCheck> CheckMinioAsync()
    {
        try
        {
            var bucketExists = await _objectStorageService.BucketExistsAsync();

            return bucketExists
                ? DependencyCheck.Ok("bucket tersedia")
                : DependencyCheck.Error("bucket tidak ditemukan");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "MinIO health check failed.");

            return DependencyCheck.Error("tidak dapat dihubungi");
        }
    }

    private async Task<DependencyCheck> CheckDatabaseAsync()
    {
        try
        {
            var connectionString = _configuration.GetConnectionString("SupabaseDb");

            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return DependencyCheck.Error("connection string SupabaseDb belum dikonfigurasi");
            }

            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand("select 1;", conn);
            await cmd.ExecuteScalarAsync();

            return DependencyCheck.Ok("reachable");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Database health check failed.");

            return DependencyCheck.Error("tidak dapat dihubungi");
        }
    }

    private static string FormatDependency(DependencyCheck dependency)
    {
        return dependency.Status == "ok"
            ? "ok"
            : $"error: {dependency.Message}";
    }

    private static long? TryGetLong(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value) ||
            value.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        return value.TryGetInt64(out var longValue)
            ? longValue
            : null;
    }

    private sealed record DependencyCheck(string Status, string Message)
    {
        public static DependencyCheck Ok(string message) => new("ok", message);
        public static DependencyCheck Error(string message) => new("error", message);
    }

    private string QdrantBaseUrl => string.IsNullOrWhiteSpace(_qdrantOptions.BaseUrl)
        ? QdrantOptions.DefaultBaseUrl
        : _qdrantOptions.BaseUrl.TrimEnd('/');

    private string QdrantCollectionName => string.IsNullOrWhiteSpace(_qdrantOptions.CollectionName)
        ? QdrantOptions.DefaultCollectionName
        : _qdrantOptions.CollectionName.Trim();

    private string OllamaBaseUrl => string.IsNullOrWhiteSpace(_ollamaOptions.BaseUrl)
        ? OllamaOptions.DefaultBaseUrl
        : _ollamaOptions.BaseUrl.TrimEnd('/');
}

public class HealthResponse
{
    public string Status { get; set; } = "ok";
    public string Service { get; set; } = "be_service";
    public DateTimeOffset Timestamp { get; set; }
    public Dictionary<string, string> Dependencies { get; set; } = new();
}

public class QdrantStatusResponse
{
    public string Status { get; set; } = "ok";
    public string CollectionName { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public long? PointsCount { get; set; }
    public long? VectorsCount { get; set; }
}
