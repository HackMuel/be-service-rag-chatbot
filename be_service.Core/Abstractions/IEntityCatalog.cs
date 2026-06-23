using be_service.Models;

namespace be_service.Abstractions;

public interface IEntityCatalog
{
    Task<StructuredEntityMatch?> ResolveAsync(
        string question,
        RagQueryAnalysis analysis
    );

    void ClearCache();

    Task<bool> IsKnownValueAsync(string fieldName, string value);
}