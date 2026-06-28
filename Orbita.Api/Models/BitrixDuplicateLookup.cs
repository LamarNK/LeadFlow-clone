namespace Orbita.Api.Models;

public enum BitrixDuplicateLookupOutcome
{
    Skipped,
    NoDuplicate,
    Duplicate,
    Unavailable
}

public sealed record BitrixDuplicateLookupResult(
    BitrixDuplicateLookupOutcome Outcome,
    string? ErrorMessage = null)
{
    public bool IsDuplicate => Outcome == BitrixDuplicateLookupOutcome.Duplicate;
    public bool IsUnavailable => Outcome == BitrixDuplicateLookupOutcome.Unavailable;
    public bool IsSkipped => Outcome == BitrixDuplicateLookupOutcome.Skipped;
}