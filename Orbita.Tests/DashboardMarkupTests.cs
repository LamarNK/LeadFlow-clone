using System.Diagnostics;
using System.Text;
using Microsoft.AspNetCore.Http;
using Orbita.Contracts;
using Orbita.Web.Models.ViewModels;
using Orbita.Web.Services;

namespace Orbita.Tests;

public sealed class DashboardMarkupTests
{
    [Fact]
    public void DashboardPreview_IncludesUniqueResponsesKpi()
    {
        var model = DesignPreviewData.BuildDashboardViewModel();

        var card = Assert.Single(model.KpiCards, card => card.Key == "unique");
        Assert.Equal("Уникальных откликов", card.Label);
        Assert.Equal(978, card.CountValue);
        Assert.Contains("уник./ч", card.Rate);
        Assert.Contains($"status={Uri.EscapeDataString(ResponseStatusFilterValues.DefaultSelection)}", card.Href);
    }

    [Fact]
    public void ResponsesStatusFilter_AllowsMultipleSelections_AndDefaultsToExcludeDuplicates()
    {
        var view = ReadRepoFile("Orbita.Web/Views/Shared/_ResponsesFilterFields.cshtml");
        var js = ReadRepoFile("Orbita.Web/wwwroot/js/orbita-responses.js");

        Assert.Contains("data-responses-status-picker", view);
        Assert.Contains("data-responses-status-option", view);
        Assert.Contains("ResponseStatusFilterValues.Parse(Model.Filters.Status)", view);
        Assert.Contains("Все, кроме дублей", view);
        Assert.Contains("function initStatusPickers()", js);
        Assert.Contains("selectedValue.value", js);
        Assert.Contains("data-responses-status-all", js);
    }

    [Fact]
    public void SharedKpiCards_RenderValuesWithoutRequiringPageScriptInitialization()
    {
        var model = ReadRepoFile("Orbita.Web/Models/ViewModels/SharedUiViewModels.cs");
        var partial = ReadRepoFile("Orbita.Web/Views/Shared/_KpiCardBody.cshtml");
        var sharedJs = ReadRepoFile("Orbita.Web/wwwroot/js/orbita-live-shared.js");

        Assert.Contains("RenderInitialValue { get; init; } = true", model);
        Assert.Contains("Model.RenderInitialValue", partial);
        Assert.Contains("@initialCountText", partial);
        Assert.Contains("function initializeKpiCounters(selector)", sharedJs);
        Assert.Contains("Math.round(displayed) === Math.round(target)", sharedJs);
    }

    [Fact]
    public void KpiPages_UseSharedCounterInitialization()
    {
        var scripts = new[]
        {
            "orbita-accounts.js",
            "orbita-balances.js",
            "orbita-dashboard.js",
            "orbita-errors.js",
            "orbita-events.js",
            "orbita-journal.js",
            "orbita-listings.js",
            "orbita-responses.js",
            "orbita-statistics.js",
            "orbita-worker.js",
            "orbita-workers.js"
        };

        foreach (var script in scripts)
        {
            var js = ReadRepoFile("Orbita.Web/wwwroot/js/" + script);
            Assert.Contains("initializeKpiCounters", js);
        }
    }

    [Fact]
    public void BalancesQrWorkspace_OffersCancellationForTheSelectedQrSession()
    {
        var view = ReadRepoFile("Orbita.Web/Views/Balances/Index.cshtml");
        var js = ReadRepoFile("Orbita.Web/wwwroot/js/orbita-balances.js");

        Assert.Contains("data-qr-cancel", view);
        Assert.Contains("data-qr-expires-at", view);
        Assert.Contains("data-qr-timer", view);
        Assert.Contains("data-qr-list-timer", view);
        Assert.Contains("data-topup-workspace", view);
        Assert.Contains("Запрошенные пополнения", view);
        Assert.Contains("Запрошено", view);
        Assert.Contains("fa-spinner", view);
        Assert.Contains("Отменить", view);
        Assert.Contains("cancel.dataset.sessionId = button.dataset.qrSelect;", js);
        Assert.Contains("event.target.closest('[data-qr-cancel]')", js);
        Assert.Contains("page.dataset.cancelUrl", js);
        Assert.Contains("Осталось на оплату:", js);
        Assert.Contains("Осталось: ", js);
        Assert.Contains("Сессия отменяется автоматически.", js);
        Assert.Contains("function topUpWorkspaceSignature", js);
        Assert.Contains("function updateQrListTimers()", js);
        Assert.Contains("orbita-balances-selected-qr", js);
        Assert.Contains("var selectedQr = Array.from(page.querySelectorAll('[data-qr-select]')).find", js);
        Assert.Contains("var failed = results.filter(function (result) { return !result.success; });", js);
        Assert.Contains("Не удалось запросить пополнение.", js);
    }

