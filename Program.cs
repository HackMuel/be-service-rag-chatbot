using be_service.Models;
using be_service.Repositories;
using be_service.Services;
using Microsoft.Extensions.Options;

using be_service.Abstractions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddSingleton<QdrantCollectionService>();
builder.Services.AddSingleton<QdrantPointWriter>();
builder.Services.AddSingleton<QdrantSearchClient>();
builder.Services.AddSingleton<QdrantFilterBuilder>();
builder.Services.AddSingleton<QdrantScrollClient>();
builder.Services.AddSingleton<QdrantService>();
builder.Services.AddSingleton<IVectorStore>(sp => sp.GetRequiredService<QdrantService>());
builder.Services.AddSingleton<SparseBm25Encoder>();
builder.Services.AddSingleton(sp =>
{
    var opts = sp.GetRequiredService<IOptions<QdrantOptions>>().Value;
    var host = new Uri(
        string.IsNullOrWhiteSpace(opts.BaseUrl)
            ? QdrantOptions.DefaultBaseUrl
            : opts.BaseUrl).Host;
    var port = opts.GrpcPort > 0 ? opts.GrpcPort : QdrantOptions.DefaultGrpcPort;
    return new Qdrant.Client.QdrantClient(host, port, https: false);
});
builder.Services.AddHttpClient<OllamaService>();
builder.Services.AddTransient<IChatService>(sp => sp.GetRequiredService<OllamaService>());
builder.Services.AddTransient<IEmbeddingService>(sp => sp.GetRequiredService<OllamaService>());

builder.Services.AddControllers();
builder.Services.AddScoped<IngestionService>();
builder.Services.AddScoped<FieldIntentClassifier>();
builder.Services.AddScoped<QueryAnalyzerService>();
builder.Services.AddScoped<QueryUnderstandingService>();
builder.Services.AddSingleton<AnswerFormatterService>();
builder.Services.AddSingleton<PromptBuilderService>();
builder.Services.AddSingleton<StructuredEntityResolver>();
builder.Services.AddSingleton<IEntityCatalog>(sp => sp.GetRequiredService<StructuredEntityResolver>());
builder.Services.Configure<ObjectStorageOptions>(
    builder.Configuration.GetSection("ObjectStorage"));
builder.Services.Configure<StorageModeOptions>(
    builder.Configuration.GetSection("StorageMode"));
builder.Services.Configure<SecurityOptions>(
    builder.Configuration.GetSection("Security"));
builder.Services.Configure<OllamaOptions>(
    builder.Configuration.GetSection("Ollama"));
builder.Services.Configure<QdrantOptions>(
    builder.Configuration.GetSection("Qdrant"));
builder.Services.Configure<RetrievalOptions>(
    builder.Configuration.GetSection("Retrieval"));
builder.Services.Configure<RagModeOptions>(
    builder.Configuration.GetSection("Rag"));
builder.Services.Configure<DatasetSchemaOptions>(
    builder.Configuration.GetSection("DatasetSchema"));
// Fall back to the built-in dummy schema when appsettings provides none, so
// ingestion behaves identically on a fresh checkout.
builder.Services.PostConfigure<DatasetSchemaOptions>(options =>
{
    if (options.RecordTypes.Count == 0)
        options.RecordTypes = DatasetSchemaOptions.Default().RecordTypes;
});
builder.Services.AddScoped<ObjectStorageService>();
builder.Services.AddScoped<IBlobStore>(sp => sp.GetRequiredService<ObjectStorageService>());
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
app.UseMiddleware<ApiKeyMiddleware>();
// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
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

    Guid documentId;

    try
    {
        documentId = await ingestionService.IngestTxtAsync(file);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "TXT upload failed",
            detail: ex.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }

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

    Guid documentId;

    try
    {
        documentId = await ingestionService.IngestPdfAsync(file);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "PDF upload failed",
            detail: ex.Message,
            statusCode: StatusCodes.Status500InternalServerError);
    }

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

app.MapPost("/api/qdrant/recreate", async (QdrantService qdrantService) =>
{
    await qdrantService.ForceRecreateCollectionAsync();

    return Results.Ok(new
    {
        message = "Collection recreated with named vectors schema. Re-ingest all documents."
    });
});

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
