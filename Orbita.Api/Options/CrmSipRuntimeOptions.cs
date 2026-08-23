namespace Orbita.Api.Options;

public sealed class CrmSipRuntimeOptions
{
    public const string SectionName = "CrmSipRuntime";

    /// <summary>Shared directory mounted into Orbita.Api and the Asterisk container.</summary>
    public string ConfigPath { get; set; } = string.Empty;
}
