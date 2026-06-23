namespace be_service.Abstractions;

public interface IEmbeddingService
{
    Task<List<float>> GenerateEmbeddingAsync(string text);
}