    [Fact]
    public void Balances_SeparatesRequestedTopUpsFromHistory()
    {
        var view = ReadRepoFile("Orbita.Web/Views/Balances/Index.cshtml");
        var service = ReadRepoFile("Orbita.Web/Services/BalancesService.cs");

        Assert.Contains("Запрошенные пополнения", view);
        Assert.Contains("var isRequestedTopUpsTab = activeTab == \"requested\";", view);
        Assert.Contains("@if (isRequestedTopUpsTab && activeTopUpSessions.Count > 0)", view);
        Assert.Contains("!TopUpSessionStatuses.IsOpenOnLowBalanceTab(x.Status)", service);
        Assert.Contains("\"requested\" => row.Session is not null && TopUpSessionStatuses.IsActive(row.Session.Status)", service);
    }

    [Fact]
    public void ClientNavigation_VersionsDynamicallyLoadedPageScripts()
    {
        var navigationJs = ReadRepoFile("Orbita.Web/wwwroot/js/orbita/navigation.js");

        Assert.Contains("function versionPageScriptUrl(src)", navigationJs);
        Assert.Contains("data-orbita-shell-version", navigationJs);
        Assert.Contains("s.src = versionPageScriptUrl(src);", navigationJs);
    }

    [Fact]
    public void Listings_InitializesServerRenderedKpiCounters()
    {
        var js = ReadRepoFile("Orbita.Web/wwwroot/js/orbita-listings.js");

        var pageInit = js.IndexOf("function init()", StringComparison.Ordinal);
        var counterInit = js.IndexOf("initKpiCounters();", pageInit, StringComparison.Ordinal);
        var liveRegistration = js.IndexOf(
            "registerLivePage('listings'",
            pageInit,
            StringComparison.Ordinal);

        Assert.Contains("function initKpiCounters()", js);
        Assert.Contains("data-listings-kpi-initialized", js);
        Assert.Contains("OrbitaLiveShared.animateKpiValue", js);
        Assert.True(pageInit >= 0);
        Assert.True(counterInit > pageInit);
        Assert.True(liveRegistration > counterInit,
            "Server-rendered listing KPI values must be initialized before live-page registration.");
    }

    [Fact]
    public void ResponsesStatusFilter_InitializesBeforeLivePageRegistration()
    {
        var js = ReadRepoFile("Orbita.Web/wwwroot/js/orbita-responses.js");

        var pageInit = js.IndexOf("function initResponsesPage()", StringComparison.Ordinal);
        var pickerInit = js.IndexOf("initStatusPickers();", pageInit, StringComparison.Ordinal);
        var liveRegistration = js.IndexOf("shared.registerLivePage('responses'", pageInit, StringComparison.Ordinal);

        Assert.True(pageInit >= 0);
        Assert.True(pickerInit > pageInit);
        Assert.True(liveRegistration > pageInit);
        Assert.True(pickerInit < liveRegistration,
            "The response status picker must be interactive before live-page registration.");
    }

    [Fact]
    public void ResponsesFilters_AllowMultipleWorkersAccountsAndCrmDestinations()
    {
        var view = ReadRepoFile("Orbita.Web/Views/Shared/_ResponsesFilterFields.cshtml");
        var picker = ReadRepoFile("Orbita.Web/Views/Shared/_StatisticsMultiSelect.cshtml");

        Assert.Contains("FieldName = \"workerIds\"", view);
        Assert.Contains("FieldName = \"accountIds\"", view);
        Assert.Contains("FieldName = \"bitrixDestination\"", view);
        Assert.Contains("SelectedIds = Model.Filters.WorkerIds", view);
        Assert.Contains("SelectedIds = Model.Filters.AccountIds", view);
        Assert.Contains("SelectedValues = Model.Filters.BitrixDestinations", view);
        Assert.DoesNotContain("name=\"workerId\"", view);
        Assert.DoesNotContain("name=\"accountId\"", view);
        Assert.Contains("data-statistics-multiselect", picker);
        Assert.Contains("name=\"@Model.FieldName\"", picker);
    }

