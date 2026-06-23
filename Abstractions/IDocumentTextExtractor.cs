namespace be_service.Abstractions;

public interface IDocumentTextExtractor
{
    Task<string> ExtractTextAsync(IFormFile file);
}