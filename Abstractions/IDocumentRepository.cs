namespace be_service.Abstractions;
public interface IDocumentRepository
{
    Task<Guid> CreateAsync(string title, string department);

    Task UpdateStorageMetadataAsync(
        Guid documentId,
        string storageBucket,
        string stotageObjectKey,
        string contentType
    );
}