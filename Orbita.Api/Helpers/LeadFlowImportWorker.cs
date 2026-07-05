namespace Orbita.Api.Helpers;

internal static class LeadFlowImportWorker
{
    public const string DisplayName = "LeadFlow (импорт)";
    public const string MachineName = "leadflow-import";

    public static bool IsImportWorker(string? machineName) =>
        string.Equals(machineName, MachineName, StringComparison.Ordinal);
}