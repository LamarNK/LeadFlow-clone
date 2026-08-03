namespace Orbita.Api.Models;

public sealed class BitrixWorkforceOptions
{
    public const string SectionName = "BitrixWorkforce";

    public string PublicBaseUrl { get; set; } = string.Empty;
    public int WorkerIntervalSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 50;
    public int LeaseSeconds { get; set; } = 300;
}
