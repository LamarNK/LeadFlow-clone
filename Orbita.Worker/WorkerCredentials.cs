namespace Orbita.Worker;

public sealed class WorkerCredentials
{
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public Guid? WorkerId { get; set; }
    public string? DisplayName { get; set; }
}