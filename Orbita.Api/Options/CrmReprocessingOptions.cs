namespace Orbita.Api.Options;

/// <summary>Explicitly activated, bounded recycling of closed no-answer cards.</summary>
public sealed class CrmReprocessingOptions
{
    public const string SectionName = "CrmReprocessing";
    public bool Enabled { get; set; }
    public Guid DestinationOfficeId { get; set; }
    public string[] SourceOfficeNames { get; set; } = [];
    // Required lower bound, retained across restarts. Never default to all history.
    public DateTimeOffset? ClosedFromUtc { get; set; }
    public int BatchSize { get; set; } = 25;
    public int ScanIntervalSeconds { get; set; } = 10;
}
