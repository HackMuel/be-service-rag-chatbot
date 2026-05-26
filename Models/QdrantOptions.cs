namespace be_service.Models;

public class QdrantOptions
{
    public const string DefaultBaseUrl = "http://localhost:6333";
    public const string DefaultCollectionName = "pertamina_chunks";
    public const int DefaultVectorSize = 768;
    public const string DefaultDistance = "Cosine";

    public string BaseUrl { get; set; } = DefaultBaseUrl;
    public string CollectionName { get; set; } = DefaultCollectionName;
    public int VectorSize { get; set; } = DefaultVectorSize;
    public string Distance { get; set; } = DefaultDistance;
}
