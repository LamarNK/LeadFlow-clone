using Microsoft.Extensions.Options;
using Orbita.Contracts;
using Orbita.Web.Formatting;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Options;

namespace Orbita.Web.Services;

public sealed class GlobalSearchService(OrbitaApiClient api, IOptions<DesignPreviewOptions> previewOptions)
{
    public async Task<GlobalSearchResultViewModel> SearchAsync(string? query, int limit = 8, CancellationToken ct = default)
    {
        query = SearchQueryNormalizer.Normalize(query) ?? string.Empty;
        if (query.Length < 2)
        {
            return GlobalSearchResultViewModel.Empty;
        }

        var perGroup = Math.Max(2, limit / 4);

        if (previewOptions.Value.Enabled)
        {
            return SearchPreview(query, perGroup);
        }

        var workersTask = api.GetWorkersAsync(ct);
        var responsesTask = api.GetResponsesPageAsync(
            $"search={Uri.EscapeDataString(query)}&page=1&pageSize={perGroup}",
            ct);
        var accountsTask = api.GetOfficeAccountsAsync(ct: ct);
        var eventsTask = api.GetEventsAsync(limit: 200, ct: ct);
        await Task.WhenAll(workersTask, responsesTask, accountsTask, eventsTask);

        var allWorkers = await workersTask ?? [];
        var workers = allWorkers
            .Where(w => SearchQueryNormalizer.MatchesTokens(query, w.DisplayName, w.MachineName))
            .Take(perGroup)
            .Select(w => new SearchHitViewModel
            {
                Title = w.DisplayName,
                Subtitle = w.IsOnline ? "Онлайн" : "Оффлайн",
                Url = $"/Workers/Details/{w.Id}",
                IconClass = "fa-solid fa-server"
            })
            .ToList();

        var officeAccounts = await accountsTask ?? [];
        var accounts = officeAccounts
            .Where(item => SearchQueryNormalizer.MatchesTokens(query, item.Account.DisplayName))
            .Take(perGroup)
            .Select(item => new SearchHitViewModel
            {
                Title = item.Account.DisplayName,
                Subtitle = item.WorkerDisplayName,
                Url = $"/Accounts?q={Uri.EscapeDataString(item.Account.DisplayName)}",
                IconClass = "fa-solid fa-user"
            })
            .ToList();

        var responses = (await responsesTask)?.Items
            .Take(perGroup)
            .Select(r => new SearchHitViewModel
            {
                Title = string.IsNullOrWhiteSpace(r.FullName) ? r.Vacancy : r.FullName,
                Subtitle = ResponseDisplay.FormatAccountWithSubProfile(r.AccountName, r.AvitoSubProfileName),
                Url = $"/Responses?search={Uri.EscapeDataString(query)}&id={r.Id}",
                IconClass = "fa-solid fa-inbox"
            })
            .ToList() ?? [];

        var events = (await eventsTask ?? [])
            .Where(e => SearchQueryNormalizer.MatchesTokens(query, e.Message, e.Details, e.WorkerDisplayName))
            .Take(perGroup)
            .Select(e => new SearchHitViewModel
            {
                Title = e.Message,
                Subtitle = e.WorkerDisplayName,
                Url = $"/Events?q={Uri.EscapeDataString(query)}",
                IconClass = "fa-solid fa-triangle-exclamation"
            })
            .ToList();

        return new GlobalSearchResultViewModel
        {
            Query = query,
            Workers = workers,
            Accounts = accounts,
            Responses = responses,
            Errors = events
        };
    }

    private static GlobalSearchResultViewModel SearchPreview(string query, int limit)
    {
        var workers = DesignPreviewData.Workers
            .Where(w => SearchQueryNormalizer.MatchesTokens(query, w.DisplayName, w.MachineName))
            .Take(limit)
            .Select(w => new SearchHitViewModel
            {
                Title = w.DisplayName,
                Subtitle = w.IsOnline ? "Онлайн" : "Оффлайн",
                Url = $"/Workers/Details/{w.Id}",
                IconClass = "fa-solid fa-server"
            })
            .ToList();

        return new GlobalSearchResultViewModel
        {
            Query = query,
            Workers = workers,
            Responses =
            [
                new SearchHitViewModel
                {
                    Title = "Демо-отклик",
                    Subtitle = "Preview",
                    Url = "/Responses",
                    IconClass = "fa-solid fa-inbox"
                }
            ]
        };
    }
}