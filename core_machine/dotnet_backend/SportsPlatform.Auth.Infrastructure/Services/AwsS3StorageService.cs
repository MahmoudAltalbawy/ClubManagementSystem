using System;
using System.IO;
using System.Threading.Tasks;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class AwsS3StorageService : IFileStorageService
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    private readonly string _region;
    private readonly ILogger<AwsS3StorageService> _logger;

    public AwsS3StorageService(IConfiguration config, ILogger<AwsS3StorageService> logger)
    {
        _logger = logger;
        _bucketName = config["Storage:BucketName"] ?? "equipex-s3";
        _region = config["Storage:Region"] ?? "eu-central-1";

        var regionEndpoint = RegionEndpoint.GetBySystemName(_region);
        // AmazonS3Client without credentials automatically resolves IAM Role from EC2 Instance Metadata
        _s3Client = new AmazonS3Client(regionEndpoint);
    }

    public async Task<string> SaveFileAsync(Stream stream, string fileName, string category, string? contentType = null)
    {
        var safeCategory = SanitizeSegment(category);
        var extension = Path.GetExtension(fileName);
        var safeName = SanitizeFileName(Path.GetFileNameWithoutExtension(fileName));
        if (safeName.Length > 50) safeName = safeName.Substring(0, 50);
        var storedName = $"{Guid.NewGuid():N}_{safeName}{extension}";
        var objectKey = $"uploads/{safeCategory}/{storedName}";

        try
        {
            var putRequest = new PutObjectRequest
            {
                BucketName = _bucketName,
                Key = objectKey,
                InputStream = stream,
                ContentType = contentType ?? "application/octet-stream",
                AutoCloseStream = false
            };

            await _s3Client.PutObjectAsync(putRequest);

            // Construct standard public S3 URL
            var publicUrl = $"https://{_bucketName}.s3.{_region}.amazonaws.com/{objectKey}";
            return publicUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error uploading file to AWS S3 bucket {Bucket}.", _bucketName);
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
                await _s3Client.DeleteObjectAsync(_bucketName, objectKey);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting file from AWS S3 bucket {Bucket}: {Url}", _bucketName, relativeUrl);
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
