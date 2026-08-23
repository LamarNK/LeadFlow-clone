namespace Orbita.Api.Options;

public sealed class CrmCallRecordingOptions
{
    public const string SectionName = "CrmCallRecordings";

    public string DataPath { get; set; } = "Data/crm/call-recordings";
    public long MaxUploadBytes { get; set; } = 100L * 1024 * 1024;
}
