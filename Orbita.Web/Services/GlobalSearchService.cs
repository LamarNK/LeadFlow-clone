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
        await Task.WhenAll(workersTask, responsesTask);

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

        var accounts = new List<SearchHitViewModel>();
        foreach (var worker in allWorkers)
        {
            var workerAccounts = await api.GetWorkerAccountsAsync(worker.Id, ct) ?? [];
            foreach (var account in workerAccounts.Where(a => SearchQueryNormalizer.MatchesTokens(query, a.DisplayName)).Take(perGroup))
            {
                accounts.Add(new SearchHitViewModel
                {
                    Title = account.DisplayName,
                    Subtitle = worker.DisplayName,
                    Url = $"/Accounts?q={Uri.EscapeDataString(account.DisplayName)}",
                    IconClass = "fa-solid fa-user"
                });
                if (accounts.Count >= perGroup) break;
            }
            if (accounts.Count >= perGroup) break;
        }

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

        var events = (await api.GetEventsAsync(limit: 200, ct: ct) ?? [])
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