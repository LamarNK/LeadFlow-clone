namespace LeadFlow.Core.Models;

public sealed class AuthCheckResult
{
    public bool IsAuthorized { get; set; }
    public bool RequiresManualAction { get; set; }
    public string CurrentUrl { get; set; } = string.Empty;
    public string ProfileName { get; set; } = string.Empty;
    public string StatusMessage { get; set; } = string.Empty;
}
