using be_service.Models;
using be_service.Abstractions;
namespace be_service.Services;

public class IngestionService
{
    private readonly IDocumentTextExtractor _pdfTextExtractor;
    private readonly DocumentIngestionOrchestrator _orchestrator;
    private readonly IBlobStore _objectStorageService;

    public IngestionService(
        IDocumentTextExtractor pdfTextExtractor,
        DocumentIngestionOrchestrator orchestrator,
        IBlobStore objectStorageService)
    {
        _pdfTextExtractor = pdfTextExtractor;
        _orchestrator = orchestrator;
        _objectStorageService = objectStorageService;
    }

    public async Task<Guid> IngestPdfAsync(IFormFile file, string department = "General")
    {
        var objectKey = _objectStorageService.GenerateObjectKey(file.FileName);
        var contentType = ResolveContentType(file, "application/pdf");

        await using (var stream = file.OpenReadStream())
        {
            await _objectStorageService.UploadFileAsync(
                stream,
                objectKey,
                contentType);
        }

        var text = await _pdfTextExtractor.ExtractTextAsync(file);

        var request = new IngestRequest
        {
            Title = file.FileName,
            Department = department,
            Content = text,
            StorageBucket = _objectStorageService.BucketName,
            StorageObjectKey = objectKey,
            ContentType = contentType
        };

        return await IngestAsync(request);
    }

    public async Task<Guid> IngestTxtAsync(IFormFile file, string department = "General")
    {
        var objectKey = _objectStorageService.GenerateObjectKey(file.FileName);
        var contentType = ResolveContentType(file, "text/plain");

        await using (var stream = file.OpenReadStream())
        {
            await _objectStorageService.UploadFileAsync(
                stream,
                objectKey,
                contentType);
        }

        string content;

        await using (var stream = file.OpenReadStream())
        using (var reader = new StreamReader(stream))
        {
            content = await reader.ReadToEndAsync();
        }

        var request = new IngestRequest
        {
            Title = file.FileName,
            Department = department,
            Content = content,
            StorageBucket = _objectStorageService.BucketName,
            StorageObjectKey = objectKey,
            ContentType = contentType
        };

        return await IngestAsync(request);
    }

    public async Task<Guid> IngestAsync(IngestRequest request)
    {
        return await _orchestrator.IngestAsync(request);
    }

    private static string ResolveContentType(
        IFormFile file,
        string fallback)
    {
        return string.IsNullOrWhiteSpace(file.ContentType)
            ? fallback
            : file.ContentType;
    }
}
