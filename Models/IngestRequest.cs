namespace be_service.Models;

public class IngestRequest
{
    public string Title { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
}