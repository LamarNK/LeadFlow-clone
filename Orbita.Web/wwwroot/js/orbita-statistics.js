(function () {
    function getLiveRoot() {
        return document.querySelector('[data-orbita-live-page="statistics"]');
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
        destroyChartOnCanvas(document.getElementById('chart-statistics-trend'));
        destroyChartOnCanvas(document.getElementById('chart-statistics-account-status'));
    }

    function initTrendChart(payload) {
        var canvas = document.getElementById('chart-statistics-trend');
        if (!canvas || typeof Chart === 'undefined' || !payload || !payload.dailyTrend) return;

        destroyChart(chartRegistry.trend);

        var trend = payload.dailyTrend;
        chartRegistry.trend = new Chart(canvas, {
            type: 'bar',
            data: {
                labels: trend.labels || [],
                datasets: [
                    { label: 'CRM', data: trend.sent || [], backgroundColor: '#22c55e', stack: 'stack' },
                    { label: 'В работе', data: trend.inProgress || [], backgroundColor: '#3b82f6', stack: 'stack' },
                    { label: 'Требует действия', data: trend.actionRequired || [], backgroundColor: '#a855f7', stack: 'stack' },
                    { label: 'Дубли', data: trend.duplicates || [], backgroundColor: '#94a3b8', stack: 'stack' },
                    { label: 'Ошибки', data: trend.errors || [], backgroundColor: '#f59e0b', stack: 'stack' }
                ]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: {
                        position: 'bottom',
                        labels: { boxWidth: 10, padding: 12 }
                    },
                    tooltip: {
                        mode: 'index',
                        intersect: false
                    }
                },
                scales: {
                    x: {
                        stacked: true,
                        grid: { display: false }
                    },
                    y: {
                        stacked: true,
                        beginAtZero: true,
                        ticks: { precision: 0 }
                    }
                }
            }
        });
    }

    function initDonutChart(payload) {
        var canvas = document.getElementById('chart-statistics-account-status');
        if (!canvas || typeof Chart === 'undefined' || !payload || !payload.accountStatus) return;

        destroyChart(chartRegistry.donut);

        var stats = payload.accountStatus;
        var values = [stats.active, stats.inactive, stats.blocked, stats.errors];
        if (values.every(function (v) { return !v; })) {
            values = [1];
        }

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
            options: {
                responsive: true,
                maintainAspectRatio: false,
                cutout: '68%',
                plugins: { legend: { display: false } }
            }
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
        return String(text)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function renderBalanceRows(rows, showOfficeColumn) {
        var container = document.querySelector('[data-statistics-balances] .statistics-balance-list');
        if (!container) return;

        if (!rows || rows.length === 0) return;

        container.innerHTML = rows.map(function (row) {
            var office = showOfficeColumn && row.officeName
                ? '<span>· ' + escapeHtml(row.officeName) + '</span>'
                : '';
            var wallet = row.wallet > 0
                ? '<span class="statistics-balance-wallet" title="Кошелёк">' + escapeHtml(row.walletText) + '</span>'
                : '';
            var foot = '';
            if (row.balanceBreakdown || row.balanceSubtitle) {
                foot = '<div class="statistics-balance-foot">'
                    + (row.balanceBreakdown ? '<span class="statistics-balance-breakdown">' + escapeHtml(row.balanceBreakdown) + '</span>' : '')
                    + (row.balanceSubtitle ? '<span class="statistics-balance-subtitle">' + escapeHtml(row.balanceSubtitle) + '</span>' : '')
                    + '</div>';
            }

            return '<div class="statistics-balance-row' + (row.isLowBalance ? ' statistics-balance-row--low' : '') + '" data-balance-account-id="' + row.accountId + '">'
                + '<div class="statistics-balance-head">'
                + '<div class="statistics-balance-title">'
                + '<span class="statistics-balance-name">' + escapeHtml(row.accountName) + '</span>'
                + '<span class="statistics-balance-meta">' + escapeHtml(row.workerName) + office + '</span>'
                + '</div>'
                + '<div class="statistics-balance-amounts">'
                + '<span class="statistics-balance-advance" title="Аванс">' + escapeHtml(row.advanceText) + '</span>'
                + wallet
                + '</div>'
                + '</div>'
                + '<div class="statistics-balance-bar-track" aria-hidden="true">'
                + '<span class="statistics-balance-bar-fill" style="width:' + (row.barWidth * 100).toFixed(2) + '%"></span>'
                + '</div>'
                + foot
                + '</div>';
        }).join('');
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

        tbody.innerHTML = rows.map(function (worker) {
            var officeCell = showOfficeColumn
                ? '<td data-label="Офис">' + escapeHtml(worker.officeName || '') + '</td>'
                : '';
            var statusClass = worker.isOnline ? 'online' : 'offline';
            var statusLabel = worker.isOnline ? 'Онлайн' : 'Офлайн';
            return '<tr data-worker-id="' + worker.id + '">'
                + '<td data-label="Воркер">' + escapeHtml(worker.displayName) + '</td>'
                + officeCell
                + '<td data-label="Статус"><span class="status-pill status-pill--' + statusClass + '">' + statusLabel + '</span></td>'
                + '<td class="cell-num" data-label="Отклики">' + worker.periodResponses + '</td>'
                + '<td class="cell-num" data-label="Дубли">' + worker.periodDuplicates + '</td>'
                + '<td class="cell-num" data-label="Ошибки">' + worker.periodErrors + '</td>'
                + '<td class="cell-num" data-label="Аккаунты">' + worker.activeAccounts + ' / ' + worker.totalAccounts + '</td>'
                + '</tr>';
        }).join('');
    }

    function applySnapshot(snapshot) {
        if (!snapshot) return;

        if (window.OrbitaLiveShared) {
            window.OrbitaLiveShared.updateKpiCards(snapshot.kpiCards, true);
            window.OrbitaLiveShared.updateUpdatedClock(snapshot.updatedAtUtc);
        }

        var root = getLiveRoot();
        var showOfficeColumn = root && root.getAttribute('data-show-office-column') === 'true';

        renderBalanceRows(snapshot.balanceRows, showOfficeColumn);
        renderWorkers(snapshot.workers || [], showOfficeColumn);

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
            var chartsEl = document.getElementById('statistics-charts-data');
            if (chartsEl) chartsEl.textContent = JSON.stringify(snapshot.charts);
            updateAccountStats(snapshot.accountStats || snapshot.charts.accountStatus);
            initTrendChart(snapshot.charts);
            initDonutChart(snapshot.charts);
        }
    }

    function fetchSnapshot() {
        var root = getLiveRoot();
        if (!root) return Promise.resolve();

        var url = root.getAttribute('data-orbita-snapshot');
        if (!url) return Promise.resolve();

        return fetch(url, { credentials: 'same-origin' })
            .then(function (res) {
                if (!res.ok) throw new Error('Statistics snapshot failed: ' + res.status);
                return res.json();
            })
            .then(applySnapshot);
    }

    function initLiveRefresh() {
        if (!getLiveRoot()) return;
        if (window.OrbitaLive && typeof window.OrbitaLive.register === 'function') {
            window.OrbitaLive.register('statistics', { fetchSnapshot: fetchSnapshot });
        }
    }

    function initStatisticsAll() {
        if (typeof Chart === 'undefined') return;
        if (!getLiveRoot()) return;

        destroyAllCharts();

        var payload = readChartsPayload();
        if (!payload) return;

        updateAccountStats(payload.accountStatus);
        initTrendChart(payload);
        initDonutChart(payload);
        initLiveRefresh();
    }

    var statisticsInitPending = false;

    function scheduleStatisticsInit() {
        if (!getLiveRoot()) return;
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