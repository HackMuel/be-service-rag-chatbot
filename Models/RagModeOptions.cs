namespace be_service.Models;

public class RagModeOptions
{
    public string Mode { get; set; } = "Legacy";

    // Hybrid: semantic dispatch for SemanticGrounded queries (dense+sparse search)
    public bool IsHybridMode =>
        string.Equals(Mode, "Hybrid", StringComparison.OrdinalIgnoreCase);

    // Semantic: LLM-based query understanding via QueryUnderstandingService
    public bool IsSemanticQueryMode =>
        string.Equals(Mode, "Semantic", StringComparison.OrdinalIgnoreCase);

    // Both Hybrid and Semantic route SemanticGrounded queries to pure vector
    // search. Keeping this shared means Semantic mode differs from Hybrid only
    // by the analyzer (QueryUnderstandingService vs QueryAnalyzerService),
    // making the Semantic-vs-Hybrid comparison apples-to-apples.
    public bool UsesSemanticDispatch => IsHybridMode || IsSemanticQueryMode;
}
