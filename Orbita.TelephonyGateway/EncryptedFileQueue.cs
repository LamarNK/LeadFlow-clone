using System.Security.Cryptography;
using System.Text.Json;

namespace Orbita.TelephonyGateway;

public sealed class EncryptedFileQueue
{
    private const byte FormatVersion = 1;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly GatewayOptions options;
    private readonly byte[] encryptionKey;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly JsonSerializerOptions jsonOptions = new(JsonSerializerDefaults.Web);

    public EncryptedFileQueue(GatewayOptions options)
    {
        this.options = options;
        encryptionKey = options.GetEncryptionKey();
        Directory.CreateDirectory(options.QueuePath);
    }

    public async Task EnqueueAsync(GatewayWebhookJob job, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var count = Directory.EnumerateFiles(options.QueuePath, "*.job", SearchOption.TopDirectoryOnly)
                .Take(options.MaxQueueItems)
                .Count();
            if (count >= options.MaxQueueItems)
            {
                throw new GatewayQueueFullException("The encrypted telephony queue is full.");
            }

            var usedBytes = Directory.EnumerateFiles(options.QueuePath, "*.job", SearchOption.TopDirectoryOnly)
                .Sum(path => new FileInfo(path).Length);
            // Base64 is 4 chars per 3 bytes. Avoid allocating another copy of a
            // potentially 100 MB recording only to estimate the encrypted queue size.
            var estimatedBytes = ((long)job.BodyBase64.Length * 3L / 4L) + 64 * 1024L;
            if (usedBytes + estimatedBytes > options.MaxQueueBytes)
            {
                throw new GatewayQueueFullException("The encrypted telephony queue has reached its byte limit.");
            }

            await WriteUnsafeAsync(job, ct);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<GatewayWebhookJob>> GetDueAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken ct = default)
    {
        var result = new List<GatewayWebhookJob>(Math.Max(0, limit));
        await gate.WaitAsync(ct);
        try
        {
            foreach (var path in Directory.EnumerateFiles(options.QueuePath, "*.job", SearchOption.TopDirectoryOnly)
                         .OrderBy(File.GetLastWriteTimeUtc))
            {
                ct.ThrowIfCancellationRequested();
                var job = await TryReadUnsafeAsync(path, ct);
                if (job is null || job.NextAttemptAtUtc > now)
                {
                    continue;
                }

                result.Add(job);
                if (result.Count >= limit)
                {
                    break;
                }
            }
        }
        finally
        {
            gate.Release();
        }

        return result;
    }

    public async Task RescheduleAsync(GatewayWebhookJob job, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (File.Exists(GetJobPath(job.Id)))
            {
                await WriteUnsafeAsync(job, ct);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task CompleteAsync(Guid jobId, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            File.Delete(GetJobPath(jobId));
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task MoveToDeadLetterAsync(Guid jobId, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var source = GetJobPath(jobId);
            if (File.Exists(source))
            {
                File.Move(source, Path.Combine(options.QueuePath, $"{jobId:N}.dead"), overwrite: true);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> CanWriteAsync(CancellationToken ct = default)
    {
        var probePath = Path.Combine(options.QueuePath, $".{Guid.NewGuid():N}.probe");
        try
        {
            await File.WriteAllBytesAsync(probePath, [FormatVersion], ct);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            File.Delete(probePath);
        }
    }

    private async Task WriteUnsafeAsync(GatewayWebhookJob job, CancellationToken ct)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(job, jsonOptions);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(encryptionKey, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var payload = new byte[1 + NonceSize + TagSize + ciphertext.Length];
        payload[0] = FormatVersion;
        nonce.CopyTo(payload.AsSpan(1, NonceSize));
        tag.CopyTo(payload.AsSpan(1 + NonceSize, TagSize));
        ciphertext.CopyTo(payload.AsSpan(1 + NonceSize + TagSize));

        var destination = GetJobPath(job.Id);
        var temporary = $"{destination}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private async Task<GatewayWebhookJob?> TryReadUnsafeAsync(string path, CancellationToken ct)
    {
        try
        {
            var payload = await File.ReadAllBytesAsync(path, ct);
            if (payload.Length < 1 + NonceSize + TagSize || payload[0] != FormatVersion)
            {
                MoveCorruptFile(path);
                return null;
            }

            var nonce = payload.AsSpan(1, NonceSize);
            var tag = payload.AsSpan(1 + NonceSize, TagSize);
            var ciphertext = payload.AsSpan(1 + NonceSize + TagSize);
            var plaintext = new byte[ciphertext.Length];
            using (var aes = new AesGcm(encryptionKey, TagSize))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext);
            }

            return JsonSerializer.Deserialize<GatewayWebhookJob>(plaintext, jsonOptions);
        }
        catch (CryptographicException)
        {
            MoveCorruptFile(path);
            return null;
        }
        catch (JsonException)
        {
            MoveCorruptFile(path);
            return null;
        }
    }

    private void MoveCorruptFile(string path)
    {
        var destination = Path.Combine(
            options.QueuePath,
            $"{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}.corrupt");
        File.Move(path, destination, overwrite: true);
    }

    private string GetJobPath(Guid id) => Path.Combine(options.QueuePath, $"{id:N}.job");
}
