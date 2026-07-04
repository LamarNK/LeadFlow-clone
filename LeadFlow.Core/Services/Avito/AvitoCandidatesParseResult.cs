using LeadFlow.Core.Models;

namespace LeadFlow.Core.Services.Avito;

public sealed record AvitoCandidatesParseResult(
    IReadOnlyList<CandidateResponse> Candidates,
    AvitoCandidatesExtractionSummary Summary);