    [Fact]
    public void StatisticsFilters_AllowMultipleWorkersAndAccounts()
    {
        var filters = ReadRepoFile("Orbita.Web/Views/Shared/_StatisticsFilterFields.cshtml");
        var picker = ReadRepoFile("Orbita.Web/Views/Shared/_StatisticsMultiSelect.cshtml");
        var js = ReadRepoFile("Orbita.Web/wwwroot/js/orbita-statistics.js");

        Assert.Contains("FieldName = \"workerIds\"", filters);
        Assert.Contains("FieldName = \"accountIds\"", filters);
        Assert.DoesNotContain("FirstOrDefault()", filters);
        Assert.Contains("data-statistics-multiselect", picker);
        Assert.Contains("name=\"@Model.FieldName\"", picker);
        Assert.Contains("function initStatisticsMultiSelects()", js);
        var filtersJs = ReadRepoFile("Orbita.Web/wwwroot/js/orbita/filters.js");
        Assert.Contains("runtime.initStatisticsMultiSelects", filtersJs);
        Assert.Contains("value.name = fieldName", filtersJs);
        Assert.Contains("[data-statistics-multiselect-trigger]", filtersJs);
    }

    [Fact]
    public void CaptchaStatistics_UsesBrowserLocalTimeFormatter()
    {
        var view = ReadRepoFile("Orbita.Web/Views/Statistics/_StatisticsCaptchaProviderRequests.cshtml");
        var time = ReadRepoFile("Orbita.Web/wwwroot/js/orbita-time.js");

        Assert.Contains("data-orbita-utc=\"@request.SubmittedAtUtc.ToString(\"O\")\"", view);
        Assert.Contains("data-orbita-format=\"datetime-short-seconds\"", view);
        Assert.DoesNotContain("SubmittedAtUtc.ToLocalTime()", view);
        Assert.Contains("case 'datetime-short-seconds':", time);
    }

    [Fact]
    public void StatisticsPreview_ProvidesAccountOptionsForTheAccountMultiSelect()
    {
        var model = DesignPreviewData.BuildStatisticsIndexViewModel(
            DashboardPeriod.Today,
            new PreviewOfficeContext(),
            new StatisticsFiltersViewModel());

        Assert.NotEmpty(model.AccountOptions);
        Assert.Contains(model.AccountOptions, option => option.Label == "Альфа HR");
    }

    [Fact]
    public void StatisticsFilters_InitializeBeforeDeferredChartWorkAfterContentSwap()
    {
        var js = ReadRepoFile("Orbita.Web/wwwroot/js/orbita-statistics.js");
        var filtersJs = ReadRepoFile("Orbita.Web/wwwroot/js/orbita/filters.js");
        var navigationJs = ReadRepoFile("Orbita.Web/wwwroot/js/orbita/navigation.js");

        var schedule = js.IndexOf("function scheduleStatisticsInit()", StringComparison.Ordinal);
        var pickerInit = js.IndexOf("initStatisticsMultiSelects();", schedule, StringComparison.Ordinal);
        var firstAnimationFrame = js.IndexOf("requestAnimationFrame(function ()", schedule, StringComparison.Ordinal);

        Assert.True(schedule >= 0);
        Assert.True(pickerInit > schedule);
        Assert.True(firstAnimationFrame > schedule);
        Assert.True(pickerInit < firstAnimationFrame,
            "The statistics pickers must be interactive before deferred chart initialization.");
        Assert.Contains("runtime.initStatisticsMultiSelects();", filtersJs);
        Assert.Contains("isChartLibrary(src)", navigationJs);
        Assert.Contains("chartReady.then(reinitChartsAfterLibraryLoad);", navigationJs);
        Assert.Contains("url.searchParams.append(key, value);", navigationJs);
        Assert.DoesNotContain("url.searchParams.set(key, value);", navigationJs);

        var controller = ReadRepoFile("Orbita.Web/Controllers/StatisticsController.cs");
        var snapshot = controller.IndexOf("public async Task<IActionResult> Snapshot(", StringComparison.Ordinal);
        Assert.True(snapshot >= 0);
        Assert.Contains("includeFilterCatalog: false", controller[snapshot..]);
    }

