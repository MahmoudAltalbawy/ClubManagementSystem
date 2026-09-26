using System;
using System.IO;
using System.Threading.Tasks;
using Google.Cloud.Storage.V1;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class GoogleCloudStorageService : IFileStorageService
{
    private readonly StorageClient _storageClient;
    private readonly string _bucketName;
    private readonly ILogger<GoogleCloudStorageService> _logger;

    public GoogleCloudStorageService(IConfiguration config, ILogger<GoogleCloudStorageService> logger)
    {
        _logger = logger;
        _bucketName = config["Storage:BucketName"] ?? throw new ArgumentNullException("Storage:BucketName is missing");

        // StorageClient.Create() automatically uses the Application Default Credentials from the Compute Engine VM.
        // It requires zero secrets or keys in the configuration.
        _storageClient = StorageClient.Create();
    }

    public async Task<string> SaveFileAsync(Stream stream, string fileName, string category, string? contentType = null)
    {
        var safeCategory = SanitizeSegment(category);
        var extension = Path.GetExtension(fileName);
        var safeName = SanitizeFileName(Path.GetFileNameWithoutExtension(fileName));
        if (safeName.Length > 50) safeName = safeName.Substring(0, 50); // Truncate to 50 chars to prevent DB crash
        var storedName = $"{Guid.NewGuid():N}_{safeName}{extension}";
        var objectKey = $"uploads/{safeCategory}/{storedName}";

        try
        {
            var options = new UploadObjectOptions();

            await _storageClient.UploadObjectAsync(
                _bucketName, 
                objectKey, 
                contentType ?? "application/octet-stream", 
                stream, 
                options);

            // Construct the public URL for GCS
            var publicUrl = $"https://{_bucketName}.storage.googleapis.com/{objectKey}";
            return publicUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading file to Google Cloud Storage.");
            throw;
        }
    }

    public async Task DeleteFileAsync(string relativeUrl)
    {
        if (string.IsNullOrWhiteSpace(relativeUrl)) return;

        try
        {
            var uri = new Uri(relativeUrl);
            var path = uri.AbsolutePath.TrimStart('/');
            
            var keyStartIndex = path.IndexOf("uploads/");
            if (keyStartIndex >= 0)
            {
                var objectKey = path.Substring(keyStartIndex);
                await _storageClient.DeleteObjectAsync(_bucketName, objectKey);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting file from Google Cloud Storage.");
        }
    }

    private string SanitizeSegment(string segment)
    {
        var invalidChars = Path.GetInvalidPathChars();
        return string.Join("_", segment.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
    }

    private string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
    }
}
