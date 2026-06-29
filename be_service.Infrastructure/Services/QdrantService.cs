    using be_service.Abstractions;
using be_service.Models;

namespace be_service.Services;

public class QdrantService : IVectorStore
{
    private readonly QdrantCollectionService _collectionService;
    private readonly QdrantPointWriter _pointWriter;
    private readonly QdrantSearchClient _searchClient;
    private readonly QdrantScrollClient _scrollClient;

    public QdrantService(
        QdrantCollectionService collectionService,
        QdrantPointWriter pointWriter,
        QdrantSearchClient searchClient,
        QdrantScrollClient scrollClient)
    {
        _collectionService = collectionService;
        _pointWriter = pointWriter;
        _searchClient = searchClient;
        _scrollClient = scrollClient;
    }

    public async Task EnsureCollectionAsync()
    {
        await _collectionService.EnsureCollectionAsync();
    }

    public async Task ForceRecreateCollectionAsync()
    {
        await _collectionService.ForceRecreateCollectionAsync();
    }

    public async Task DeleteByDocumentIdAsync(Guid documentId)
    {
        await _pointWriter.DeleteByDocumentIdAsync(documentId);
    }

    public async Task UpsertChunkAsync(
        Guid id,
        Guid documentId,
        string documentTitle,
        string content,
        List<float> embedding,
        int chunkIndex = -1,
        string department = "")
    {
        await _pointWriter.UpsertChunkAsync(
            id,
            documentId,
            documentTitle,
            content,
            embedding,
            chunkIndex,
            department);
    }

    public async Task UpsertChunkAsync(
        RetrievedChunk chunk,
        List<float> denseEmbedding,
        Dictionary<uint, float>? sparseVector = null)
    {
        await _pointWriter.UpsertChunkAsync(chunk, denseEmbedding, sparseVector);
    }

    public async Task<List<RetrievedChunk>> SearchAsync(
        List<float> queryEmbedding,
        int limit = 10)
    {
        return await _searchClient.SearchAsync(queryEmbedding, limit);
    }

    public async Task<List<RetrievedChunk>> SearchSemanticAsync(
        List<float> queryEmbedding,
        int limit = 10,
        Dictionary<uint, float>? sparseVector = null)
    {
        return await _searchClient.SearchSemanticAsync(queryEmbedding, limit, sparseVector);
    }

    public async Task<List<RetrievedChunk>> SearchByNikAsync(string nik)
    {
        return await _searchClient.SearchByNikAsync(nik);
    }

    public async Task<List<RetrievedChunk>> SearchByMaintenanceCodeAsync(string code)
    {
        return await _searchClient.SearchByMaintenanceCodeAsync(code);
    }

    public async Task<List<RetrievedChunk>> SearchByDateAsync(string date)
    {
        return await _searchClient.SearchByDateAsync(date);
    }

    public async Task<List<RetrievedChunk>> SearchByNameAsync(string name, int limit = 10)
    {
        return await _searchClient.SearchByNameAsync(name, limit);
    }

    public async Task<List<RetrievedChunk>> SearchByPayloadFilterAsync(
        Dictionary<string, string> filters,
        int limit = 50)
    {
        return await _searchClient.SearchByPayloadFilterAsync(filters, limit);
    }

    public async Task<List<StructuredEntityMatch>> GetKnownStructuredEntitiesAsync()
    {
        return await _searchClient.GetKnownStructuredEntitiesAsync();
    }

    public async Task<List<RetrievedChunk>> SearchEmployeesByDivisionAsync(string division, int limit = 50)
    {
        return await _searchClient.SearchEmployeesByDivisionAsync(division, limit);
    }

    public async Task<List<RetrievedChunk>> SearchEmployeesByShiftAsync(string shift, int limit = 50)
    {
        return await _searchClient.SearchEmployeesByShiftAsync(shift, limit);
    }

    public async Task<List<RetrievedChunk>> SearchEmployeesByStatusAsync(string status, int limit = 50)
    {
        return await _searchClient.SearchEmployeesByStatusAsync(status, limit);
    }

    public async Task<List<RetrievedChunk>> SearchEmployeesByPositionAsync(string position, int limit = 50)
    {
        return await _searchClient.SearchEmployeesByPositionAsync(position, limit);
    }

    public async Task<List<RetrievedChunk>> SearchOvertimeByApprovalAsync(string approval, int limit = 50)
    {
        return await _searchClient.SearchOvertimeByApprovalAsync(approval, limit);
    }

    public async Task<List<RetrievedChunk>> SearchOvertimeByDivisionAsync(string division, int limit = 50)
    {
        return await _searchClient.SearchOvertimeByDivisionAsync(division, limit);
    }

    public async Task<List<RetrievedChunk>> SearchOvertimeByNameAsync(string name, int limit = 50)
    {
        return await _searchClient.SearchOvertimeByNameAsync(name, limit);
    }

    public async Task<List<RetrievedChunk>> SearchMaintenanceByStatusAsync(string status, int limit = 50)
    {
        return await _searchClient.SearchMaintenanceByStatusAsync(status, limit);
    }

    public async Task<List<RetrievedChunk>> SearchMaintenanceByLocationAsync(string location, int limit = 50)
    {
        return await _searchClient.SearchMaintenanceByLocationAsync(location, limit);
    }

    public async Task<List<RetrievedChunk>> SearchMaintenanceByTechnicianAsync(string technician, int limit = 50)
    {
        return await _searchClient.SearchMaintenanceByTechnicianAsync(technician, limit);
    }

    public async Task<List<RetrievedChunk>> SearchMaintenanceByEquipmentAsync(string equipment, int limit = 50)
    {
        return await _searchClient.SearchMaintenanceByEquipmentAsync(equipment, limit);
    }

    public async Task<List<RetrievedChunk>> SearchByRecordTypeAsync(
        string recordType,
        string keyword,
        int limit = 10)
    {
        return await _searchClient.SearchByRecordTypeAsync(recordType, keyword, limit);
    }

    public async Task<List<RetrievedChunk>> SearchByRecordTypeAsync(
        string recordType,
        int limit = 10)
    {
        return await _searchClient.SearchByRecordTypeAsync(recordType, limit);
    }

    public async Task<List<RetrievedChunk>> SearchByKeywordAsync(
        string keyword,
        int limit = 10)
    {
        return await _scrollClient.SearchByKeywordAsync(keyword, limit);
    }

    public static string NormalizeKeyword(string value)
    {
        return QdrantSearchClient.NormalizeKeyword(value);
    }
}