    [Fact]
    public void StatisticsTrendTooltip_ShowsDailyResponseCountAndRate()
    {
        var js = ReadRepoFile("Orbita.Web/wwwroot/js/orbita-statistics.js");

        Assert.Contains("elapsedHours", js);
        Assert.Contains("Откликов: ", js);
        Assert.Contains("Скорость: ", js);
        Assert.Contains("откл./ч", js);
        Assert.Contains("function trendTickLabel", js);
        Assert.Contains("callback: function (value, index)", js);
    }

    [Fact]
    public void DashboardJs_InitializesWorkerControlsWhenChartLibraryIsStillLoading()
    {
        var js = ReadRepoFile("Orbita.Web/wwwroot/js/orbita-dashboard.js");

        var configureDefaults = js.IndexOf("function configureChartDefaults()", StringComparison.Ordinal);
        var chartDefaults = js.IndexOf("Chart.defaults.font.family", StringComparison.Ordinal);
        var chartGuard = js.IndexOf("if (!hasChart()) return;", configureDefaults, StringComparison.Ordinal);
        var initAll = js.IndexOf("function initDashboardAll()", StringComparison.Ordinal);
        var toolbarInit = js.IndexOf("initDashboardWorkerToolbar();", initAll, StringComparison.Ordinal);
        var chartEarlyReturn = js.IndexOf("if (typeof Chart ===", initAll, StringComparison.Ordinal);

        Assert.True(configureDefaults >= 0);
        Assert.True(chartGuard > configureDefaults);
        Assert.True(chartDefaults > chartGuard);
        Assert.Contains("initDashboardWorkerToolbar();", js);
        Assert.Contains("configureChartDefaults();", js);
        Assert.True(initAll >= 0);
        Assert.True(toolbarInit > initAll);
        Assert.True(chartEarlyReturn > toolbarInit);
    }

    [Fact]
    public void Dashboard_UsesSingleServerSortControl_WithoutClientDomSorting()
    {
        var js = ReadRepoFile("Orbita.Web/wwwroot/js/orbita-dashboard.js");
        var view = ReadRepoFile("Orbita.Web/Views/Dashboard/Index.cshtml");

        Assert.DoesNotContain("function sortWorkerRows", js);
        Assert.DoesNotContain("orbita-dashboard-worker-sort-key", js);
        Assert.DoesNotContain("orbita-dashboard-worker-sort-direction", js);
        Assert.Contains("initDashboardWorkerToolbar();", js);
        Assert.Contains("data-dashboard-worker-sort-form", js);

        Assert.DoesNotContain("_TableSortTh", view);
        Assert.DoesNotContain("data-table-sort", view);
        Assert.DoesNotContain("По статусу", view);
        Assert.Contains("data-dashboard-worker-sort-form", view);
        Assert.Contains("data-dashboard-worker-sort-key", view);
        Assert.Contains("data-dashboard-worker-sort", view);
        Assert.Contains("name=\"sort\"", view);
        Assert.Contains("name=\"dir\"", view);
        Assert.Contains("По активности", view);
        Assert.Contains("По имени", view);
        Assert.Contains("По откликам", view);
        Assert.Contains("По ошибкам", view);
        Assert.Contains("<th>Воркер</th>", view);
        Assert.Contains("<th>Статус</th>", view);
        Assert.Contains("<th title=\"Последняя активность\">Активность</th>", view);
        Assert.Equal(1, CountOccurrences(view, "data-dashboard-worker-sort-key"));
        Assert.Equal(1, CountOccurrences(view, "data-dashboard-worker-sort-form"));
    }

