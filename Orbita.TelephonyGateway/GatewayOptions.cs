namespace Orbita.TelephonyGateway;

public sealed class GatewayOptions
{
    public const string SectionName = "TelephonyGateway";

    public string OrbitaApiBaseUrl { get; set; } = string.Empty;
    public string QueuePath { get; set; } = "/app/Data/queue";
    public string QueueEncryptionKey { get; set; } = string.Empty;
    public int MaxBodyBytes { get; set; } = 105 * 1024 * 1024;
    public int MaxQueueItems { get; set; } = 100_000;
    public long MaxQueueBytes { get; set; } = 10L * 1024 * 1024 * 1024;
    public int MaxDeliveryAttempts { get; set; } = 20;
    public int RetryBaseSeconds { get; set; } = 5;
    public int RetryMaxSeconds { get; set; } = 300;
    public int PollIntervalSeconds { get; set; } = 2;
    public int DeliveryBatchSize { get; set; } = 50;

    public Uri GetOrbitaApiBaseUri()
    {
        if (!Uri.TryCreate(OrbitaApiBaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                "TelephonyGateway:OrbitaApiBaseUrl must be an absolute HTTP(S) URL.");
        }

        return uri;
    }

    public byte[] GetEncryptionKey()
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(QueueEncryptionKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                "TelephonyGateway:QueueEncryptionKey must be a Base64 encoded 32-byte key.",
                exception);
        }

        if (key.Length != 32)
        {
            throw new InvalidOperationException(
                "TelephonyGateway:QueueEncryptionKey must decode to exactly 32 bytes.");
        }

        return key;
    }

    public void Validate()
    {
        _ = GetOrbitaApiBaseUri();
        _ = GetEncryptionKey();

        if (string.IsNullOrWhiteSpace(QueuePath))
        {
            throw new InvalidOperationException("TelephonyGateway:QueuePath is required.");
        }

        if (MaxBodyBytes is < 1024 or > 128 * 1024 * 1024)
        {
            throw new InvalidOperationException("TelephonyGateway:MaxBodyBytes must be between 1 KB and 128 MB.");
        }

        if (MaxQueueItems <= 0 || MaxQueueBytes < MaxBodyBytes
            || MaxDeliveryAttempts <= 0 || DeliveryBatchSize <= 0)
        {
            throw new InvalidOperationException("Telephony gateway queue limits must be positive.");
        }

        if (RetryBaseSeconds <= 0 || RetryMaxSeconds < RetryBaseSeconds || PollIntervalSeconds <= 0)
        {
            throw new InvalidOperationException("Telephony gateway retry intervals are invalid.");
        }
    }
}
