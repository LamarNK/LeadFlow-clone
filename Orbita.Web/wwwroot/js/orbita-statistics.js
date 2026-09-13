(function () {
    var shared = window.OrbitaLiveShared;

    function getLiveRoot() {
        return document.querySelector('[data-orbita-live-page="statistics"]');
    }

    function stableJson(value) {
        return shared && typeof shared.stableJson === 'function'
            ? shared.stableJson(value)
            : JSON.stringify(value);
    }

    function readChartsPayload() {
        var el = document.getElementById('statistics-charts-data');
        if (!el) return null;
        try {
            return JSON.parse(el.textContent || '{}');
        } catch (e) {
            console.error('Statistics charts: invalid JSON', e);
            return null;
        }
    }

    var chartRegistry = {
        trend: null,
        donut: null
    };

    var chartsFingerprint = '';

    function destroyChart(chart) {
        if (chart) {
            try { chart.destroy(); } catch (e) { }
        }
    }

    function destroyChartOnCanvas(canvas) {
        if (!canvas || typeof Chart === 'undefined' || typeof Chart.getChart !== 'function') return;
        var existing = Chart.getChart(canvas);
        if (existing) {
            try { existing.destroy(); } catch (e) { }
        }
    }

    function destroyAllCharts() {
        destroyChart(chartRegistry.trend);
        destroyChart(chartRegistry.donut);
        chartRegistry.trend = null;
        chartRegistry.donut = null;
        chartsFingerprint = '';
        destroyChartOnCanvas(document.getElementById('chart-statistics-trend'));
        destroyChartOnCanvas(document.getElementById('chart-statistics-account-status'));
    }

    var TREND_SERIES = [
        { key: 'sent', label: 'Битрикс24', color: '#22c55e' },
        { key: 'inProgress', label: 'В работе', color: '#3b82f6' },
        { key: 'actionRequired', label: 'Нужно действие', color: '#8b5cf6' },
        { key: 'duplicates', label: 'Дубли', color: '#f59e0b' },
        { key: 'errors', label: 'Ошибки', color: '#ef4444' }
    ];

    function readPeriodDays() {
        var root = document.querySelector('.statistics-trend-chart');
        var raw = root ? parseInt(root.getAttribute('data-period-days') || '0', 10) : 0;
        return raw > 0 ? raw : 0;
    }

    function getBucketMeta(dayCount) {
        if (dayCount > 90) {
            return { size: 30, label: 'по месяцам', avgSuffix: '/мес' };
        }
        if (dayCount > 31) {
            return { size: 7, label: 'по неделям', avgSuffix: '/нед' };
        }
        return { size: 1, label: 'по дням', avgSuffix: '/день' };
    }

    function sumSeriesSlice(trend, key, start, end) {
        var values = trend[key] || [];
        var sum = 0;
        for (var i = start; i < end; i++) {
            sum += values[i] || 0;
        }
        return sum;
    }

    function sumNumbersSlice(values, start, end) {
        var sum = 0;
        for (var i = start; i < end; i++) {
            sum += Number(values[i]) || 0;
        }
        return sum;
    }

    function normalizeDailyTrend(trend) {
        if (!trend) return null;
        var labels = trend.labels || [];
        if (!labels.length) return null;

        var sent = (trend.sent || []).slice();
        var inProgress = (trend.inProgress || []).slice();
        var actionRequired = (trend.actionRequired || []).slice();
        var duplicates = (trend.duplicates || []).slice();
        var errors = (trend.errors || []).slice();
        var totals = Array.isArray(trend.totals) ? trend.totals.slice() : [];
        var elapsedHours = Array.isArray(trend.elapsedHours) ? trend.elapsedHours.slice() : [];

        if (!totals.length || totals.every(function (v) { return !(v || 0); })) {
            totals = labels.map(function (_, index) {
                return (sent[index] || 0)
                    + (inProgress[index] || 0)
                    + (actionRequired[index] || 0)
                    + (duplicates[index] || 0)
                    + (errors[index] || 0);
            });
        }
        if (elapsedHours.length !== labels.length) {
            elapsedHours = labels.map(function () { return 24; });
        }

        return {
            labels: labels,
            totals: totals,
            elapsedHours: elapsedHours,
            sent: sent,
            inProgress: inProgress,
            actionRequired: actionRequired,
            duplicates: duplicates,
            errors: errors
        };
    }

    function aggregateTrend(trend, bucketSize) {
        if (!trend || bucketSize <= 1) return trend;

        var labels = [];
        var aggregated = {
            sent: [],
            inProgress: [],
            actionRequired: [],
            duplicates: [],
            errors: [],
            totals: [],
            elapsedHours: []
        };

        for (var i = 0; i < trend.labels.length; i += bucketSize) {
            var end = Math.min(i + bucketSize, trend.labels.length);
            var label = trend.labels[i];
            if (end - i > 1) {
                label = trend.labels[i] + '–' + trend.labels[end - 1];
            }
            labels.push(label);

            TREND_SERIES.forEach(function (series) {
                var value = sumSeriesSlice(trend, series.key, i, end);
                aggregated[series.key].push(value);
            });
            aggregated.totals.push(sumSeriesSlice(trend, 'totals', i, end));
            aggregated.elapsedHours.push(sumNumbersSlice(trend.elapsedHours || [], i, end));
        }

        aggregated.labels = labels;
        return aggregated;
    }

    function prepareTrendForChart(trend) {
        trend = normalizeDailyTrend(trend);
        if (!trend) return null;

        var dayCount = readPeriodDays() || trend.labels.length;
        var bucket = getBucketMeta(dayCount);
        var chartTrend = aggregateTrend(trend, bucket.size);
        chartTrend._bucket = bucket;
        return chartTrend;
    }

    function trendHasData(trend) {
        trend = normalizeDailyTrend(trend);
        if (!trend) return false;
        return trend.totals.some(function (v) { return (v || 0) > 0; });
    }

    function stackedYBounds(trend) {
        var maxStack = 0;
        (trend.labels || []).forEach(function (_, index) {
            var stack = 0;
            TREND_SERIES.forEach(function (series) {
                stack += (trend[series.key] || [])[index] || 0;
            });
            if (stack > maxStack) maxStack = stack;
        });

        if (maxStack === 0) {
            return { yMin: 0, yMax: 5, step: 1 };
        }

        var padded = maxStack + Math.max(1, Math.ceil(maxStack * 0.12));
        var step = Math.max(1, Math.ceil(padded / 5));
        return { yMin: 0, yMax: Math.ceil(padded / step) * step, step: step };
    }

    function formatTrendNumber(value) {
        var num = Number(value) || 0;
        if (num >= 10000) {
            return (num / 1000).toFixed(1).replace(/\.0$/, '') + 'k';
        }
        return String(Math.round(num * 10) / 10);
    }

    function formatTrendRate(total, elapsedHours) {
        var hours = Math.max(1, Number(elapsedHours) || 0);
        var rate = (Number(total) || 0) / hours;
        return '≈ ' + rate.toLocaleString('ru-RU', {
            minimumFractionDigits: 0,
            maximumFractionDigits: 1
        }) + ' откл./ч';
    }

    function trendTooltipFooter(trend, items) {
        if (!items.length || !trend) return '';
        var idx = items[0].dataIndex;
        var total = (trend.totals || [])[idx] || 0;
        var elapsedHours = (trend.elapsedHours || [])[idx] || 24;
        return [
            'Откликов: ' + total,
            'Скорость: ' + formatTrendRate(total, elapsedHours)
        ];
    }

    function trendTickLabel(trend, index) {
        var total = (trend.totals || [])[index] || 0;
        var elapsedHours = (trend.elapsedHours || [])[index] || 24;
        return [
            (trend.labels || [])[index] || '',
            formatTrendNumber(total) + ' откл.',
            formatTrendRate(total, elapsedHours)
        ];
    }

    function updateTrendSummary(trend) {
        var summary = document.querySelector('[data-statistics-trend-summary]');
        if (!summary || !trend) return;

        var total = (trend.totals || []).reduce(function (acc, value) { return acc + (value || 0); }, 0);
        var peak = (trend.totals || []).reduce(function (acc, value) { return Math.max(acc, value || 0); }, 0);
        var bucketCount = Math.max(1, (trend.labels || []).length);
        var avg = Math.round((total / bucketCount) * 10) / 10;
        var bucket = trend._bucket || getBucketMeta(readPeriodDays() || bucketCount);

        var totalEl = summary.querySelector('[data-trend-total]');
        var peakEl = summary.querySelector('[data-trend-peak]');
        var avgEl = summary.querySelector('[data-trend-avg]');
        var avgSuffixEl = summary.querySelector('[data-trend-avg-suffix]');
        var bucketEl = summary.querySelector('[data-trend-bucket]');

        if (totalEl) totalEl.textContent = formatTrendNumber(total);
        if (peakEl) peakEl.textContent = formatTrendNumber(peak);
        if (avgEl) avgEl.textContent = formatTrendNumber(avg);
        if (avgSuffixEl) avgSuffixEl.textContent = bucket.avgSuffix;
        if (bucketEl) bucketEl.textContent = bucket.label;
    }

    function buildTrendDatasets(trend) {
        var barCount = (trend.labels || []).length;
        var maxBarThickness = barCount <= 12 ? 42 : barCount <= 24 ? 32 : barCount <= 52 ? 22 : 14;

        return TREND_SERIES.map(function (series) {
            return {
                label: series.label,
                data: trend[series.key] || [],
                backgroundColor: series.color,
                borderColor: series.color,
                borderWidth: 0,
                stack: 'responses',
                maxBarThickness: maxBarThickness,
                borderRadius: 2,
                borderSkipped: false
            };
        });
    }

    function buildTrendOptions(trend) {
        var labels = trend.labels || [];
        var yBounds = stackedYBounds(trend);
        var autoSkip = labels.length > 14;
        return {
            responsive: true,
            maintainAspectRatio: false,
            animation: false,
            interaction: { mode: 'index', intersect: false },
            datasets: {
                bar: {
                    categoryPercentage: 0.82,
                    barPercentage: 0.9
                }
            },
            plugins: {
                legend: { display: false },
                tooltip: {
                    enabled: true,
                    mode: 'index',
                    intersect: false,
                    backgroundColor: '#ffffff',
                    titleColor: '#101828',
                    bodyColor: '#667085',
                    borderColor: '#eef2f7',
                    borderWidth: 1,
                    padding: { top: 10, right: 14, bottom: 10, left: 14 },
                    cornerRadius: 12,
                    displayColors: true,
                    titleFont: { size: 13, weight: '600' },
                    bodyFont: { size: 13, weight: '400' },
                    caretSize: 6,
                    caretPadding: 10,
                    filter: function (item) {
                        return (item.parsed.y || 0) > 0;
                    },
                    callbacks: {
                        title: function (items) {
                            if (!items.length) return '';
                            return String(labels[items[0].dataIndex] || items[0].label || '');
                        },
                        label: function (ctx) {
                            return (ctx.dataset.label || 'Значение') + ': ' + (ctx.parsed.y || 0);
                        },
                        footer: function (items) {
                            return trendTooltipFooter(trend, items);
                        }
                    }
                }
            },
            scales: {
                x: {
                    stacked: true,
                    grid: { display: false },
                    border: { display: false },
                    ticks: {
                        color: '#667085',
                        font: { size: 11, weight: '500' },
                        callback: function (value, index) {
                            return trendTickLabel(trend, index);
                        },
                        maxRotation: labels.length > 20 ? 45 : 0,
                        autoSkip: autoSkip,
                        maxTicksLimit: autoSkip ? 12 : labels.length
                    }
                },
                y: {
                    stacked: true,
                    min: yBounds.yMin,
                    max: yBounds.yMax,
                    grid: { color: '#f2f4f7', lineWidth: 1 },
                    border: { display: false },
                    ticks: {
                        stepSize: yBounds.step,
                        color: '#98a2b3',
                        font: { size: 11 },
                        padding: 6,
                        precision: 0
                    }
                }
            },
            layout: {
                padding: { top: 8, right: 6, bottom: 0, left: 0 }
            }
        };
    }

    function setTrendChartState(state) {
        var root = document.querySelector('.statistics-trend-chart');
        if (root) root.setAttribute('data-chart-state', state);

        var summary = document.querySelector('[data-statistics-trend-summary]');
        if (summary) summary.hidden = state === 'empty';
    }

    function buildDonutOptions() {
        return {
            responsive: true,
            maintainAspectRatio: false,
            animation: false,
            cutout: '68%',
            plugins: { legend: { display: false } }
        };
    }

    function updateTrendChart(chart, trend) {
        if (!chart || !trend) return false;

        if (!trendHasData(trend)) {
            destroyChart(chart);
            chartRegistry.trend = null;
            setTrendChartState('empty');
            return false;
        }

        var chartTrend = prepareTrendForChart(trend);
        if (!chartTrend) {
            setTrendChartState('empty');
            return false;
        }

        setTrendChartState('ready');
        updateTrendSummary(chartTrend);

        var yBounds = stackedYBounds(chartTrend);
        chart.data.labels = chartTrend.labels || [];
        chart.data.datasets = buildTrendDatasets(chartTrend);
        chart.options.scales.y.min = yBounds.yMin;
        chart.options.scales.y.max = yBounds.yMax;
        chart.options.scales.y.ticks.stepSize = yBounds.step;
        chart.options.scales.x.ticks.autoSkip = chartTrend.labels.length > 14;
        chart.options.scales.x.ticks.maxTicksLimit = chartTrend.labels.length > 14 ? 12 : chartTrend.labels.length;
        chart.options.scales.x.ticks.maxRotation = chartTrend.labels.length > 20 ? 45 : 0;
        chart.options.scales.x.ticks.callback = function (value, index) {
            return trendTickLabel(chartTrend, index);
        };
        chart.options.plugins.tooltip.callbacks.footer = function (items) {
            return trendTooltipFooter(chartTrend, items);
        };
        chart.options.plugins.tooltip.callbacks.title = function (items) {
            if (!items.length) return '';
            return String((chartTrend.labels || [])[items[0].dataIndex] || items[0].label || '');
        };
        chart.update('none');
        return true;
    }

    function updateDonutChart(chart, stats) {
        if (!chart || !stats) return false;

        var values = [stats.active, stats.inactive, stats.blocked, stats.errors];
        if (values.every(function (v) { return !v; })) {
            values = [1];
        }
        chart.data.datasets[0].data = values;
        chart.update('none');
        return true;
    }

    function initTrendChart(payload) {
        var canvas = document.getElementById('chart-statistics-trend');
        if (!canvas || typeof Chart === 'undefined' || !payload || !payload.dailyTrend) return;

        var rawTrend = payload.dailyTrend;
        if (!trendHasData(rawTrend)) {
            destroyChart(chartRegistry.trend);
            chartRegistry.trend = null;
            destroyChartOnCanvas(canvas);
            setTrendChartState('empty');
            return;
        }

        var chartTrend = prepareTrendForChart(rawTrend);
        if (!chartTrend) {
            setTrendChartState('empty');
            return;
        }

        setTrendChartState('ready');
        updateTrendSummary(chartTrend);

        if (chartRegistry.trend) {
            if (chartRegistry.trend.config.type !== 'bar') {
                destroyChart(chartRegistry.trend);
                chartRegistry.trend = null;
                destroyChartOnCanvas(canvas);
            } else {
                updateTrendChart(chartRegistry.trend, rawTrend);
                return;
            }
        }

        destroyChartOnCanvas(canvas);
        chartRegistry.trend = new Chart(canvas, {
            type: 'bar',
            data: {
                labels: chartTrend.labels || [],
                datasets: buildTrendDatasets(chartTrend)
            },
            options: buildTrendOptions(chartTrend)
        });
    }

    function initDonutChart(payload) {
        var canvas = document.getElementById('chart-statistics-account-status');
        if (!canvas || typeof Chart === 'undefined' || !payload || !payload.accountStatus) return;

        var stats = payload.accountStatus;
        if (chartRegistry.donut) {
            updateDonutChart(chartRegistry.donut, stats);
            return;
        }

        var values = [stats.active, stats.inactive, stats.blocked, stats.errors];
        if (values.every(function (v) { return !v; })) {
            values = [1];
        }

        destroyChartOnCanvas(canvas);
        chartRegistry.donut = new Chart(canvas, {
            type: 'doughnut',
            data: {
                labels: ['Активны', 'Неактивны', 'Заблокированы', 'Ошибки'],
                datasets: [{
                    data: values,
                    backgroundColor: ['#22c55e', '#94a3b8', '#ef4444', '#f59e0b'],
                    borderWidth: 0
                }]
            },
            options: buildDonutOptions()
        });
    }

    function initKpiCounters() {
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

        document.querySelectorAll('.statistics-kpi-row [data-kpi-count]').forEach(function (el, index) {
            var target = parseFloat(el.getAttribute('data-kpi-count'));
            var suffix = el.getAttribute('data-kpi-suffix') || '';
            if (isNaN(target)) return;

            if (reduced) {
                el.textContent = Math.round(target) + suffix;
                el.setAttribute('data-kpi-suffix', suffix);
                return;
            }

            if (shared && typeof shared.animateKpiValue === 'function') {
                shared.animateKpiValue(el, 0, target, suffix, 720, 80 + index * 70);
                return;
            }

            el.textContent = Math.round(target) + suffix;
            el.setAttribute('data-kpi-suffix', suffix);
        });
    }

    function updateAccountStats(stats) {
        if (!stats) return;
        var total = Math.max(1, stats.total || 0);
        function pct(value) {
            return Math.round((value || 0) * 100 / total) + '%';
        }

        document.querySelectorAll('[data-account-stat="total"]').forEach(function (el) {
            el.textContent = String(stats.total || 0);
        });
        document.querySelectorAll('[data-account-stat="active"]').forEach(function (el) {
            el.textContent = (stats.active || 0) + ' (' + pct(stats.active) + ')';
        });
        document.querySelectorAll('[data-account-stat="inactive"]').forEach(function (el) {
            el.textContent = (stats.inactive || 0) + ' (' + pct(stats.inactive) + ')';
        });
        document.querySelectorAll('[data-account-stat="blocked"]').forEach(function (el) {
            el.textContent = (stats.blocked || 0) + ' (' + pct(stats.blocked) + ')';
        });
        document.querySelectorAll('[data-account-stat="errors"]').forEach(function (el) {
            el.textContent = (stats.errors || 0) + ' (' + pct(stats.errors) + ')';
        });
    }

    function escapeHtml(text) {
        return shared && typeof shared.escapeHtml === 'function'
            ? shared.escapeHtml(text)
            : String(text)
                .replace(/&/g, '&amp;')
                .replace(/</g, '&lt;')
                .replace(/>/g, '&gt;')
                .replace(/"/g, '&quot;');
    }

    function workerDetailsUrl(workerId) {
        var root = getLiveRoot();
        var template = root ? root.getAttribute('data-worker-details-url') : '';
        return shared && typeof shared.urlFromTemplate === 'function'
            ? shared.urlFromTemplate(template, '__id__', workerId)
            : template.split('__id__').join(encodeURIComponent(String(workerId)));
    }

    function accountSearchUrl(accountName) {
        var root = getLiveRoot();
        var base = root ? (root.getAttribute('data-accounts-url') || '/Accounts') : '/Accounts';
        return base + (base.indexOf('?') > -1 ? '&' : '?') + 'q=' + encodeURIComponent(accountName || '');
    }

    function renderBalanceSubProfiles(subProfiles) {
        if (!subProfiles || !subProfiles.length) {
            return '';
        }

        var items = subProfiles.map(function (subProfile) {
            var wallet = subProfile.walletText
                ? '<span class="statistics-balance-subprofile-wallet" title="Кошелёк">' + escapeHtml(subProfile.walletText) + '</span>'
                : '';
            var duration = subProfile.durationText
                ? '<span class="statistics-balance-subprofile-duration">' + escapeHtml(subProfile.durationText) + '</span>'
                : '';

            return '<div class="statistics-balance-subprofile' + (subProfile.isLowBalance ? ' statistics-balance-subprofile--low' : '') + '">'
                + '<div class="statistics-balance-subprofile-head">'
                + '<span class="statistics-balance-subprofile-name">' + escapeHtml(subProfile.name) + '</span>'
                + '<div class="statistics-balance-subprofile-amounts">'
                + '<span class="statistics-balance-subprofile-advance" title="Аванс">' + escapeHtml(subProfile.advanceText) + '</span>'
                + wallet
                + '</div>'
                + '</div>'
                + duration
                + '<div class="statistics-balance-bar-track statistics-balance-bar-track--sub" aria-hidden="true">'
                + '<span class="statistics-balance-bar-fill" style="width:' + ((subProfile.barWidth || 0) * 100).toFixed(2) + '%"></span>'
                + '</div>'
                + '</div>';
        }).join('');

        return '<details class="statistics-balance-subprofiles-details">'
            + '<summary class="statistics-balance-subprofiles-summary">'
            + '<i class="fa-solid fa-chevron-right statistics-balance-subprofiles-caret" aria-hidden="true"></i>'
            + subProfiles.length + ' субпроф.'
            + '</summary>'
            + '<div class="statistics-balance-subprofiles">' + items + '</div>'
            + '</details>';
    }

    function renderBalanceRows(rows, showOfficeColumn) {
        var container = document.querySelector('[data-statistics-balances]');
        if (!container) return;

        if (!rows || rows.length === 0) {
            return;
        }

        var list = container.querySelector('.statistics-balance-list');
        if (!list) return;

        list.innerHTML = rows.map(function (row) {
            var office = showOfficeColumn && row.officeName
                ? '<span>· ' + escapeHtml(row.officeName) + '</span>'
                : '';
            var wallet = row.wallet > 0
                ? '<span class="statistics-balance-wallet" title="Кошелёк">' + escapeHtml(row.walletText) + '</span>'
                : '';
            var subProfiles = renderBalanceSubProfiles(row.subProfiles);
            var foot = '';
            if (!subProfiles && row.balanceSubtitle) {
                foot = '<div class="statistics-balance-foot">'
                    + '<span class="statistics-balance-subtitle">' + escapeHtml(row.balanceSubtitle) + '</span>'
                    + '</div>';
            }

            return '<div class="statistics-balance-row' + (row.isLowBalance ? ' statistics-balance-row--low' : '') + '" data-balance-account-id="' + row.accountId + '">'
                + '<div class="statistics-balance-head">'
                + '<div class="statistics-balance-title">'
                + '<a href="' + escapeHtml(accountSearchUrl(row.accountName)) + '" class="statistics-balance-name">' + escapeHtml(row.accountName) + '</a>'
                + '<span class="statistics-balance-meta">' + escapeHtml(row.workerName) + office + '</span>'
                + '</div>'
                + '<div class="statistics-balance-amounts">'
                + '<span class="statistics-balance-advance" title="Аванс">'
                + ((row.subProfiles && row.subProfiles.length) ? 'суммарно ' : '')
                + escapeHtml(row.advanceText) + '</span>'
                + wallet
                + '</div>'
                + '</div>'
                + '<div class="statistics-balance-bar-track" aria-hidden="true">'
                + '<span class="statistics-balance-bar-fill" style="width:' + ((row.barWidth || 0) * 100).toFixed(2) + '%"></span>'
                + '</div>'
                + subProfiles
                + foot
                + '</div>';
        }).join('');
    }

    function getMonitoringAccountDetailRow(row) {
        if (!row) return null;
        var accountName = row.getAttribute('data-monitoring-account') || '';
        if (!accountName) return null;
        var root = row.closest('[data-monitoring-accounts]');
        if (!root) return null;
        return root.querySelector('[data-monitoring-account-detail="' + accountName + '"]');
    }

    function setMonitoringAccountExpanded(row, expanded) {
        var detailRow = getMonitoringAccountDetailRow(row);
        row.setAttribute('aria-expanded', expanded ? 'true' : 'false');
        if (detailRow) {
            detailRow.hidden = !expanded;
        }
    }

    function initMonitoringAccountRows() {
        document.querySelectorAll('[data-monitoring-account-toggle]').forEach(function (row) {
            if (row.hasAttribute('data-monitoring-account-bound')) return;
            row.setAttribute('data-monitoring-account-bound', '1');

            row.addEventListener('click', function (e) {
                if (e.target.closest('a') || e.target.closest('button') || e.target.closest('form')) return;
                var expanded = row.getAttribute('aria-expanded') === 'true';
                setMonitoringAccountExpanded(row, !expanded);
            });
        });
    }

    function initMonitoringSearch() {
        var input = document.querySelector('[data-monitoring-search]');
        var accountsRoot = document.querySelector('[data-monitoring-accounts]');
        if (!input || !accountsRoot) return;
        if (input.hasAttribute('data-monitoring-search-bound')) return;
        input.setAttribute('data-monitoring-search-bound', '1');

        var meta = document.querySelector('[data-monitoring-search-meta]');
        var items = Array.prototype.slice.call(
            accountsRoot.querySelectorAll('[data-monitoring-account]'));

        function applyFilter() {
            var query = (input.value || '').trim().toLowerCase();
            var visible = 0;
            items.forEach(function (item) {
                var name = (item.getAttribute('data-monitoring-account') || '').toLowerCase();
                var match = !query || name.indexOf(query) > -1;
                item.hidden = !match;
                var detailRow = getMonitoringAccountDetailRow(item);
                if (detailRow) {
                    detailRow.hidden = !match || item.getAttribute('aria-expanded') !== 'true';
                }
                if (match) visible++;
            });
            if (meta) {
                meta.textContent = query
                    ? visible + ' из ' + items.length
                    : items.length + ' аккаунт(ов)';
            }
        }

        input.addEventListener('input', applyFilter);
        applyFilter();
    }

    function renderHrTable(title, rows) {
        var panels = document.querySelectorAll('.statistics-hr-panel');
        var panel = Array.prototype.find.call(panels, function (el) {
            var heading = el.querySelector('.statistics-hr-title');
            return heading && heading.textContent === title;
        });
        if (!panel) return;

        var tbody = panel.querySelector('tbody');
        if (!tbody) return;

        if (!rows || rows.length === 0) {
            tbody.innerHTML = '<tr><td colspan="5" class="cell-muted">Нет данных за период</td></tr>';
            return;
        }

        tbody.innerHTML = rows.map(function (row) {
            return '<tr>'
                + '<td>' + escapeHtml(row.name) + '</td>'
                + '<td class="cell-num">' + row.total + '</td>'
                + '<td class="cell-num">' + row.sent + '</td>'
                + '<td class="cell-num">' + escapeHtml(row.conversionText) + '</td>'
                + '<td class="cell-num">' + escapeHtml(row.shareText) + '</td>'
                + '</tr>';
        }).join('');
    }

    function renderDeliveries(bitrixRows, crmRows) {
        var container = document.querySelector('[data-statistics-bitrix-deliveries]');
        if (!container) return;

        var deliveries = (bitrixRows || []).map(function (row) {
            return {
                channel: 'Битрикс24',
                channelKind: 'bitrix',
                url: row.responsesUrl || row.ResponsesUrl || '#',
                label: row.label || row.Label || 'Битрикс',
                count: Number(row.sentCount != null ? row.sentCount : row.SentCount) || 0
            };
        }).concat((crmRows || []).map(function (row) {
            return {
                channel: 'CRM',
                channelKind: 'crm',
                url: row.responsesUrl || row.ResponsesUrl || '#',
                label: row.label || row.Label || 'Офис CRM',
                count: Number(row.sentCount != null ? row.sentCount : row.SentCount) || 0
            };
        })).filter(function (row) { return row.count > 0; })
            .sort(function (left, right) { return right.count - left.count || left.label.localeCompare(right.label); });

        var totalEl = document.querySelector('[data-statistics-bitrix-total]');
        var total = deliveries.reduce(function (sum, row) { return sum + row.count; }, 0);
        if (totalEl) totalEl.textContent = total + ' всего за период';

        if (!deliveries.length) {
            container.innerHTML = '<div class="table-empty-state">'
                + '<i class="fa-solid fa-paper-plane" aria-hidden="true"></i>'
                + '<p class="table-empty-title">Нет отправок за период</p>'
                + '<p class="table-empty-desc">Отклики ещё не отправлялись в CRM или Битрикс24.</p>'
                + '</div>';
            return;
        }

        var maxCount = Math.max.apply(null, deliveries.map(function (row) { return row.count; })) || 1;
        var bars = deliveries.map(function (row) {
            var width = Math.round((row.count / maxCount) * 1000) / 10;
            var share = Math.round((row.count / total) * 1000) / 10;
            return '<a class="statistics-bitrix-bar statistics-bitrix-bar--' + row.channelKind + '" href="' + escapeHtml(row.url) + '" style="--statistics-bitrix-bar-width:' + width + '%"'
                + ' aria-label="' + escapeHtml(row.channel + ', ' + row.label + ': ' + row.count + ' отправлено, ' + share + '% от всех отправок') + '">'
                + '<span class="statistics-bitrix-bar__label"><i aria-hidden="true"></i><span class="statistics-bitrix-bar__label-text" title="' + escapeHtml(row.label) + '">' + escapeHtml(row.label) + '</span><em class="statistics-bitrix-bar__channel">' + row.channel + '</em></span>'
                + '<span class="statistics-bitrix-bar__track" aria-hidden="true"><b></b></span>'
                + '<strong>' + row.count + '</strong><small>' + share + '%</small></a>';
        }).join('');

        container.innerHTML = '<div class="statistics-bitrix-chart">'
            + '<div class="statistics-bitrix-chart__total"><span>Всего отправлено</span><strong>' + total + '</strong><small>за выбранный период</small></div>'
            + '<div class="statistics-bitrix-chart__bars" aria-label="Распределение отправок по каналам">' + bars + '</div></div>';
    }

    function renderAgeBuckets(rows) {
        var tbody = document.querySelector('[data-statistics-age-buckets]');
        if (!tbody) return;
        if (!rows || rows.length === 0) {
            tbody.innerHTML = '<tr><td colspan="4" class="cell-muted">Нет данных за период</td></tr>';
            return;
        }
        tbody.innerHTML = rows.map(function (row) {
            return '<tr>'
                + '<td>' + escapeHtml(row.bucket) + '</td>'
                + '<td class="cell-num">' + row.total + '</td>'
                + '<td class="cell-num">' + row.sent + '</td>'
                + '<td class="cell-num">' + escapeHtml(row.conversionText) + '</td>'
                + '</tr>';
        }).join('');
    }

    function renderWorkers(rows, showOfficeColumn) {
        var tbody = document.querySelector('[data-orbita-live-body="statistics-workers"]');
        if (!tbody) return;

        tbody.innerHTML = (rows || []).map(function (worker) {
            var detailsUrl = workerDetailsUrl(worker.id);
            var officeCell = showOfficeColumn
                ? '<td data-label="Офис">' + escapeHtml(worker.officeName || '') + '</td>'
                : '';
            var statusClass = worker.isOnline ? '' : ' offline';
            var statusLabel = worker.isOnline ? 'Онлайн' : 'Оффлайн';
            return '<tr class="statistics-worker-row" data-href="' + escapeHtml(detailsUrl) + '" data-worker-id="' + worker.id + '">'
                + '<td class="cell-name" data-label="Воркер"><a href="' + escapeHtml(detailsUrl) + '">' + escapeHtml(worker.displayName) + '</a></td>'
                + officeCell
                + '<td data-label="Статус"><span class="status-dot' + statusClass + '"><i class="fa-solid fa-circle status-dot-icon" aria-hidden="true"></i>' + statusLabel + '</span></td>'
                + '<td class="cell-num" data-label="Отклики">' + worker.periodResponses + '</td>'
                + '<td class="cell-num" data-label="Битрикс24">' + (worker.periodSent || 0) + '</td>'
                + '<td class="cell-num" data-label="Дубли">' + worker.periodDuplicates + '</td>'
                + '<td class="cell-num" data-label="Ошибки">' + worker.periodErrors + '</td>'
                + '<td class="cell-num" data-label="Аккаунты">' + worker.activeAccounts + ' / ' + worker.totalAccounts + '</td>'
                + '</tr>';
        }).join('');

        initRowNavigation();
    }

    function updateSummaryMeta(summary) {
        if (!summary) return;

        var advance = document.querySelector('[data-statistics-infra="advance"]');
        if (advance && summary.totalAdvanceText) {
            advance.textContent = summary.totalAdvanceText;
        }

        var adsMeta = document.querySelector('[data-statistics-ads-meta]');
        if (adsMeta) {
            adsMeta.textContent = (summary.activeAdsCount || 0) + ' активных / ' + (summary.blockedAdsCount || 0) + ' заблок.';
        }
    }

    function initRowNavigation() {
        document.querySelectorAll('.statistics-worker-row[data-href]').forEach(function (row) {
            if (row.hasAttribute('data-statistics-row-bound')) return;
            row.setAttribute('data-statistics-row-bound', '1');

            row.addEventListener('click', function (e) {
                if (e.target.closest('a') || e.target.closest('button') || e.target.closest('form')) return;
                var href = row.getAttribute('data-href');
                if (!href) return;
                if (window.Orbita && typeof window.Orbita.navigateTo === 'function') {
                    window.Orbita.navigateTo(href, true);
                } else {
                    window.location.href = href;
                }
            });
        });
    }

    function initStatisticsMultiSelects() {
        // Shell-level delegation in filters.js binds pickers as soon as markup is swapped.
        // Keep this call so a full reload still initializes even if navigation.js has not run yet.
        if (window.OrbitaRuntime && typeof window.OrbitaRuntime.initStatisticsMultiSelects === 'function') {
            window.OrbitaRuntime.initStatisticsMultiSelects();
        }
    }

    function applyCharts(payload) {
        if (!payload) return;

        var nextFingerprint = stableJson(payload);
        if (nextFingerprint === chartsFingerprint && chartRegistry.trend && chartRegistry.donut) {
            return;
        }

        chartsFingerprint = nextFingerprint;
        var chartsEl = document.getElementById('statistics-charts-data');
        if (chartsEl) chartsEl.textContent = JSON.stringify(payload);
        updateAccountStats(payload.accountStatus);
        initTrendChart(payload);
        initDonutChart(payload);
    }

    function applySnapshot(snapshot) {
        if (!snapshot) return;

        if (shared) {
            shared.updateKpiCards(snapshot.kpiCards, true);
            shared.updateUpdatedClock(snapshot.updatedAtUtc);
        }

        var root = getLiveRoot();
        var showOfficeColumn = root && root.getAttribute('data-show-office-column') === 'true';

        if (snapshot.balanceRows && snapshot.balanceRows.length > 0) {
            renderBalanceRows(snapshot.balanceRows, showOfficeColumn);
        }

        renderWorkers(snapshot.workers || [], showOfficeColumn);
        updateSummaryMeta(snapshot.summary);
        renderDeliveries(snapshot.bitrixDeliveries || [], snapshot.crmDeliveries || []);

        if (snapshot.hrInsights) {
            renderHrTable('Города', snapshot.hrInsights.topCities);
            renderHrTable('Вакансии', snapshot.hrInsights.topVacancies);
            renderHrTable('Аккаунты', snapshot.hrInsights.topAccounts);
            renderAgeBuckets(snapshot.hrInsights.ageBuckets);

            var avgAge = document.querySelector('[data-statistics-avg-age]');
            if (avgAge) avgAge.textContent = snapshot.hrInsights.averageAgeText || 'н/д';
            var messenger = document.querySelector('[data-statistics-messenger]');
            if (messenger) messenger.textContent = snapshot.hrInsights.messengerCoverageText || '0%';
        }

        if (snapshot.charts) {
            applyCharts(snapshot.charts);
        }
    }

    var snapshotFetcher = shared && shared.createSnapshotFetcher
        ? shared.createSnapshotFetcher('statistics', applySnapshot, { errorName: 'Statistics' })
        : null;

    function fetchSnapshot() {
        return snapshotFetcher ? snapshotFetcher.fetchSnapshot() : Promise.resolve();
    }

    function initLiveRefresh() {
        if (shared && shared.registerLivePage) {
            shared.registerLivePage('statistics', snapshotFetcher);
            return;
        }
        if (!getLiveRoot()) return;
        if (window.OrbitaLive && typeof window.OrbitaLive.register === 'function') {
            window.OrbitaLive.register('statistics', { fetchSnapshot: fetchSnapshot });
        }
    }

    function ensureChartRegistry() {
        if (typeof Chart === 'undefined' || typeof Chart.getChart !== 'function') return;

        var trendCanvas = document.getElementById('chart-statistics-trend');
        if (chartRegistry.trend && (!trendCanvas || Chart.getChart(trendCanvas) !== chartRegistry.trend)) {
            destroyChart(chartRegistry.trend);
            chartRegistry.trend = null;
        }

        var donutCanvas = document.getElementById('chart-statistics-account-status');
        if (chartRegistry.donut && (!donutCanvas || Chart.getChart(donutCanvas) !== chartRegistry.donut)) {
            destroyChart(chartRegistry.donut);
            chartRegistry.donut = null;
        }
    }

    function initStatisticsAll() {
        if (!getLiveRoot()) return;

        initKpiCounters();
        initRowNavigation();
        initStatisticsMultiSelects();
        initMonitoringAccountRows();
        initMonitoringSearch();
        initLiveRefresh();

        if (typeof Chart === 'undefined') return;

        ensureChartRegistry();

        var payload = readChartsPayload();
        if (!payload) return;

        applyCharts(payload);
    }

    var statisticsInitPending = false;

    function scheduleStatisticsInit() {
        if (!getLiveRoot()) return;
        // Filters must be ready as soon as the navigation swap completes.
        // Chart initialization can remain deferred, but a user can click a filter immediately.
        initStatisticsMultiSelects();
        if (statisticsInitPending) return;
        statisticsInitPending = true;
        requestAnimationFrame(function () {
            requestAnimationFrame(function () {
                statisticsInitPending = false;
                if (!getLiveRoot()) return;
                initStatisticsAll();
            });
        });
    }

    if (getLiveRoot()) {
        scheduleStatisticsInit();
    }

    if (!window.__orbitaStatisticsContentListener) {
        document.addEventListener('orbita:content-updated', function () {
            if (getLiveRoot()) {
                scheduleStatisticsInit();
            }
        });
        window.__orbitaStatisticsContentListener = true;
    }

    window.OrbitaStatistics = {
        destroyAllCharts: destroyAllCharts,
        destroyCharts: destroyAllCharts,
        applySnapshot: applySnapshot,
        reinit: scheduleStatisticsInit
    };
})();
