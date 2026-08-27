using Microsoft.Extensions.Options;
using Orbita.Api.Options;

namespace Orbita.Api.Services;

/// <summary>Stores successful-close report files outside the web root.</summary>
public sealed class CrmSuccessDocumentStorageService(IOptions<CrmSuccessDocumentOptions> options)
{
    private readonly CrmSuccessDocumentOptions _options = options.Value;

    public async Task<string> SaveAsync(Guid cardId, Guid documentId, Stream content, CancellationToken ct)
    {
        var relativePath = Path.Combine(cardId.ToString("N"), $"{documentId:N}.bin");
        var fullPath = GetFullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        try
        {
            await using var output = new FileStream(
                fullPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous);
            await content.CopyToAsync(output, ct).ConfigureAwait(false);
            return relativePath.Replace('\\', '/');
        }
        catch
        {
            TryDelete(relativePath);
            throw;
        }
    }

    public Stream? OpenRead(string relativePath)
    {
        var fullPath = GetFullPath(relativePath);
        return File.Exists(fullPath)
            ? new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, FileOptions.Asynchronous)
            : null;
    }

    public void TryDelete(string relativePath)
    {
        try
        {
            var fullPath = GetFullPath(relativePath);
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }
        catch
        {
            // Database cleanup must not fail because an orphaned file cannot be removed now.
        }
    }

    private string GetFullPath(string relativePath)
    {
        var root = Path.GetFullPath(_options.DataPath);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Недопустимый путь документа успешного закрытия.");
        }

        return fullPath;
    }
}
