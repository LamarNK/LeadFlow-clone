(function () {
    if (typeof Chart === 'undefined') return;

    var liveRoot = document.querySelector('[data-dashboard-live]');
    var dataEl = document.getElementById('dashboard-charts-data');
    if (!dataEl) return;

    var payload;
    try {
        payload = JSON.parse(dataEl.textContent || '{}');
    } catch (e) {
        console.error('Dashboard charts: invalid JSON', e);
        return;
    }

    var chartRegistry = {
        sparklines: [],
        hourly: null,
        donut: null
    };

    var liveState = null;
    var pollTimer = null;
    var pollInFlight = false;
    var pollIntervalMs = 10000;
    var highlightMs = 1800;

    Chart.defaults.font.family = '"Segoe UI", system-ui, -apple-system, sans-serif';
    Chart.defaults.font.size = 11;
    Chart.defaults.color = '#94a3b8';

    function dashboardTooltipOptions(metricLabel, valueAtIndex) {
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
                    var value = typeof valueAtIndex === 'function'
                        ? valueAtIndex(ctx.dataIndex, ctx.parsed.y)
                        : ctx.parsed.y;
                    return (metricLabel || 'Значение') + ': ' + value;
                }
            }
        };
    }

    function initKpiCounters() {
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

        document.querySelectorAll('[data-kpi-count]').forEach(function (el, index) {
            var target = parseFloat(el.getAttribute('data-kpi-count'));
            var suffix = el.getAttribute('data-kpi-suffix') || '';
            if (isNaN(target)) return;

            if (reduced) {
                el.textContent = Math.round(target) + suffix;
                return;
            }

            animateKpiValue(el, 0, target, suffix, 820, 120 + index * 90);
        });
    }

    function animateKpiValue(el, from, to, suffix, duration, delay) {
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        if (reduced) {
            el.textContent = Math.round(to) + suffix;
            el.setAttribute('data-kpi-count', String(to));
            return;
        }

        duration = duration || 620;
        delay = delay || 0;
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
            var value = Math.round(from + (to - from) * easeOutCubic(t));
            el.textContent = value + suffix;

            if (t < 1) {
                requestAnimationFrame(frame);
            } else {
                el.setAttribute('data-kpi-count', String(to));
            }
        }

        requestAnimationFrame(frame);
    }

    function initSparklines(sparklines) {
        if (!Array.isArray(sparklines)) return;
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

        document.querySelectorAll('[data-sparkline-index]').forEach(function (canvas) {
            var index = parseInt(canvas.getAttribute('data-sparkline-index'), 10);
            var cfg = sparklines[index];
            if (!cfg || !cfg.values || cfg.values.length < 2) return;

            var chart = createSparklineChart(canvas, cfg, index, reduced);
            chartRegistry.sparklines[index] = chart;
        });
    }

    function createSparklineChart(canvas, cfg, index, reduced) {
        var color = cfg.color || '#2563eb';
        var values = cfg.values;
        var labels = cfg.labels && cfg.labels.length === values.length
            ? cfg.labels
            : values.map(function (_, i) { return String(i + 1); });
        var metricLabel = cfg.metricLabel || 'Значение';
        var tooltipValues = cfg.tooltipValues && cfg.tooltipValues.length === values.length
            ? cfg.tooltipValues
            : null;
        var yBounds = sparklineYBounds(values);

        return new Chart(canvas, {
            type: 'line',
            data: {
                labels: labels,
                datasets: [{
                    data: values,
                    borderColor: color,
                    backgroundColor: 'transparent',
                    fill: false,
                    cubicInterpolationMode: yBounds.isFlat ? false : 'monotone',
                    tension: yBounds.isFlat ? 0 : 0.4,
                    borderWidth: 2,
                    borderCapStyle: 'round',
                    borderJoinStyle: 'round',
                    pointRadius: 1.5,
                    pointBackgroundColor: color,
                    pointBorderWidth: 0,
                    pointHoverRadius: 4,
                    pointHoverBackgroundColor: color,
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
                    duration: 850,
                    easing: 'easeOutQuart',
                    x: {
                        type: 'number',
                        easing: 'easeOutQuart',
                        duration: 850,
                        from: NaN,
                        delay: function (ctx) {
                            return index * 80 + ctx.index * 22;
                        }
                    },
                    y: {
                        type: 'number',
                        easing: 'easeOutQuart',
                        duration: 850,
                        from: function (ctx) {
                            return ctx.chart.scales.y.getPixelForValue(yBounds.yMin);
                        },
                        delay: function (ctx) {
                            return index * 80 + ctx.index * 22;
                        }
                    }
                },
                plugins: {
                    legend: { display: false },
                    tooltip: dashboardTooltipOptions(metricLabel, function (idx, fallback) {
                        return tooltipValues ? tooltipValues[idx] : fallback;
                    })
                },
                scales: {
                    x: { display: false, offset: false },
                    y: { display: false, min: yBounds.yMin, max: yBounds.yMax }
                },
                layout: { padding: { top: 8, bottom: 4, left: 2, right: 2 } }
            }
        });
    }

    function sparklineYBounds(values) {
        var minVal = Math.min.apply(null, values);
        var maxVal = Math.max.apply(null, values);
        var isFlat = minVal === maxVal;
        return {
            isFlat: isFlat,
            yMin: isFlat ? minVal : minVal - 3,
            yMax: isFlat ? minVal + 1 : maxVal + 3
        };
    }

    function initHourlyChart(chartData) {
        var canvas = document.getElementById('chart-hourly-responses');
        if (!canvas || !chartData || !chartData.values || chartData.values.length < 2) return;

        chartRegistry.hourly = createHourlyChart(canvas, chartData);
    }

    function createHourlyChart(canvas, chartData) {
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        var labels = chartData.labels || [];
        var lineColor = '#2563eb';

        return new Chart(canvas, {
            type: 'line',
            data: {
                labels: labels,
                datasets: [{
                    label: 'Отклики',
                    data: chartData.values,
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
                    easing: 'easeOutQuart',
                    x: {
                        type: 'number',
                        easing: 'easeOutQuart',
                        duration: 800,
                        from: NaN,
                        delay: function (ctx) {
                            return ctx.type === 'data' && ctx.mode === 'default' ? ctx.index * 18 : 0;
                        }
                    },
                    y: {
                        type: 'number',
                        easing: 'easeOutQuart',
                        duration: 800,
                        from: function (ctx) {
                            return ctx.chart.scales.y.getPixelForValue(0);
                        },
                        delay: function (ctx) {
                            return ctx.type === 'data' && ctx.mode === 'default' ? ctx.index * 18 : 0;
                        }
                    }
                },
                animations: reduced ? false : {
                    radius: {
                        type: 'number',
                        duration: 350,
                        easing: 'easeOutQuart',
                        from: 0,
                        delay: function (ctx) {
                            return 720 + ctx.index * 12;
                        }
                    }
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
                        max: 100,
                        grid: {
                            color: '#f2f4f7',
                            lineWidth: 1
                        },
                        border: { display: false },
                        ticks: {
                            stepSize: 20,
                            color: '#98a2b3',
                            font: { size: 12, weight: '400' },
                            padding: 8
                        }
                    }
                },
                layout: {
                    padding: { top: 8, right: 8, bottom: 0, left: 0 }
                }
            }
        });
    }

    function donutDataset(chartData) {
        var total = chartData.total || 0;
        var active = chartData.active || 0;
        var rest = Math.max(0, total - active);
        var labels = ['Активны'];
        var values = [active];
        var colors = ['#22c55e'];
        var isPartial = total > 0 && active > 0 && rest > 0;

        if (total === 0) {
            labels = ['Нет данных'];
            values = [1];
            colors = ['#e5e7eb'];
        } else if (rest > 0) {
            labels = ['Активны', 'Остальные'];
            values = [active, rest];
            colors = ['#22c55e', '#e5e7eb'];
        }

        return { total: total, labels: labels, values: values, colors: colors, isPartial: isPartial };
    }

    function initDonutChart(chartData) {
        var canvas = document.getElementById('chart-account-status');
        if (!canvas || !chartData) return;

        chartRegistry.donut = createDonutChart(canvas, chartData);
    }

    function createDonutChart(canvas, chartData) {
        var dataset = donutDataset(chartData);

        return new Chart(canvas, {
            type: 'doughnut',
            data: {
                labels: dataset.labels,
                datasets: [{
                    data: dataset.values,
                    backgroundColor: dataset.colors,
                    borderWidth: 0,
                    borderRadius: function (ctx) {
                        if (!dataset.isPartial || ctx.dataIndex !== 0) return 0;
                        return 13;
                    },
                    borderAlign: 'inner',
                    hoverOffset: 0,
                    spacing: 0
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                cutout: '68%',
                animation: false,
                plugins: {
                    legend: { display: false },
                    tooltip: {
                        enabled: dataset.total > 0,
                        callbacks: {
                            label: function (ctx) {
                                var pct = Math.round(ctx.parsed * 100 / (dataset.total || 1));
                                return ctx.label + ': ' + ctx.parsed + ' (' + pct + '%)';
                            }
                        }
                    }
                },
                elements: {
                    arc: {
                        borderWidth: 0,
                        borderJoinStyle: 'round'
                    }
                }
            }
        });
    }

    function stableJson(value) {
        return JSON.stringify(value);
    }

    function highlightCard(el) {
        if (!el) return;
        el.classList.remove('orbita-live-updated');
        void el.offsetWidth;
        el.classList.add('orbita-live-updated');
        window.setTimeout(function () {
            el.classList.remove('orbita-live-updated');
        }, highlightMs);
    }

    function fmtPct(value, total) {
        return Math.round(value * 100 / Math.max(1, total)) + '%';
    }

    function escapeHtml(text) {
        return String(text)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function workerDetailsUrl(id) {
        var template = liveRoot ? liveRoot.getAttribute('data-worker-details-url') : '';
        return template ? template.replace('__id__', id) : '#';
    }

    function renderWorkers(workers) {
        var tbody = document.querySelector('[data-dashboard-workers-body]');
        if (!tbody) return;

        tbody.innerHTML = workers.map(function (w) {
            var statusClass = w.isOnline ? '' : ' offline';
            var statusText = w.isOnline ? 'Онлайн' : 'Оффлайн';
            var iso = w.lastActivityUtc || '';
            var timeHtml = iso
                ? '<time class="" data-orbita-utc="' + escapeHtml(iso) + '" data-orbita-format="time"></time>'
                : '—';

            return '<tr>' +
                '<td class="cell-name"><a href="' + escapeHtml(workerDetailsUrl(w.id)) + '">' + escapeHtml(w.displayName) + '</a></td>' +
                '<td><span class="status-dot' + statusClass + '"><i class="fa-solid fa-circle status-dot-icon" aria-hidden="true"></i>' + statusText + '</span></td>' +
                '<td>' + w.activeAccounts + ' / ' + w.totalAccounts + '</td>' +
                '<td>' + w.responses + '</td>' +
                '<td>' + w.duplicates + '</td>' +
                '<td>' + w.errors + '</td>' +
                '<td>' + timeHtml + '</td>' +
                '<td class="data-table-menu"><button type="button" class="row-menu-btn" aria-label="Действия"><i class="fa-solid fa-ellipsis-vertical" aria-hidden="true"></i></button></td>' +
                '</tr>';
        }).join('');

        if (window.OrbitaTime) {
            window.OrbitaTime.localizeAll(tbody);
        }
    }

    function eventIcon(level) {
        if (level === 'error') return 'fa-regular fa-circle-xmark';
        if (level === 'warning') return 'fa-solid fa-triangle-exclamation';
        return 'fa-regular fa-circle-check';
    }

    function renderEvents(events) {
        var container = document.querySelector('[data-dashboard-events]');
        if (!container) return;

        if (!events.length) {
            container.innerHTML = '<p class="dash-event-empty">Событий пока нет.</p>';
            return;
        }

        container.innerHTML = '<div class="dash-event-list">' + events.map(function (evt) {
            var subtitle = evt.subtitle
                ? '<div class="dash-event-subtitle">' + escapeHtml(evt.subtitle) + '</div>'
                : '';
            var iso = evt.timeUtc || '';

            return '<div class="dash-event-row">' +
                '<div class="dash-event-icon dash-event-icon--' + escapeHtml(evt.level) + '"><i class="' + eventIcon(evt.level) + '" aria-hidden="true"></i></div>' +
                '<div class="dash-event-body"><div class="dash-event-title">' + escapeHtml(evt.message) + '</div>' + subtitle + '</div>' +
                '<div class="dash-event-side">' +
                '<div class="dash-event-time"><time data-orbita-utc="' + escapeHtml(iso) + '" data-orbita-format="time"></time></div>' +
                '<div class="dash-event-worker">' + escapeHtml(evt.workerName) + '</div>' +
                '</div></div>';
        }).join('') + '</div>';

        if (window.OrbitaTime) {
            window.OrbitaTime.localizeAll(container);
        }
    }

    function updateAccountStats(stats) {
        var widget = document.querySelector('[data-dashboard-account-stats]');
        if (!widget) return;

        var total = Math.max(1, stats.total || 0);
        var setStat = function (key, text) {
            widget.querySelectorAll('[data-account-stat="' + key + '"]').forEach(function (el) {
                el.textContent = text;
            });
        };

        setStat('total', String(stats.total || 0));
        setStat('total-summary', String(stats.total || 0));
        setStat('active', stats.active + ' (' + fmtPct(stats.active, total) + ')');
        setStat('inactive', stats.inactive + ' (' + fmtPct(stats.inactive, total) + ')');
        setStat('blocked', stats.blocked + ' (' + fmtPct(stats.blocked, total) + ')');
        setStat('errors', stats.errors + ' (' + fmtPct(stats.errors, total) + ')');

        if (chartRegistry.donut) {
            var dataset = donutDataset(stats);
            chartRegistry.donut.data.labels = dataset.labels;
            chartRegistry.donut.data.datasets[0].data = dataset.values;
            chartRegistry.donut.data.datasets[0].backgroundColor = dataset.colors;
            chartRegistry.donut.update('none');
        }
    }

    function updateKpiCards(kpiCards, charts, highlightChanged) {
        kpiCards.forEach(function (card, index) {
            var el = document.querySelector('[data-kpi-key="' + card.key + '"]');
            if (!el) return;

            var valueEl = el.querySelector('[data-kpi-count]');
            if (!valueEl) return;

            var suffix = card.valueSuffix || '';
            var prev = parseFloat(valueEl.getAttribute('data-kpi-count'));
            var next = card.countValue;
            if (isNaN(prev)) prev = 0;
            var valueChanged = prev !== next || valueEl.getAttribute('data-kpi-suffix') !== suffix;

            if (prev !== next) {
                animateKpiValue(valueEl, prev, next, suffix);
            } else if (valueEl.getAttribute('data-kpi-suffix') !== suffix) {
                valueEl.setAttribute('data-kpi-suffix', suffix);
                valueEl.textContent = Math.round(next) + suffix;
            }

            var deltaEl = el.querySelector('.kpi-delta-pill');
            if (deltaEl && card.delta) {
                deltaEl.textContent = card.delta;
                deltaEl.className = 'kpi-delta-pill kpi-delta-' + (card.deltaTone || 'neutral');
            }

            var sparkCfg = payload.sparklines && payload.sparklines[index];
            var chart = chartRegistry.sparklines[index];
            var nextCfg = charts && charts.sparklines ? charts.sparklines[index] : null;
            var sparkChanged = nextCfg && sparkCfg && stableJson(nextCfg.values) !== stableJson(sparkCfg.values);

            if (chart && nextCfg && sparkChanged) {
                var bounds = sparklineYBounds(nextCfg.values);
                chart.data.labels = nextCfg.labels;
                chart.data.datasets[0].data = nextCfg.values;
                chart.options.scales.y.min = bounds.yMin;
                chart.options.scales.y.max = bounds.yMax;
                chart.update('none');
                payload.sparklines[index] = nextCfg;
            }

            if (highlightChanged && (valueChanged || sparkChanged)) {
                highlightCard(el);
            }
        });
    }

    function updateHourlyChart(chartData, highlightChanged) {
        if (!chartData || !chartData.values || chartData.values.length < 2) return;

        var card = document.querySelector('.card--chart-hourly');
        var prev = payload.hourlyResponses;
        var changed = !prev || stableJson(prev) !== stableJson(chartData);

        if (chartRegistry.hourly) {
            if (changed) {
                chartRegistry.hourly.data.labels = chartData.labels;
                chartRegistry.hourly.data.datasets[0].data = chartData.values;
                chartRegistry.hourly.update('active');
                payload.hourlyResponses = chartData;
                if (highlightChanged) highlightCard(card);
            }
            return;
        }

        var canvas = document.getElementById('chart-hourly-responses');
        if (canvas) {
            chartRegistry.hourly = createHourlyChart(canvas, chartData);
            payload.hourlyResponses = chartData;
        }
    }

    function fingerprint(snapshot) {
        return {
            kpi: stableJson((snapshot.kpiCards || []).map(function (k, index) {
                var spark = snapshot.charts && snapshot.charts.sparklines
                    ? snapshot.charts.sparklines[index]
                    : null;
                return {
                    key: k.key,
                    countValue: k.countValue,
                    suffix: k.valueSuffix || '',
                    spark: spark ? spark.values : []
                };
            })),
            workers: stableJson(snapshot.workers || []),
            events: stableJson(snapshot.events || []),
            accountStats: stableJson(snapshot.accountStats || {}),
            hourly: stableJson(snapshot.charts ? snapshot.charts.hourlyResponses : null)
        };
    }

    function applySnapshot(snapshot, highlightChanged) {
        if (!snapshot) return;

        var prevFp = liveState ? fingerprint(liveState) : null;
        var nextFp = fingerprint(snapshot);

        if (!prevFp || prevFp.kpi !== nextFp.kpi) {
            updateKpiCards(snapshot.kpiCards || [], snapshot.charts, highlightChanged);
        }

        if (!prevFp || prevFp.workers !== nextFp.workers) {
            renderWorkers(snapshot.workers || []);
            if (highlightChanged) highlightCard(document.querySelector('.card--dashboard-workers'));
        }

        if (!prevFp || prevFp.hourly !== nextFp.hourly) {
            if (snapshot.charts && snapshot.charts.hourlyResponses) {
                updateHourlyChart(snapshot.charts.hourlyResponses, highlightChanged);
            }
        }

        if (!prevFp || prevFp.events !== nextFp.events) {
            renderEvents(snapshot.events || []);
            if (highlightChanged) highlightCard(document.querySelector('.card--dashboard-events'));
        }

        if (!prevFp || prevFp.accountStats !== nextFp.accountStats) {
            updateAccountStats(snapshot.accountStats || {});
            if (highlightChanged) highlightCard(document.querySelector('.card--account-stats'));
        }

        if (snapshot.charts) {
            payload.sparklines = snapshot.charts.sparklines || payload.sparklines;
            payload.accountStatus = snapshot.charts.accountStatus || payload.accountStatus;
        }

        liveState = snapshot;
        updateUpdatedClock(snapshot.updatedAtUtc);
    }

    function updateUpdatedClock(isoUtc) {
        if (!isoUtc) return;
        document.querySelectorAll('.orbita-updated-time').forEach(function (el) {
            el.setAttribute('data-orbita-utc', isoUtc);
            if (window.OrbitaTime) {
                window.OrbitaTime.localizeElement(el);
            }
        });
    }

    function setRefreshBusy(busy) {
        document.querySelectorAll('[data-orbita-refresh]').forEach(function (btn) {
            btn.classList.toggle('is-refreshing', busy);
            if (busy) {
                btn.setAttribute('aria-busy', 'true');
            } else {
                btn.removeAttribute('aria-busy');
            }
        });
    }

    function fetchSnapshot() {
        if (!liveRoot || pollInFlight) return Promise.resolve();

        var url = liveRoot.getAttribute('data-dashboard-snapshot');
        if (!url) return Promise.resolve();

        pollInFlight = true;
        setRefreshBusy(true);

        return fetch(url, {
            method: 'GET',
            credentials: 'same-origin',
            headers: { Accept: 'application/json' }
        })
            .then(function (response) {
                if (!response.ok) throw new Error('Dashboard snapshot failed: ' + response.status);
                return response.json();
            })
            .then(function (snapshot) {
                applySnapshot(snapshot, true);
            })
            .catch(function (err) {
                console.warn('Dashboard live refresh:', err);
            })
            .finally(function () {
                pollInFlight = false;
                setRefreshBusy(false);
            });
    }

    function schedulePoll() {
        if (pollTimer) window.clearInterval(pollTimer);
        pollTimer = window.setInterval(function () {
            if (document.hidden) return;
            fetchSnapshot();
        }, pollIntervalMs);
    }

    function initLiveRefresh() {
        if (!liveRoot) return;

        var bootstrapEl = document.getElementById('dashboard-live-bootstrap');
        if (bootstrapEl) {
            try {
                liveState = JSON.parse(bootstrapEl.textContent || 'null');
            } catch (e) {
                console.warn('Dashboard live bootstrap parse failed', e);
            }
        }

        document.addEventListener('visibilitychange', function () {
            if (!document.hidden) fetchSnapshot();
        });

        document.querySelectorAll('[data-orbita-refresh]').forEach(function (btn) {
            btn.addEventListener('click', function (e) {
                if (!document.querySelector('[data-dashboard-live]')) return;
                e.preventDefault();
                fetchSnapshot();
            });
        });

        schedulePoll();
        window.setTimeout(fetchSnapshot, pollIntervalMs);
    }

    initKpiCounters();
    initSparklines(payload.sparklines);
    initHourlyChart(payload.hourlyResponses);
    initDonutChart(payload.accountStatus);
    initLiveRefresh();
})();