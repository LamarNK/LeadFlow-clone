namespace LeadFlow.Core.Data;

public sealed record WorkerDatabasePruneResult(
    int CandidatesRemoved,
    int LogsRemoved,
    bool Vacuumed);