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

    // When true, dense + sparse (BM25) vectors are used for hybrid RRF search
    public bool HybridSearchEnabled { get; set; } = false;

    // Fuzzy entity matching thresholds for StructuredEntityResolver
    public double FuzzyMultiTokenThreshold { get; set; } = 0.85;
    public double FuzzySingleTokenThreshold { get; set; } = 0.90;

    // Generic fallback chunking bounds for ChunkingService
    public int GenericChunkMaxLength { get; set; } = 3000;
    public int GenericChunkOverlap { get; set; } = 400;
}
