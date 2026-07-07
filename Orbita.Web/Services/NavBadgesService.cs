using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class NavBadgesService(OrbitaApiClient api, IOptions<DesignPreviewOptions> previewOptions)
{
    public async Task<NavBadgesDto> GetAsync(CancellationToken ct = default)
    {
        if (previewOptions.Value.Enabled)
        {
            var summary = DesignPreviewData.Summary;
            return new NavBadgesDto(
                summary.Errors,
                summary.SentToCrm,
                summary.ActionRequired,
                DateTime.UtcNow);
        }

        var live = await api.GetSummaryAsync(ct);
        if (live is null)
        {
            return new NavBadgesDto(0, 0, 0, DateTime.UtcNow);
        }

        return new NavBadgesDto(
            live.Errors,
            live.SentToCrm,
            live.ActionRequired,
            live.AggregatedAtUtc);
    }
}