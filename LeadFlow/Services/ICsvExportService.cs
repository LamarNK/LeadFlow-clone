

namespace LeadFlow.Services;

public interface ICsvExportService
{
    Task<string> ExportJournalAsync(IEnumerable<CandidateResponse> items, CancellationToken cancellationToken);
}
