namespace Orbita.Web.Formatting;

public static class WorkerDisplay
{
    public static bool ShouldShowMachineName(string displayName, string? machineName) =>
        !string.IsNullOrWhiteSpace(machineName)
        && !string.Equals(displayName.Trim(), machineName.Trim(), StringComparison.OrdinalIgnoreCase);

    public static string FormatMachineSubtitle(string machineName) => $"ПК: {machineName.Trim()}";
}