    [Fact]
    public void Dashboard_MonitoringPauseWarningPersistsAfterLiveRefresh()
    {
        var js = ReadRepoFile("Orbita.Web/wwwroot/js/orbita-dashboard.js");
        var view = ReadRepoFile("Orbita.Web/Views/Dashboard/Index.cshtml");
        var css = ReadRepoFile("Orbita.Web/wwwroot/css/orbita/dashboard.css");

        Assert.Contains("data-dashboard-worker-filter=\"paused\"", view);
        Assert.Contains("data-dashboard-worker-count=\"paused\"", view);
        Assert.Contains("data-dashboard-worker-paused", view);
        Assert.Contains("dashboard-worker-row--monitoring-paused", view);
        Assert.Contains("dashboard-monitoring-paused-tooltip", view);
        Assert.Contains("var monitoringPausedClass = w.IsMonitoringPaused ? \" dashboard-worker-row--monitoring-paused\" : \"\";", view);
        Assert.Contains("class=\"@lowBalanceClass@monitoringPausedClass\"", view);

        Assert.Contains("function updateWorkerToolbar(tabCounts)", js);
        Assert.Contains("updateWorkerToolbar(snapshot.workerTabCounts);", js);
        Assert.Contains("url.searchParams.set('workerFilter', activeWorkerFilter);", js);
        Assert.Contains("url.searchParams.set('page', '1');", js);
        Assert.Contains("data-dashboard-worker-paused", js);
        Assert.Contains("dashboard-worker-row--monitoring-paused", js);
        Assert.Contains("dashboard-monitoring-paused-tooltip", js);

        Assert.Contains("dashboard-worker-filter[data-dashboard-worker-filter=\"paused\"]", css);
        Assert.Contains("dashboard-worker-row--monitoring-paused", css);
        Assert.Contains("dashboard-monitoring-paused-tooltip", css);
    }

