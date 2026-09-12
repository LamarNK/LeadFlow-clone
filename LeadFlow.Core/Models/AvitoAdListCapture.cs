namespace LeadFlow.Core.Models;

/// <summary>Снимок вкладки «Активные»: HTML страниц и признак полного обхода.</summary>
public sealed class AvitoAdListCapture
{
    public bool Success { get; init; }
    public bool Complete { get; init; }
    public string? FailureReason { get; init; }
    public IReadOnlyList<string> PageHtml { get; init; } = [];
}
