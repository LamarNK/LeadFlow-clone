namespace Orbita.TelephonyGateway;

public sealed record GatewayWebhookJob(
    Guid Id,
    string Provider,
    Guid PublicId,
    string Method,
    string QueryString,
    string? ContentType,
    IReadOnlyDictionary<string, string> Headers,
    string BodyBase64,
    DateTimeOffset ReceivedAtUtc,
    int AttemptCount,
    DateTimeOffset NextAttemptAtUtc)
{
    public byte[] GetBody() => string.IsNullOrEmpty(BodyBase64)
        ? []
        : Convert.FromBase64String(BodyBase64);
}

public sealed class GatewayQueueFullException(string message) : Exception(message);