    [Fact]
    public void DashboardLiveRenderer_RendersPausedLowBalanceWorkerStateAndPausedFilter()
    {
        var dashboardScript = FindRepoFile("Orbita.Web/wwwroot/js/orbita-dashboard.js");
        var dashboardCss = FindRepoFile("Orbita.Web/wwwroot/css/orbita/dashboard.css");
        var testPage = Path.Combine(Path.GetTempPath(), $"orbita-dashboard-{Guid.NewGuid():N}.html");
        var scriptUrl = new Uri(dashboardScript).AbsoluteUri;
        var cssUrl = new Uri(dashboardCss).AbsoluteUri;
        var page = """
            <!doctype html>
            <html><head><link rel="stylesheet" href="__CSS_URL__"></head><body>
            <main data-orbita-live data-worker-details-url="/workers/__id__" data-settings-logs-url="/workers/__id__/logs">
              <button data-dashboard-worker-filter="all"></button>
              <button data-dashboard-worker-filter="paused"></button>
              <span data-dashboard-worker-count="paused"></span>
              <table class="data-table--dashboard-workers" data-dashboard-workers-table><tbody data-dashboard-workers-body>
                <tr class="dashboard-worker-row dashboard-worker-row--monitoring-paused" data-href="/workers/paused-only" data-dashboard-worker-paused="true"><td class="cell-name">SSR paused worker</td></tr>
              </tbody></table>
            </main>
            <script>window.__orbitaDashboardTestMode = true;</script>
            <script src="__SCRIPT_URL__"></script>
            <script>
            function encodeDashboardTestHtml(value) {
              return btoa(unescape(encodeURIComponent(value)));
            }
            document.documentElement.setAttribute('data-dashboard-test-ssr-worker-html', encodeDashboardTestHtml(document.querySelector('[data-dashboard-workers-body] tr').outerHTML));
            window.OrbitaDashboard.__testApplySnapshot({
              workers: [
                { id: 'paused-low', displayName: 'Paused low balance', isEnabled: true, isOnline: true, isMonitoringPaused: true, lowBalanceAccountCount: 2, totalAccounts: 3, activeAccounts: 2, responses: 1, duplicates: 0, errors: 0 },
                { id: 'paused-only', displayName: 'Paused worker', isEnabled: true, isOnline: true, isMonitoringPaused: true, lowBalanceAccountCount: 0, totalAccounts: 1, activeAccounts: 1, responses: 1, duplicates: 0, errors: 0 },
                { id: 'active', displayName: 'Active worker', isEnabled: true, isOnline: true, isMonitoringPaused: false, lowBalanceAccountCount: 0, totalAccounts: 1, activeAccounts: 1, responses: 1, duplicates: 0, errors: 0 }
              ],
              pagination: { totalItems: 3, page: 1 },
              enabledWorkersCount: 1,
              disabledWorkersCount: 2,
              workerTabCounts: { all: 3, online: 3, offline: 0, empty: 1, paused: 2 }
            }, false);
            var pausedOnly = document.querySelector('[data-dashboard-workers-body] tr[data-href="/workers/paused-only"]');
            var pausedLow = document.querySelector('[data-dashboard-workers-body] tr[data-href="/workers/paused-low"]');
            var lowBalanceIcon = pausedLow.querySelector('.dashboard-low-balance-tooltip');
            var pausedIcon = pausedLow.querySelector('.dashboard-monitoring-paused-tooltip');
            var lowBalanceBox = lowBalanceIcon.getBoundingClientRect();
            var pausedBox = pausedIcon.getBoundingClientRect();
            var pausedOnlyStyle = window.getComputedStyle(pausedOnly);
            document.documentElement.setAttribute('data-dashboard-test-live-worker-html', encodeDashboardTestHtml(pausedOnly.outerHTML));
            document.documentElement.setAttribute('data-dashboard-test-paused-background', pausedOnlyStyle.backgroundColor);
            document.documentElement.setAttribute('data-dashboard-test-paused-border-left-width', pausedOnlyStyle.borderLeftWidth);
            document.documentElement.setAttribute('data-dashboard-test-paused-border-left-color', pausedOnlyStyle.borderLeftColor);
            document.documentElement.setAttribute('data-dashboard-test-warning-icons-overlap', String(!(lowBalanceBox.right <= pausedBox.left || pausedBox.right <= lowBalanceBox.left || lowBalanceBox.bottom <= pausedBox.top || pausedBox.bottom <= lowBalanceBox.top)));
            document.documentElement.setAttribute('data-dashboard-test-paused-low-warning-icon-count', String(pausedLow.querySelectorAll('.fa-triangle-exclamation').length));
            document.documentElement.setAttribute('data-dashboard-test-paused-row-count', String(document.querySelectorAll('[data-dashboard-workers-body] tr[data-dashboard-worker-paused="true"]').length));
            document.documentElement.setAttribute('data-dashboard-test-visible-row-count', String(document.querySelectorAll('[data-dashboard-workers-body] tr[data-href]:not([hidden])').length));
            </script>
            </body></html>
            """.Replace("__SCRIPT_URL__", scriptUrl, StringComparison.Ordinal)
                .Replace("__CSS_URL__", cssUrl, StringComparison.Ordinal);

        try
        {
            File.WriteAllText(testPage, page, Encoding.UTF8);
            var dom = DumpDomWithEdge(testPage);
            var ssrWorkerHtml = DecodeBase64Attribute(dom, "data-dashboard-test-ssr-worker-html");
            var liveWorkerHtml = DecodeBase64Attribute(dom, "data-dashboard-test-live-worker-html");

            Assert.Contains("dashboard-worker-row--monitoring-paused", ssrWorkerHtml);
            Assert.Contains("data-dashboard-worker-paused=\"true\"", ssrWorkerHtml);
            Assert.Contains("dashboard-worker-row--monitoring-paused", liveWorkerHtml);
            Assert.Contains("data-dashboard-worker-paused=\"true\"", liveWorkerHtml);
            Assert.Matches("<tr[^>]*class=\"[^\"]*dashboard-worker-row--low-balance[^\"]*dashboard-worker-row--monitoring-paused[^\"]*\"[^>]*data-dashboard-worker-paused=\"true\"", dom);
            Assert.Contains("workers-status-badge--paused", dom);
            Assert.Contains("dashboard-monitoring-paused-tooltip", dom);
            Assert.Contains("title=\"Мониторинг приостановлен\"", dom);
            Assert.Contains("aria-label=\"Предупреждение: мониторинг приостановлен\"", dom);
            Assert.Contains("dashboard-low-balance-tooltip", dom);
            Assert.Contains("title=\"Субпрофилей с балансом 250 ₽ и ниже без текущей операции: 2\"", dom);
            Assert.Contains("aria-label=\"Предупреждение: 2 субпрофилей требуют пополнения\"", dom);
            Assert.Equal("2", ExtractAttribute(dom, "data-dashboard-test-paused-low-warning-icon-count"));
            Assert.Equal("false", ExtractAttribute(dom, "data-dashboard-test-warning-icons-overlap"));
            Assert.Equal("rgb(255, 250, 235)", ExtractAttribute(dom, "data-dashboard-test-paused-background"));
            Assert.Equal("1px", ExtractAttribute(dom, "data-dashboard-test-paused-border-left-width"));
            Assert.Equal("rgb(247, 144, 9)", ExtractAttribute(dom, "data-dashboard-test-paused-border-left-color"));
            Assert.Equal("2", ExtractElementText(dom, "data-dashboard-worker-count=\"paused\""));
            Assert.Equal("2", ExtractAttribute(dom, "data-dashboard-test-paused-row-count"));
            Assert.Equal("3", ExtractAttribute(dom, "data-dashboard-test-visible-row-count"));
            Assert.Matches("<tr[^>]*data-href=\"/workers/paused-low\"(?![^>]* hidden)[^>]*>", dom);
            Assert.Matches("<tr[^>]*data-href=\"/workers/paused-only\"(?![^>]* hidden)[^>]*>", dom);
            Assert.Matches("<tr[^>]*data-href=\"/workers/active\"(?![^>]* hidden)[^>]*>", dom);
        }
        finally
        {
            File.Delete(testPage);
        }
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string ReadRepoFile(string relativePath)
    {
        return File.ReadAllText(FindRepoFile(relativePath));
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException(relativePath);
    }

    private static string DumpDomWithEdge(string pagePath)
    {
        var browserPath = FindHeadlessBrowser();

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = browserPath,
            Arguments = $"--headless=new --disable-gpu --no-first-run --no-default-browser-check --allow-file-access-from-files --dump-dom --virtual-time-budget=3000 \"{new Uri(pagePath).AbsoluteUri}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Assert.NotNull(process);
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "Microsoft Edge did not finish rendering the dashboard test page.");
        Assert.True(process.ExitCode == 0, $"Microsoft Edge failed with exit code {process.ExitCode}: {error}");
        return output;
    }

