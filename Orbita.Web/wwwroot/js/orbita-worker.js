(function () {
    function initKpiCounters() {
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

        document.querySelectorAll('.worker-detail-kpi-row [data-kpi-count]').forEach(function (el, index) {
            var target = parseFloat(el.getAttribute('data-kpi-count'));
            var suffix = el.getAttribute('data-kpi-suffix') || '';
            if (isNaN(target)) return;

            if (reduced) {
                el.textContent = Math.round(target) + suffix;
                return;
            }

            var duration = 720;
            var delay = 80 + index * 70;
            var startAt = 0;

            function easeOutCubic(t) {
                return 1 - Math.pow(1 - t, 3);
            }

            function frame(now) {
                if (!startAt) startAt = now;
                var elapsed = now - startAt;
                if (elapsed < delay) {
                    requestAnimationFrame(frame);
                    return;
                }

                var t = Math.min(1, (elapsed - delay) / duration);
                el.textContent = Math.round(target * easeOutCubic(t)) + suffix;
                if (t < 1) requestAnimationFrame(frame);
            }

            requestAnimationFrame(frame);
        });
    }

    function initAccountRowNavigation() {
        document.querySelectorAll('.worker-account-row[data-href]').forEach(function (row) {
            row.addEventListener('click', function (e) {
                if (e.target.closest('[data-row-menu]') || e.target.closest('a')) return;
                var href = row.getAttribute('data-href');
                if (href) {
                    if (window.Orbita && typeof window.Orbita.navigateTo === 'function') {
                        window.Orbita.navigateTo(href, true);
                    } else {
                        window.location.href = href;
                    }
                }
            });
        });
    }

    function dashboardTooltipOptions(metricLabel) {
        return {
            enabled: true,
            backgroundColor: '#ffffff',
            titleColor: '#101828',
            bodyColor: '#667085',
            borderColor: '#eef2f7',
            borderWidth: 1,
            padding: { top: 10, right: 14, bottom: 10, left: 14 },
            cornerRadius: 12,
            displayColors: false,
            titleFont: { size: 13, weight: '600' },
            bodyFont: { size: 13, weight: '400' },
            caretSize: 6,
            caretPadding: 10,
            callbacks: {
                title: function (items) {
                    return items.length ? String(items[0].label) : '';
                },
                label: function (ctx) {
                    return (metricLabel || 'Значение') + ': ' + ctx.parsed.y;
                }
            }
        };
    }

    function destroyChartOnCanvas(canvas) {
        if (typeof Chart === 'undefined' || typeof Chart.getChart !== 'function') return;
        if (canvas) {
            var existing = Chart.getChart(canvas);
            if (existing) {
                try { existing.destroy(); } catch (e) { }
            }
            if (canvas.id) {
                var byId = Chart.getChart(canvas.id);
                if (byId) {
                    try { byId.destroy(); } catch (e) { }
                }
            }
        }
    }

    function destroyActivityChart() {
        destroyChartOnCanvas(document.getElementById('chart-worker-activity'));
        if (activityChart) {
            try { activityChart.destroy(); } catch (e) { }
            activityChart = null;
        }
    }

    function initActivityChart() {
        if (typeof Chart === 'undefined') return;

        var dataEl = document.getElementById('worker-charts-data');
        var canvas = document.getElementById('chart-worker-activity');
        if (!dataEl || !canvas) return;

        destroyActivityChart();

        var chartData;
        try {
            chartData = JSON.parse(dataEl.textContent || '{}');
        } catch (e) {
            console.error('Worker chart: invalid JSON', e);
            return;
        }

        if (!chartData.values || chartData.values.length < 2) return;

        if (window.OrbitaTime && window.OrbitaTime.localizeHourlyChart) {
            chartData = window.OrbitaTime.localizeHourlyChart(chartData);
        }

        Chart.defaults.font.family = '"Segoe UI", system-ui, -apple-system, sans-serif';
        Chart.defaults.font.size = 11;
        Chart.defaults.color = '#94a3b8';

        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        var labels = chartData.labels || [];
        var values = chartData.values || [];
        var maxValue = values.reduce(function (max, value) {
            return Math.max(max, value);
        }, 0);
        if (maxValue <= 0) return;

        var yMax = Math.max(10, Math.ceil(maxValue / 10) * 10);
        var lineColor = '#2563eb';

        activityChart = new Chart(canvas, {
            type: 'line',
            data: {
                labels: labels,
                datasets: [{
                    label: 'Отклики',
                    data: values,
                    borderColor: lineColor,
                    backgroundColor: 'transparent',
                    fill: false,
                    tension: 0,
                    borderWidth: 2,
                    borderCapStyle: 'round',
                    borderJoinStyle: 'round',
                    pointRadius: 3,
                    pointBackgroundColor: lineColor,
                    pointBorderColor: '#ffffff',
                    pointBorderWidth: 0,
                    pointHoverRadius: 5,
                    pointHoverBackgroundColor: lineColor,
                    pointHoverBorderColor: '#ffffff',
                    pointHoverBorderWidth: 2,
                    pointHitRadius: 10
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                interaction: { mode: 'index', intersect: false },
                animation: reduced ? false : {
                    duration: 800,
                    easing: 'easeOutQuart'
                },
                plugins: {
                    legend: { display: false },
                    tooltip: dashboardTooltipOptions('Откликов')
                },
                scales: {
                    x: {
                        grid: { display: false },
                        border: { display: false, color: '#f2f4f7' },
                        ticks: {
                            color: '#667085',
                            font: { size: 12, weight: '400' },
                            maxRotation: 0,
                            autoSkip: false,
                            callback: function (_value, index) {
                                var label = labels[index];
                                if (!label) return '';
                                var hour = parseInt(label.split(':')[0], 10);
                                return hour % 4 === 0 ? label : '';
                            }
                        }
                    },
                    y: {
                        min: 0,
                        max: yMax,
                        grid: { color: '#f2f4f7', lineWidth: 1 },
                        border: { display: false },
                        ticks: {
                            stepSize: 20,
                            color: '#98a2b3',
                            font: { size: 12, weight: '400' },
                            padding: 8
                        }
                    }
                },
                layout: { padding: { top: 8, right: 8, bottom: 0, left: 0 } }
            }
        });
    }

    function initParallelismSlider() {
        var slider = document.getElementById('maxConcurrentAccounts');
        var output = document.getElementById('maxConcurrentAccountsOut');
        if (!slider || !output) return;
        slider.addEventListener('input', function () {
            output.textContent = slider.value;
        });
    }

    function initCopyButtons() {
        document.querySelectorAll('[data-worker-copy]').forEach(function (button) {
            button.addEventListener('click', async function () {
                var targetId = button.getAttribute('data-copy-target');
                var copyText = button.getAttribute('data-copy-text');
                var text = copyText || '';
                if (!text && targetId) {
                    var target = document.getElementById(targetId);
                    text = target ? (target.value || target.textContent || '').trim() : '';
                }
                if (!text) return;

                if (window.Orbita && window.Orbita.copyText) {
                    window.Orbita.copyText(text);
                }
            });
        });
    }

    var shared = window.OrbitaLiveShared;
    var liveState = null;
    var activityChart = null;

    function updateOnlineStatus(snapshot) {
        document.querySelectorAll('[data-worker-online-badge]').forEach(function (el) {
            var online = !!snapshot.isOnline;
            el.classList.toggle('status-dot', true);
            el.classList.toggle('offline', !online);
            el.innerHTML = '<i class="fa-solid fa-circle status-dot-icon" aria-hidden="true"></i>' +
                (online ? 'Онлайн' : 'Оффлайн');
        });
    }

    function meterTone(percent) {
        if (typeof percent !== 'number') return 'empty';
        if (percent >= 85) return 'critical';
        if (percent >= 60) return 'warn';
        return 'good';
    }

    function meterWidth(percent) {
        if (typeof percent !== 'number') return 0;
        return Math.max(0, Math.min(100, percent));
    }

    function formatCpu(cpu) {
        return typeof cpu === 'number' ? cpu.toFixed(1) + '%' : '—';
    }

    function formatRamPercent(snapshot) {
        return typeof snapshot.ramPercent === 'number' ? snapshot.ramPercent.toFixed(1) + '%' : '—';
    }

    function formatRamDetail(snapshot) {
        if (typeof snapshot.ramUsedMb !== 'number' || typeof snapshot.ramTotalMb !== 'number') return '';
        return snapshot.ramUsedMb.toLocaleString('ru-RU') + ' / ' + snapshot.ramTotalMb.toLocaleString('ru-RU') + ' MB';
    }

    function updateMeterBar(barEl, percent) {
        if (!barEl) return;
        barEl.style.width = meterWidth(percent) + '%';
        barEl.classList.remove(
            'worker-system-meter-fill--good',
            'worker-system-meter-fill--warn',
            'worker-system-meter-fill--critical',
            'worker-system-meter-fill--empty'
        );
        barEl.classList.add('worker-system-meter-fill--' + meterTone(percent));
    }

    function updateSystemMetrics(snapshot) {
        var cpuEl = document.querySelector('[data-worker-live="cpu"]');
        var ramEl = document.querySelector('[data-worker-live="ram"]');
        var ramDetailEl = document.querySelector('[data-worker-live="ram-detail"]');
        if (cpuEl) cpuEl.textContent = formatCpu(snapshot.cpuPercent);
        if (ramEl) ramEl.textContent = formatRamPercent(snapshot);
        updateMeterBar(document.querySelector('[data-worker-live="cpu-bar"]'), snapshot.cpuPercent);
        updateMeterBar(document.querySelector('[data-worker-live="ram-bar"]'), snapshot.ramPercent);

        if (ramDetailEl) {
            var ramDetail = formatRamDetail(snapshot);
            if (ramDetail) {
                ramDetailEl.textContent = ramDetail;
                ramDetailEl.hidden = false;
            } else {
                ramDetailEl.textContent = '';
                ramDetailEl.hidden = true;
            }
        }

        var hintEl = document.querySelector('.worker-system-hint');
        if (hintEl) {
            var hasMetrics = typeof snapshot.cpuPercent === 'number' || typeof snapshot.ramPercent === 'number';
            hintEl.hidden = hasMetrics;
        }
    }

    function updateLastActivity(isoUtc) {
        document.querySelectorAll('[data-worker-last-activity]').forEach(function (el) {
            if (!isoUtc) return;
            el.setAttribute('data-orbita-utc', isoUtc);
            if (window.OrbitaTime) {
                window.OrbitaTime.localizeElement(el);
            }
        });
    }

    function renderInfoValue(item) {
        if (item.timeValue && item.timeValue.utc) {
            return '<time data-orbita-utc="' + shared.escapeHtml(item.timeValue.utc) +
                '" data-orbita-format="' + shared.escapeHtml(item.timeValue.format || 'time') + '"></time>';
        }
        return shared.escapeHtml(item.value || '—');
    }

    function updateInfoItems(items) {
        (items || []).forEach(function (item) {
            var row = document.querySelector('[data-worker-info-label="' + item.label + '"] .worker-info-value');
            if (!row) return;
            row.innerHTML = renderInfoValue(item);
        });
        if (window.OrbitaTime) {
            var list = document.querySelector('[data-worker-info-list]');
            if (list) window.OrbitaTime.localizeAll(list);
        }
    }

    function updatePeriodStats(stats) {
        (stats || []).forEach(function (stat) {
            var row = document.querySelector('[data-worker-stat-label="' + stat.label + '"] .worker-stats-value');
            if (row) row.textContent = stat.value || '—';
        });
    }

    function eventIcon(level) {
        if (level === 'error') return 'fa-regular fa-circle-xmark';
        if (level === 'warning') return 'fa-solid fa-triangle-exclamation';
        return 'fa-regular fa-circle-check';
    }

    function renderWorkerEvents(events) {
        var container = document.querySelector('[data-worker-events]');
        if (!container || !shared) return;

        if (!events || !events.length) {
            container.innerHTML = '<p class="dash-event-empty">Событий пока нет.</p>';
            return;
        }

        container.innerHTML = '<div class="dash-event-list">' + events.map(function (evt) {
            var subtitle = evt.subtitle
                ? '<div class="dash-event-subtitle" title="' + shared.escapeHtml(evt.subtitle) + '">' + shared.escapeHtml(evt.subtitle) + '</div>'
                : '';
            var iso = evt.timeUtc || '';
            var workerUrl = shared.urlFromTemplate(shared.getLiveAttr('data-worker-details-url'), '__id__', evt.workerId);
            var accountUrl = evt.accountName
                ? shared.urlFromTemplate(shared.getLiveAttr('data-account-search-url'), '__q__', evt.accountName)
                : '';
            var logUrl = shared.urlFromTemplate(shared.getLiveAttr('data-settings-logs-url'), '__id__', evt.workerId);
            var attachmentUrl = evt.attachmentId ? '/Diagnostics/Image/' + evt.attachmentId : '';

            return '<div class="dash-event-row dash-event-row--detail" role="button" tabindex="0"' +
                ' data-event-id="' + shared.escapeHtml(evt.id || '') + '"' +
                ' data-copy="' + shared.escapeHtml(evt.copyText || '') + '"' +
                ' data-detail-title="' + shared.escapeHtml(evt.detailTitle || 'Детали') + '"' +
                ' data-detail-subtitle="' + shared.escapeHtml(evt.detailSubtitle || '') + '"' +
                ' data-detail-body="' + shared.escapeHtml(evt.detailBody || '') + '"' +
                ' data-detail-attachment="' + shared.escapeHtml(attachmentUrl) + '"' +
                ' data-detail-log-url="' + shared.escapeHtml(logUrl) + '"' +
                ' data-detail-worker-url="' + shared.escapeHtml(workerUrl) + '"' +
                ' data-detail-account-url="' + shared.escapeHtml(accountUrl) + '"' +
                ' data-is-error="' + (evt.isError ? 'true' : 'false') + '">' +
                '<div class="dash-event-icon dash-event-icon--' + shared.escapeHtml(evt.level || 'success') + '">' +
                '<i class="' + eventIcon(evt.level) + '" aria-hidden="true"></i></div>' +
                '<div class="dash-event-body"><div class="dash-event-title" title="' + shared.escapeHtml(evt.message || '') + '">' + shared.escapeHtml(evt.message || '') + '</div>' + subtitle + '</div>' +
                '<div class="dash-event-side"><div class="dash-event-time">' +
                '<time data-orbita-utc="' + shared.escapeHtml(iso) + '" data-orbita-format="time-short"></time></div></div></div>';
        }).join('') + '</div>';

        if (window.OrbitaTime) {
            window.OrbitaTime.localizeAll(container);
        }
    }

    function getWorkerId() {
        return shared.getLiveAttr('data-worker-id');
    }

    function accountSearchUrl(name) {
        return shared.urlFromTemplate(shared.getLiveAttr('data-account-search-url'), '__q__', name);
    }

    function responsesFilterUrl(accountId) {
        return shared.urlFromTemplate(shared.getLiveAttr('data-responses-filter-url'), '__id__', accountId);
    }

    function renderWorkerAccountMenu(account) {
        var items = '<a class="row-menu-item" href="' + shared.escapeHtml(accountSearchUrl(account.displayName)) + '">' +
            '<i class="fa-regular fa-eye" aria-hidden="true"></i>Просмотр</a>' +
            '<a class="row-menu-item" href="#worker-accounts">' +
            '<i class="fa-regular fa-pen-to-square" aria-hidden="true"></i>Настройки на воркере</a>' +
            '<a class="row-menu-item" href="' + shared.escapeHtml(responsesFilterUrl(account.id)) + '">' +
            '<i class="fa-regular fa-clock" aria-hidden="true"></i>История откликов</a>';
        return shared.rowMenuShell('', items);
    }

    function insertWorkerSubProfileRowsAfter(accountRow, account, panelId, wasExpanded) {
        if (!account.hasSubProfiles || !shared.accountSubProfilesRenderable(account)) {
            shared.removeSubProfileTableRows(panelId);
            return;
        }
        shared.removeSubProfileTableRows(panelId);
        var subRowsHtml = shared.renderSubProfileTableRows(
            getWorkerId(),
            account,
            panelId,
            'worker',
            account.isProcessingNow ? account.processingSubProfileId : null,
            false,
            wasExpanded);
        if (!subRowsHtml) return;
        var temp = document.createElement('tbody');
        temp.innerHTML = subRowsHtml;
        var insertAfter = accountRow;
        while (temp.firstChild) {
            insertAfter.insertAdjacentElement('afterend', temp.firstChild);
            insertAfter = insertAfter.nextElementSibling;
        }
    }

    function renderWorkerAccounts(accounts) {
        var tbody = document.querySelector('[data-orbita-live-body="worker-accounts"]');
        if (!tbody || !shared) return;
        var workerId = getWorkerId();
        var expandedPanels = shared.captureExpandedSubprofilePanels(tbody);
        var nextIds = {};

        (accounts || []).forEach(function (account) {
            nextIds[account.id] = true;
            var panelId = 'subprofiles-' + account.id;
            var existingRow = tbody.querySelector('tr.worker-account-row[data-account-id="' + account.id + '"]');
            var statusHtml = '<span class="account-status account-status--' + shared.escapeHtml(account.statusTone || 'success') + '">' +
                '<i class="fa-solid fa-circle account-status-dot" aria-hidden="true"></i>' +
                shared.escapeHtml(account.statusLabel || '') + '</span>';
            if (account.lastErrorMessage) {
                statusHtml += '<span class="account-error-hint" title="' + shared.escapeHtml(account.lastErrorMessage) + '">' +
                    shared.escapeHtml(account.lastErrorMessage) + '</span>';
            }
            var activityHtml = account.lastActivityUtc
                ? '<time data-orbita-utc="' + shared.escapeHtml(account.lastActivityUtc) + '" data-orbita-format="activity"></time>'
                : '—';
            var adsPower = account.adsPowerProfileId
                ? '<span class="worker-account-sub">AdsPower ' + shared.escapeHtml(account.adsPowerProfileId) + '</span>'
                : '';
            var subProfiles = shared.renderSubProfilesToolbar(workerId, account, 'subprofiles');
            var checked = account.isEnabledInPanel ? ' checked' : '';

            var rowClass = 'worker-account-row' + (account.isProcessingNow ? ' worker-account-row--processing' : '');

            var toggleTitle = account.isEnabledInPanel ? 'Отключить аккаунт в панели' : 'Включить аккаунт в панели';
            var subProfilesJson = shared.escapeHtml(JSON.stringify(account.subProfiles || []));
            var processingSubProfileId = account.isProcessingNow && account.processingSubProfileId
                ? shared.escapeHtml(account.processingSubProfileId)
                : '';

            var rowHtml = '<tr class="' + rowClass + '" data-account-id="' + shared.escapeHtml(account.id) + '"' +
                ' data-subprofiles-layout="worker"' +
                (processingSubProfileId ? ' data-processing-subprofile-id="' + processingSubProfileId + '"' : '') +
                ' data-subprofiles-json="' + subProfilesJson + '">' +
                '<td class="cell-toggle" data-label="Вкл"><label class="worker-toggle" title="' + shared.escapeHtml(toggleTitle) + '">' +
                '<input type="checkbox" data-account-enable-toggle data-worker-id="' + shared.escapeHtml(workerId) + '" data-account-id="' + shared.escapeHtml(account.id) + '"' + checked + ' />' +
                '<span class="worker-toggle-slider"></span></label></td>' +
                '<td class="cell-name" data-label="Аккаунт"><a href="' + shared.escapeHtml(accountSearchUrl(account.displayName)) + '">' + shared.escapeHtml(account.displayName) + '</a>' + adsPower + subProfiles + '</td>' +
                '<td data-label="Статус">' + statusHtml + '</td>' +
                '<td class="cell-num cell-balance" data-label="Баланс"><span class="account-balance-multiline">' + shared.escapeHtml(account.balanceText || '—') + '</span></td>' +
                '<td class="cell-num" data-label="Откликов">' + (account.responses || 0) + '</td>' +
                '<td data-label="Последняя активность">' + activityHtml + '</td>' +
                '<td class="cell-num" data-label="Ошибок">' + (account.errors || 0) + '</td>' +
                '<td class="data-table-menu" data-label="">' + renderWorkerAccountMenu(account) + '</td></tr>';

            var temp = document.createElement('tbody');
            temp.innerHTML = rowHtml;
            var newRow = temp.firstElementChild;
            if (!newRow) return;

            var wasExpanded = !!expandedPanels[panelId];
            if (existingRow) {
                shared.removeSubProfileTableRows(panelId);
                existingRow.replaceWith(newRow);
            } else {
                tbody.appendChild(newRow);
            }

            if (account.hasSubProfiles) {
                insertWorkerSubProfileRowsAfter(newRow, account, panelId, wasExpanded);
            }

            if (wasExpanded) {
                var btn = newRow.querySelector('[aria-controls="' + panelId + '"]');
                if (btn) {
                    btn.setAttribute('aria-expanded', 'true');
                    var icon = btn.querySelector('.subprofiles-toggle-icon');
                    if (icon) icon.classList.add('subprofiles-toggle-icon--open');
                }
            }
        });

        tbody.querySelectorAll('tr.worker-account-row[data-account-id]').forEach(function (row) {
            var accountId = row.getAttribute('data-account-id');
            if (!accountId || nextIds[accountId]) return;
            var panelId = 'subprofiles-' + accountId;
            shared.removeSubProfileTableRows(panelId);
            row.remove();
        });

        if (window.OrbitaTime) {
            window.OrbitaTime.localizeAll(tbody);
        }
        shared.reinitLiveContent();
        if (window.Orbita && typeof window.Orbita.initWorkerAccountEnableToggles === 'function') {
            window.Orbita.initWorkerAccountEnableToggles();
        }
        initAccountRowNavigation();
    }

    function renderActivityBlock(activity, activeAccountActivities) {
        var items = (activeAccountActivities || []).filter(function (item) {
            return item && item.label;
        });
        if (items.length > 1) {
            return '<div class="worker-detail-activity-list">' + items.map(function (item) {
                return shared.renderActivityPill(item.label, item.tone, item.isLive);
            }).join('') + '</div>';
        }

        var single = items.length === 1 ? items[0] : activity;
        if (!single || !single.label) {
            return '<span class="worker-activity-pill worker-activity-pill--muted">—</span>';
        }
        return shared.renderActivityPill(single.label, single.tone, single.isLive);
    }

    function updateCurrentActivity(activity, activeAccountActivities) {
        var host = document.querySelector('[data-worker-current-activity]');
        if (!host || !shared) return;
        var labelEl = host.querySelector('.worker-detail-activity-label');
        var contentHtml = renderActivityBlock(activity, activeAccountActivities);
        var existingList = host.querySelector('.worker-detail-activity-list');
        var existingPill = host.querySelector('.worker-activity-pill');

        if (existingList) {
            existingList.outerHTML = contentHtml;
        } else if (existingPill) {
            existingPill.outerHTML = contentHtml;
        } else if (labelEl) {
            labelEl.insertAdjacentHTML('afterend', contentHtml);
        } else {
            host.insertAdjacentHTML('beforeend', contentHtml);
        }
    }

    function applySnapshot(snapshot, highlightChanged) {
        if (!snapshot || !shared) return;
        shared.updateKpiCards(snapshot.kpiCards || [], highlightChanged);
        updateCurrentActivity(snapshot.currentActivity, snapshot.activeAccountActivities);
        updateOnlineStatus(snapshot);
        updateSystemMetrics(snapshot);
        updateLastActivity(snapshot.lastActivityUtc);
        updateInfoItems(snapshot.infoItems);
        updatePeriodStats(snapshot.periodStats);

        if (liveState && shared.stableJson(liveState.events) !== shared.stableJson(snapshot.events)) {
            renderWorkerEvents(snapshot.events);
            if (highlightChanged) shared.highlightCard(document.querySelector('.card--worker-events'));
        } else if (!liveState) {
            renderWorkerEvents(snapshot.events);
        }

        if (liveState && shared.stableJson(liveState.accounts) !== shared.stableJson(snapshot.accounts)) {
            renderWorkerAccounts(snapshot.accounts);
            if (highlightChanged) shared.highlightCard(document.querySelector('.card--worker-accounts'));
        } else if (!liveState) {
            renderWorkerAccounts(snapshot.accounts);
        }

        if (activityChart && snapshot.activityChart && snapshot.activityChart.values) {
            var chartData = snapshot.activityChart;
            if (window.OrbitaTime && window.OrbitaTime.localizeHourlyChart) {
                chartData = window.OrbitaTime.localizeHourlyChart(chartData);
            }
            activityChart.data.labels = chartData.labels || [];
            activityChart.data.datasets[0].data = chartData.values || [];
            activityChart.update('none');
        }

        liveState = snapshot;
        shared.updateUpdatedClock(snapshot.updatedAtUtc);
    }

    function fetchSnapshot() {
        var root = shared && shared.getLiveRoot();
        if (!root) return Promise.resolve();
        var url = root.getAttribute('data-orbita-snapshot');
        if (!url) return Promise.resolve();
        return fetch(url, { credentials: 'same-origin', headers: { Accept: 'application/json' } })
            .then(function (res) {
                if (!res.ok) throw new Error('Worker details snapshot failed: ' + res.status);
                return res.json();
            })
            .then(function (snapshot) { applySnapshot(snapshot, true); });
    }

    function initWorkerPage() {
        initKpiCounters();
        if (window.Orbita && typeof window.Orbita.initWorkerRestartButtons === 'function') {
            window.Orbita.initWorkerRestartButtons();
        }
        if (window.Orbita && typeof window.Orbita.initWorkerAccountEnableToggles === 'function') {
            window.Orbita.initWorkerAccountEnableToggles();
        }
        initAccountRowNavigation();
        initActivityChart();
        initParallelismSlider();
        initCopyButtons();
        if (window.OrbitaLive && shared && shared.getLiveRoot()) {
            window.OrbitaLive.register('worker', { fetchSnapshot: fetchSnapshot });
        }
    }

    window.OrbitaWorker = window.OrbitaWorker || {};
    window.OrbitaWorker.destroyCharts = destroyActivityChart;

    initWorkerPage();
    document.addEventListener('orbita:content-updated', initWorkerPage);
})();