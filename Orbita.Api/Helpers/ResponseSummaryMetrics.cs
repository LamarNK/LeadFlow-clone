using Microsoft.EntityFrameworkCore;
using Orbita.Api.Data;

namespace Orbita.Api.Helpers;

public static class ResponseSummaryMetrics
{
    public static Task<int> CountUniqueAuthorsAsync(
        IQueryable<CandidateResponseEntity> query,
        CancellationToken ct) =>
        query
            .Where(x => x.PersonId != Guid.Empty)
            .Select(x => x.PersonId)
            .Distinct()
            .CountAsync(ct);
}