    private static string FindHeadlessBrowser()
    {
        var configuredBrowser = Environment.GetEnvironmentVariable("ORBITA_HEADLESS_BROWSER");
        var candidates = new[]
        {
            configuredBrowser,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            "/usr/bin/microsoft-edge",
            "/usr/bin/microsoft-edge-stable",
            "/usr/bin/google-chrome",
            "/usr/bin/google-chrome-stable",
            "/usr/bin/chromium",
            "/usr/bin/chromium-browser",
            "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
        };

        var browserPath = candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
        Assert.True(browserPath is not null,
            "No supported headless browser was found. Set ORBITA_HEADLESS_BROWSER or install Edge, Chrome, or Chromium.");
        return browserPath!;
    }

    private static string ExtractElementText(string html, string attribute)
    {
        var marker = html.IndexOf(attribute, StringComparison.Ordinal);
        Assert.True(marker >= 0, $"Element with {attribute} was not rendered.");
        var start = html.IndexOf('>', marker) + 1;
        var end = html.IndexOf('<', start);
        return html[start..end].Trim();
    }

    private static string ExtractAttribute(string html, string attributeName)
    {
        var marker = attributeName + "=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Attribute {attributeName} was not rendered.");
        start += marker.Length;
        var end = html.IndexOf('"', start);
        return html[start..end];
    }

    private static string DecodeBase64Attribute(string html, string attributeName)
    {
        return Encoding.UTF8.GetString(Convert.FromBase64String(ExtractAttribute(html, attributeName)));
    }

    private sealed class PreviewOfficeContext : IOfficeContext
    {
        public bool IsAdmin => false;
        public bool ShowAllOffices => false;
        public bool ShowOfficeColumn => false;
        public Guid? EffectiveOfficeId => null;
        public string? ContextLabel => null;
        public void Bind(HttpContext context) { }
    }
}
