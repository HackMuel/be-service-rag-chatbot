using be_service.Models;
using be_service.Repositories;
using be_service.Services;
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddSingleton<QdrantCollectionService>();
builder.Services.AddSingleton<QdrantPointWriter>();
builder.Services.AddSingleton<QdrantSearchClient>();
builder.Services.AddSingleton<QdrantFilterBuilder>();
builder.Services.AddSingleton<QdrantScrollClient>();
builder.Services.AddSingleton<QdrantService>();
builder.Services.AddHttpClient<OllamaService>();
builder.Services.AddHttpClient<HealthCheckService>();
builder.Services.AddControllers();
builder.Services.AddScoped<IngestionService>();
builder.Services.AddSingleton<QueryAnalyzerService>();
builder.Services.AddSingleton<AnswerFormatterService>();
builder.Services.AddSingleton<PromptBuilderService>();
builder.Services.AddSingleton<StructuredEntityResolver>();
builder.Services.Configure<QdrantOptions>(
    builder.Configuration.GetSection("Qdrant"));
builder.Services.Configure<OllamaOptions>(
    builder.Configuration.GetSection("Ollama"));
builder.Services.Configure<RetrievalOptions>(
    builder.Configuration.GetSection("Retrieval"));
builder.Services.Configure<ObjectStorageOptions>(
    builder.Configuration.GetSection("ObjectStorage"));
builder.Services.Configure<StorageModeOptions>(
    builder.Configuration.GetSection("StorageMode"));
builder.Services.AddScoped<ObjectStorageService>();
builder.Services.AddScoped<RetrievalService>();
builder.Services.AddScoped<PdfTextExtractor>();
builder.Services.AddScoped<TextNormalizer>();
builder.Services.AddScoped<ChunkingService>();
builder.Services.AddScoped<EmbeddingIngestionService>();
builder.Services.AddScoped<DocumentIngestionOrchestrator>();
builder.Services.AddScoped<ChunkRepository>();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddScoped<RagChatService>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy
            .WithOrigins("http://localhost:5173")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
var app = builder.Build();
app.UseCors("AllowFrontend");
app.UseMiddleware<GlobalExceptionHandlingMiddleware>();
// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/health", async (HealthCheckService healthCheckService) =>
{
    var response = await healthCheckService.CheckHealthAsync();

    return Results.Ok(response);
});

app.MapPost("/api/chat", async (
    ChatRequest request,
    RagChatService ragChatService) =>
{
    var response =
        await ragChatService.AskAsync(request.Message);

    return Results.Ok(response);
});

app.MapPost("/api/ingest", async (
    IngestRequest request,
    IngestionService ingestionService) =>
{
    var documentId = await ingestionService.IngestAsync(request);

    return Results.Ok(new
    {
        message = "Document ingested successfully",
        documentId
    });
});

app.MapPost("/api/upload-txt", async (
    IFormFile file,
    IngestionService ingestionService) =>
{
    if (file == null || file.Length == 0)
    {
        return Results.BadRequest("File kosong");
    }

    var documentId = await ingestionService.IngestTxtAsync(file);

    return Results.Ok(new
    {
        message = "TXT uploaded successfully",
        documentId
    });
})
.DisableAntiforgery();

app.MapPost("/api/upload-pdf", async (
    IFormFile file,
    IngestionService ingestionService) =>
{
    if (file == null || file.Length == 0)
    {
        return Results.BadRequest("File PDF kosong");
    }

    if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest("File harus PDF");
    }

    var documentId = await ingestionService.IngestPdfAsync(file);

    return Results.Ok(new
    {
        message = "PDF uploaded successfully",
        documentId
    });
})
.DisableAntiforgery();

app.MapGet("/api/qdrant/init", async (QdrantService qdrantService) =>
{
    await qdrantService.EnsureCollectionAsync();

    return Results.Ok(new
    {
        message = "Qdrant collection ready"
    });
});

app.MapGet("/api/qdrant/status", async (HealthCheckService healthCheckService) =>
{
    var response = await healthCheckService.CheckQdrantStatusAsync();

    return response.Status == "ok"
        ? Results.Ok(response)
        : Results.Json(
            response,
            statusCode: StatusCodes.Status503ServiceUnavailable);
});

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
