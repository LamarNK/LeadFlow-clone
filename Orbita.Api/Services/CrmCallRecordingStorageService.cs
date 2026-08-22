using Microsoft.Extensions.Options;
using Orbita.Api.Options;

namespace Orbita.Api.Services;

/// <summary>Stores SIP recordings outside the public web root.</summary>
public sealed class CrmCallRecordingStorageService(IOptions<CrmCallRecordingOptions> options)
{
    private readonly CrmCallRecordingOptions _options = options.Value;

    public long MaxUploadBytes => _options.MaxUploadBytes;

    public async Task<string> SaveAsync(Guid callId, Stream content, CancellationToken ct)
    {
        var relativePath = $"{callId:N}.bin";
        var fullPath = GetFullPath(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + $".{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                var buffer = new byte[81920];
                long written = 0;
                while (true)
                {
                    var read = await content.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    written += read;
                    if (written > MaxUploadBytes)
                    {
                        throw new InvalidDataException("Запись звонка превышает допустимый размер.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                }
                await output.FlushAsync(ct).ConfigureAwait(false);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
            return relativePath;
        }
        catch
        {
            TryDeleteFullPath(temporaryPath);
            throw;
        }
    }

    public Stream? OpenRead(string relativePath)
    {
        var fullPath = GetFullPath(relativePath);
        return File.Exists(fullPath)
            ? new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous)
            : null;
    }

    public void TryDelete(string relativePath) => TryDeleteFullPath(GetFullPath(relativePath));

    private string GetFullPath(string relativePath)
    {
        var root = Path.GetFullPath(_options.DataPath);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Недопустимый путь записи звонка.");
        }

        return fullPath;
    }

    private static void TryDeleteFullPath(string fullPath)
    {
        try
        {
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }
        catch
        {
            // An orphaned file must not break call processing or database cleanup.
        }
    }
}
