using be_service.Models;

namespace be_service.Abstractions;

public interface IVectorStore
{
    // --- Collection lifecycle ---
    Task EnsureCollectionAsync();
    Task ForceRecreateCollectionAsync();
    Task DeleteByDocumentIdAsync(Guid documentId);

    // --- Upsert ---
    Task UpsertChunkAsync(
        Guid id,
        Guid documentId,
        string documentTitle,
        string content,
        List<float> embedding,
        int chunkIndex = -1,
        string department = "");

    Task UpsertChunkAsync(
        RetrievedChunk chunk,
        List<float> denseEmbedding,
        Dictionary<uint, float>? sparseVector = null);

    // --- Vector / hybrid search ---
    Task<List<RetrievedChunk>> SearchAsync(List<float> queryEmbedding, int limit = 10);
    Task<List<RetrievedChunk>> SearchSemanticAsync(
        List<float> queryEmbedding,
        int limit = 10,
        Dictionary<uint, float>? sparseVector = null);

    // --- Structured exact lookups ---
    Task<List<RetrievedChunk>> SearchByNikAsync(string nik);
    Task<List<RetrievedChunk>> SearchByMaintenanceCodeAsync(string code);
    Task<List<RetrievedChunk>> SearchByDateAsync(string date);
    Task<List<RetrievedChunk>> SearchByNameAsync(string name, int limit = 10);
    Task<List<RetrievedChunk>> SearchByPayloadFilterAsync(
        Dictionary<string, string> filters,
        int limit = 50);
    Task<List<StructuredEntityMatch>> GetKnownStructuredEntitiesAsync();

    // --- Employee facets ---
    Task<List<RetrievedChunk>> SearchEmployeesByDivisionAsync(string division, int limit = 50);
    Task<List<RetrievedChunk>> SearchEmployeesByShiftAsync(string shift, int limit = 50);
    Task<List<RetrievedChunk>> SearchEmployeesByStatusAsync(string status, int limit = 50);
    Task<List<RetrievedChunk>> SearchEmployeesByPositionAsync(string position, int limit = 50);

    // --- Overtime facets ---
    Task<List<RetrievedChunk>> SearchOvertimeByApprovalAsync(string approval, int limit = 50);
    Task<List<RetrievedChunk>> SearchOvertimeByDivisionAsync(string division, int limit = 50);
    Task<List<RetrievedChunk>> SearchOvertimeByNameAsync(string name, int limit = 50);

    // --- Maintenance facets ---
    Task<List<RetrievedChunk>> SearchMaintenanceByStatusAsync(string status, int limit = 50);
    Task<List<RetrievedChunk>> SearchMaintenanceByLocationAsync(string location, int limit = 50);
    Task<List<RetrievedChunk>> SearchMaintenanceByTechnicianAsync(string technician, int limit = 50);
    Task<List<RetrievedChunk>> SearchMaintenanceByEquipmentAsync(string equipment, int limit = 50);

    // --- Record-type / keyword ---
    Task<List<RetrievedChunk>> SearchByRecordTypeAsync(string recordType, string keyword, int limit = 10);
    Task<List<RetrievedChunk>> SearchByRecordTypeAsync(string recordType, int limit = 10);
    Task<List<RetrievedChunk>> SearchByKeywordAsync(string keyword, int limit = 10);
}