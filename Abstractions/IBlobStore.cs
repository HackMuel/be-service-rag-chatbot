namespace be_service.Abstractions;

public interface IBlobStore
{
     string BucketName { get; }

    Task<bool> BucketExistsAsync();
    Task EnsureBucketExistsAsync();

    Task UploadFileAsync(
        Stream fileStream,
        string objectKey,
        string contentType);

    string GenerateObjectKey(string originalFileName);
    Task<MemoryStream> GetFileAsync(string objectKey);    
}