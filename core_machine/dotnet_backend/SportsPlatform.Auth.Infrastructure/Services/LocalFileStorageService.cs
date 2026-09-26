using System;
using System.IO;
using System.Threading.Tasks;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class LocalFileStorageService : IFileStorageService
{
    private readonly string _uploadsRoot;

    public LocalFileStorageService()
    {
        _uploadsRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
    }

    public async Task<string> SaveFileAsync(Stream stream, string fileName, string category, string? contentType = null)
    {
        var safeCategory = SanitizeSegment(category);
        var extension = Path.GetExtension(fileName);
        var safeName = SanitizeFileName(Path.GetFileNameWithoutExtension(fileName));
        var storedName = $"{Guid.NewGuid():N}_{safeName}{extension}";
        var directory = Path.Combine(_uploadsRoot, safeCategory);

        Directory.CreateDirectory(directory);

        var fullPath = Path.Combine(directory, storedName);
        await using var fileStream = File.Create(fullPath);
        await stream.CopyToAsync(fileStream);

        return $"/uploads/{safeCategory}/{storedName}";
    }

    public Task DeleteFileAsync(string relativeUrl)
    {
        if (string.IsNullOrWhiteSpace(relativeUrl) || !relativeUrl.StartsWith("/uploads/", StringComparison.OrdinalIgnoreCase))
            return Task.CompletedTask;

        var fullPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", relativeUrl.TrimStart('/'));
        var uploadsRoot = Path.GetFullPath(_uploadsRoot);

        if (fullPath.StartsWith(uploadsRoot, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath))
            File.Delete(fullPath);

        return Task.CompletedTask;
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