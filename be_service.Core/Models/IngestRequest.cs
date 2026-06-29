namespace be_service.Models;

public class IngestRequest
{
    public string Title { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string StorageBucket { get; set; } = string.Empty;
    public string StorageObjectKey { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
}
