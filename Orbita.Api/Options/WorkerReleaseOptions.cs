namespace Orbita.Api.Options;

public sealed class WorkerReleaseOptions
{
    public const string SectionName = "WorkerReleases";

    public string DataPath { get; set; } = "Data/releases/worker";

    public long MaxUploadBytes { get; set; } = 536_870_912;
}