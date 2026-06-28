using System.Reflection;

namespace Orbita.Worker;

public static class ApplicationVersionProvider
{
    public static string GetVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plusIndex = informational.IndexOf('+', StringComparison.Ordinal);
            return plusIndex >= 0 ? informational[..plusIndex] : informational;
        }

        var version = assembly.GetName().Version;
        return version?.ToString() ?? "0.0.0.0";
    }
}