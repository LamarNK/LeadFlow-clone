namespace Orbita.Api.Options;

public sealed class WorkerReleaseOptions
{
    public const string SectionName = "WorkerReleases";

    public string DataPath { get; set; } = "Data/releases/worker";

    public long MaxUploadBytes { get; set; } = 536_870_912;

    /// <summary>
    /// Secret used only by CI to publish a new worker MSI. An empty value disables
    /// the deployment endpoint so it can never become publicly writable by mistake.
    /// </summary>
    public string PublishToken { get; set; } = string.Empty;
}
