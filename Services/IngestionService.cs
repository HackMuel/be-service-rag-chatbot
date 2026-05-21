using be_service.Models;

namespace be_service.Services;

public class IngestionService
{
    private readonly PdfTextExtractor _pdfTextExtractor;
    private readonly DocumentIngestionOrchestrator _orchestrator;

    public IngestionService(
        PdfTextExtractor pdfTextExtractor,
        DocumentIngestionOrchestrator orchestrator)
    {
        _pdfTextExtractor = pdfTextExtractor;
        _orchestrator = orchestrator;
    }

    public async Task<Guid> IngestPdfAsync(IFormFile file, string department = "General")
    {
        var text = await _pdfTextExtractor.ExtractTextAsync(file);

        var request = new IngestRequest
        {
            Title = file.FileName,
            Department = department,
            Content = text
        };

        return await IngestAsync(request);
    }

    public async Task<Guid> IngestAsync(IngestRequest request)
    {
        return await _orchestrator.IngestAsync(request);
    }
}
