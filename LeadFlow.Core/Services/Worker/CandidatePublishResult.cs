using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Worker;

public sealed class CandidatePublishResult
{
    public bool Synchronized { get; init; }
    public ResponseStatus Status { get; init; } = ResponseStatus.InProgress;
    public string? ErrorMessage { get; init; }

    public static CandidatePublishResult Pending() => new() { Synchronized = false };

    public static CandidatePublishResult FromApi(string status, string? errorMessage)
    {
        var mapped = MapStatus(status);
        return new CandidatePublishResult
        {
            Synchronized = true,
            Status = mapped,
            ErrorMessage = errorMessage
        };
    }

    private static ResponseStatus MapStatus(string status) => status switch
    {
        "Sent" => ResponseStatus.Sent,
        "Duplicate" => ResponseStatus.Duplicate,
        "Error" => ResponseStatus.Error,
        "ActionRequired" => ResponseStatus.ActionRequired,
        "InProgress" => ResponseStatus.InProgress,
        "New" => ResponseStatus.New,
        _ => ResponseStatus.Error
    };
}