namespace be_service.Models;

public class RagModeOptions
{
    public string Mode { get; set; } = "Legacy";

    // Measurement scaffolding (Semantic mode only): when true, every query also
    // runs the legacy keyword analyzer and logs the Semantic-vs-legacy diff
    // (QUS_VS_LEGACY). This adds an extra analyzer pass — and occasionally an LLM
    // call (FieldIntentClassifier) — on the critical path, so keep it off in
    // normal operation and enable only when comparing analyzers.
    public bool ShadowCompare { get; set; } = false;

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
