namespace Orbita.Api.Options;

public sealed class LeadFlowImportOptions
{
    public string DataPath { get; set; } = "Data/leadflow-import";
    public long MaxUploadBytes { get; set; } = 104_857_600;
    public int PreviewItemLimit { get; set; } = 100;
    public int SessionRetentionHours { get; set; } = 24;
}