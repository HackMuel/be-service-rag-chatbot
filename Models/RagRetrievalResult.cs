namespace be_service.Models;

public class RagRetrievalResult
{
    public List<RetrievedChunk> Chunks { get; set; } = new();
    public string RetrievalMode { get; set; } = "semantic";
    public int ContextLimit { get; set; } = 5;
}