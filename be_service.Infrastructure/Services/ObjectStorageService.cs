using System.Text.RegularExpressions;
using be_service.Models;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using be_service.Abstractions;

namespace be_service.Services;

public class ObjectStorageService : IBlobStore
{
    private readonly ObjectStorageOptions _options;
    private readonly IMinioClient _minioClient;
    private readonly ILogger<ObjectStorageService> _logger;

    public ObjectStorageService(
        IOptions<ObjectStorageOptions> options,
        ILogger<ObjectStorageService> logger)
    {
        _options = options.Value;
        _logger = logger;

        _minioClient = new MinioClient()
            .WithEndpoint(_options.Endpoint)
            .WithCredentials(_options.AccessKey, _options.SecretKey)
            .WithSSL(_options.UseSsl)
            .Build();
    }

    public string BucketName => _options.BucketName;

    public async Task<bool> BucketExistsAsync()
    {
        EnsureConfigured();

        try
        {
            return await _minioClient.BucketExistsAsync(
                new BucketExistsArgs().WithBucket(_options.BucketName));
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "SERVICE_UNAVAILABLE service=ObjectStorage");

            throw ExternalServiceUnavailableException.ObjectStorage(ex);
        }
    }

    public async Task EnsureBucketExistsAsync()
    {
        EnsureConfigured();

        var exists = await BucketExistsAsync();

        if (!exists)
        {
            try
            {
                await _minioClient.MakeBucketAsync(
                    new MakeBucketArgs().WithBucket(_options.BucketName));
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "SERVICE_UNAVAILABLE service=ObjectStorage");

                throw ExternalServiceUnavailableException.ObjectStorage(ex);
            }
        }
    }

    public async Task UploadFileAsync(
        Stream fileStream,
        string objectKey,
        string contentType)
    {
        EnsureConfigured();

        await using var uploadStream = await CreateSeekableUploadStreamAsync(fileStream);

        try
        {
            await EnsureBucketExistsAsync();

            var putObjectArgs = new PutObjectArgs()
                .WithBucket(_options.BucketName)
                .WithObject(objectKey)
                .WithStreamData(uploadStream)
                .WithObjectSize(uploadStream.Length)
                .WithContentType(string.IsNullOrWhiteSpace(contentType)
                    ? "application/octet-stream"
                    : contentType);

            await _minioClient.PutObjectAsync(putObjectArgs);
        }
        catch (ExternalServiceUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "SERVICE_UNAVAILABLE service=ObjectStorage");

            throw ExternalServiceUnavailableException.ObjectStorage(ex);
        }

        _logger.LogInformation(
            "OBJECT_STORAGE_UPLOAD bucket={BucketName} objectKey={ObjectKey} contentType={ContentType}",
            _options.BucketName,
            objectKey,
            contentType);
    }

    public string GenerateObjectKey(string originalFileName)
    {
        var now = DateTimeOffset.UtcNow;
        var safeFileName = SanitizeFileName(originalFileName);

        return $"documents/{now:yyyy}/{now:MM}/{Guid.NewGuid()}-{safeFileName}";
    }

    public async Task<MemoryStream> GetFileAsync(string objectKey)
    {
        EnsureConfigured();

        var memoryStream = new MemoryStream();

        try
        {
            var getObjectArgs = new GetObjectArgs()
                .WithBucket(_options.BucketName)
                .WithObject(objectKey)
                .WithCallbackStream(stream => stream.CopyTo(memoryStream));

            await _minioClient.GetObjectAsync(getObjectArgs);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "SERVICE_UNAVAILABLE service=ObjectStorage");

            throw ExternalServiceUnavailableException.ObjectStorage(ex);
        }

        memoryStream.Position = 0;

        return memoryStream;
    }

    private static async Task<MemoryStream> CreateSeekableUploadStreamAsync(Stream fileStream)
    {
        var memoryStream = new MemoryStream();

        if (fileStream.CanSeek)
        {
            fileStream.Position = 0;
        }

        await fileStream.CopyToAsync(memoryStream);
        memoryStream.Position = 0;

        return memoryStream;
    }

    private static string SanitizeFileName(string fileName)
    {
        var safeFileName = Path.GetFileName(fileName);

        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            return "document";
        }

        safeFileName = Regex.Replace(safeFileName, @"[^\w.\-]+", "_");
        safeFileName = Regex.Replace(safeFileName, @"_+", "_").Trim('_');

        return string.IsNullOrWhiteSpace(safeFileName) ? "document" : safeFileName;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.Endpoint) ||
            string.IsNullOrWhiteSpace(_options.AccessKey) ||
            string.IsNullOrWhiteSpace(_options.SecretKey) ||
            string.IsNullOrWhiteSpace(_options.BucketName))
        {
            throw new InvalidOperationException("ObjectStorage configuration is incomplete.");
        }
    }
}
