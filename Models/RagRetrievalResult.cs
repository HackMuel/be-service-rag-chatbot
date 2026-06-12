namespace be_service.Models;

public class RagRetrievalResult
{
    public List<RetrievedChunk> Chunks { get; set; } = new();
    public string RetrievalMode { get; set; } = "semantic";
    public int ContextLimit { get; set; } = 5;
    public AnswerLevel AnswerLevel { get; set; } = AnswerLevel.Unknown;
    public string RetrievalSource { get; set; } = string.Empty;
    public bool QdrantVectorSearch { get; set; }
    public string BlockReason { get; set; } = string.Empty;
}
