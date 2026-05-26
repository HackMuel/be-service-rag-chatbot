namespace be_service.Models;

public class RetrievalOptions
{
    public const int DefaultSemanticTopK = 5;
    public const float DefaultSemanticScoreThreshold = 0.55f;
    public const int DefaultSemanticMaxContextChunks = 3;
    public const int DefaultStructuredDefaultLimit = 50;

    public int SemanticTopK { get; set; } = DefaultSemanticTopK;
    public float SemanticScoreThreshold { get; set; } = DefaultSemanticScoreThreshold;
    public int SemanticMaxContextChunks { get; set; } = DefaultSemanticMaxContextChunks;
    public int StructuredDefaultLimit { get; set; } = DefaultStructuredDefaultLimit;
}
