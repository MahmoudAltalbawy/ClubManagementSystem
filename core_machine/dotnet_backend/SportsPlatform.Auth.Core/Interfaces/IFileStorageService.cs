namespace SportsPlatform.Auth.Core.Interfaces;

public interface IFileStorageService
{
    Task<string> SaveFileAsync(Stream stream, string fileName, string category, string? contentType = null);
    Task DeleteFileAsync(string relativeUrl);
}
