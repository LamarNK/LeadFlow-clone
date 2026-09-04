(function () {
    function getLiveRoot() {
        return document.querySelector('[data-orbita-live]') || document.querySelector('[data-dashboard-live]');
    }

    function hasChart() {
        return typeof Chart !== 'undefined';
    }

    function readChartsPayload() {
        var el = document.getElementById('dashboard-charts-data');
        if (!el) return null;
        try {
            return JSON.parse(el.textContent || '{}');
        } catch (e) {
            console.error('Dashboard charts: invalid JSON', e);
            return null;
        }
    }

    var payload = readChartsPayload() || {};

    var chartRegistry = {
        sparklines: [],
        hourly: null,
        donut: null
    };

    function destroyChartOnCanvas(canvas) {
        if (!canvas || typeof Chart === 'undefined' || typeof Chart.getChart !== 'function') return;
        var existing = Chart.getChart(canvas);
        if (existing) {
            try { existing.destroy(); } catch (e) { }
        }
    }

    function destroyAllCharts() {
        if (typeof Chart === 'undefined') return;

        chartRegistry.sparklines.forEach(function (chart) {
            if (chart) {
                try { chart.destroy(); } catch (e) { }
            }
        });
        chartRegistry.sparklines = [];

        if (chartRegistry.hourly) {
            try { chartRegistry.hourly.destroy(); } catch (e) { }
            chartRegistry.hourly = null;
        }

        if (chartRegistry.donut) {
            try { chartRegistry.donut.destroy(); } catch (e) { }
            chartRegistry.donut = null;
        }

        document.querySelectorAll('[data-sparkline-index], #chart-hourly-responses, #chart-account-status').forEach(destroyChartOnCanvas);
    }

    var liveState = null;
    var activeWorkerFilter = 'all';
    var workerSearchQuery = '';
    function dashboardPreference(key, fallback) {
        try {
            return window.sessionStorage.getItem(key) || fallback;
        } catch (e) {
            return fallback;
        }
    }

    function saveDashboardPreference(key, value) {
        try {
            window.sessionStorage.setItem(key, value);
        } catch (e) { }
    }

    var workerView = dashboardPreference('orbita-dashboard-worker-view', 'table');
    var highlightMs = 1800;

    // The dashboard script can be prefetched before Chart.js finishes loading.
    // Keep the non-chart controls available in that case, then apply
    // chart-specific defaults when charts are initialized.
    function configureChartDefaults() {
        if (!hasChart()) return;
        Chart.defaults.font.family = '"Segoe UI", system-ui, -apple-system, sans-serif';
        Chart.defaults.font.size = 11;
        Chart.defaults.color = '#94a3b8';
    }

    function localizeChartData(chartData) {
        if (window.OrbitaTime && window.OrbitaTime.localizeHourlyChart) {
            return window.OrbitaTime.localizeHourlyChart(chartData);
        }
        return chartData;
    }

    function sparklineTooltipTitle(items) {
        if (!items.length) return '';
        var chart = items[0].chart;
        var utcHours = chart.$utcHours;
        var refDay = chart.$referenceDayUtc;
        if (utcHours && utcHours.length && window.OrbitaTime && window.OrbitaTime.utcHourToLocalLabel) {
            var idx = items[0].dataIndex;
            if (idx >= 0 && idx < utcHours.length) {
                return window.OrbitaTime.utcHourToLocalLabel(utcHours[idx], refDay);
            }
        }
        return String(items[0].label || '');
    }

    function dashboardTooltipOptions(metricLabel, valueAtIndex, titleAtIndex) {
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
                    if (typeof titleAtIndex === 'function') {
                        return titleAtIndex(items);
                    }
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

    function isSparklineChart(cfg) {
        return cfg && cfg.kind !== 'segments' && cfg.values && cfg.values.length >= 2;
    }

    function segmentTrackTotal(segments) {
        if (!Array.isArray(segments) || segments.length === 0) return 0;
        if (segments.length === 1 && segments[0].label === 'Нет данных') return 0;
        return segments.reduce(function (sum, segment) {
            return sum + (segment.value || 0);
        }, 0);
    }

    function renderSegmentTrack(track, segments) {
        if (!track || !Array.isArray(segments)) return;

        var total = segmentTrackTotal(segments);
        var hasData = total > 0;
        track.innerHTML = '';

        segments.forEach(function (segment) {
            var pct = hasData ? segment.value * 100 / total : 100;
            var piece = document.createElement('span');
            piece.className = 'kpi-segment-piece';
            piece.style.width = pct + '%';
            piece.style.backgroundColor = segment.color;
            piece.title = segment.label + ': ' + segment.value;
            piece.setAttribute('data-segment-label', segment.label);
            piece.setAttribute('data-segment-value', String(segment.value));
            track.appendChild(piece);
        });
    }

    function updateKpiSegmentTrack(cardEl, segments) {
        if (!cardEl || !Array.isArray(segments) || segments.length === 0) return false;

        var track = cardEl.querySelector('[data-kpi-segment-track]');
        if (!track) return false;

        renderSegmentTrack(track, segments);
        return true;
    }

    function initSparklines(sparklines) {
        if (!Array.isArray(sparklines)) return;
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

        document.querySelectorAll('[data-sparkline-index]').forEach(function (canvas) {
            var index = parseInt(canvas.getAttribute('data-sparkline-index'), 10);
            var cfg = sparklines[index];
            if (!cfg || !isSparklineChart(cfg)) return;

            destroyChartOnCanvas(canvas);
            chartRegistry.sparklines[index] = createSparklineChart(canvas, cfg, index, reduced);
        });
    }

    function resolveSparklineTooltipValues(cfg) {
        var values = cfg.values || [];
        if (!values.length) return null;
        if (cfg.tooltipValues && cfg.tooltipValues.length === values.length) {
            return cfg.tooltipValues;
        }
        return values;
    }

    function applySparklineChartData(chart, cfg) {
        if (!chart || !cfg || !cfg.values || cfg.values.length < 2) return;

        cfg = localizeChartData(cfg);
        var values = cfg.values;
        var labels = cfg.labels && cfg.labels.length === values.length
            ? cfg.labels
            : values.map(function (_, i) { return String(i + 1); });
        var tooltipValues = resolveSparklineTooltipValues(cfg);
        var yBounds = sparklineYBounds(values);

        chart.data.labels = labels;
        chart.data.datasets.forEach(function (dataset) {
            dataset.data = values;
        });
        chart.options.scales.y.min = yBounds.yMin;
        chart.options.scales.y.max = yBounds.yMax;
        chart.$tooltipValues = tooltipValues;
        chart.$metricLabel = cfg.metricLabel || 'Значение';
        chart.$utcHours = cfg.utcHours || null;
        chart.$referenceDayUtc = cfg.referenceDayUtc || null;
    }

    function createSparklineChart(canvas, cfg, index, reduced) {
        cfg = localizeChartData(cfg);
        var color = cfg.color || '#2563eb';
        var values = cfg.values;
        var labels = cfg.labels && cfg.labels.length === values.length
            ? cfg.labels
            : values.map(function (_, i) { return String(i + 1); });
        var metricLabel = cfg.metricLabel || 'Значение';
        var tooltipValues = resolveSparklineTooltipValues(cfg);
        var yBounds = sparklineYBounds(values);
        var areaFill = function (context) {
            var chart = context.chart;
            var area = chart.chartArea;
            if (!area) return 'transparent';

            var match = color.match(/^#([\da-f]{2})([\da-f]{2})([\da-f]{2})$/i);
            if (!match) return 'transparent';

            var red = parseInt(match[1], 16);
            var green = parseInt(match[2], 16);
            var blue = parseInt(match[3], 16);
            var gradient = chart.ctx.createLinearGradient(0, area.top, 0, area.bottom);
            gradient.addColorStop(0, 'rgba(' + red + ', ' + green + ', ' + blue + ', 0.22)');
            gradient.addColorStop(0.55, 'rgba(' + red + ', ' + green + ', ' + blue + ', 0.07)');
            gradient.addColorStop(1, 'rgba(' + red + ', ' + green + ', ' + blue + ', 0)');
            return gradient;
        };

        var chart = new Chart(canvas, {
            type: 'line',
            data: {
                labels: labels,
                datasets: [{
                    data: values,
                    borderColor: color,
                    backgroundColor: areaFill,
                    fill: 'start',
                    cubicInterpolationMode: 'monotone',
                    tension: 0.36,
                    borderWidth: 1.9,
                    borderCapStyle: 'round',
                    borderJoinStyle: 'round',
                    pointRadius: 0,
                    pointBackgroundColor: color,
                    pointBorderWidth: 0,
                    pointHoverRadius: 3,
                    pointHoverBackgroundColor: color,
                    pointHoverBorderColor: '#ffffff',
                    pointHoverBorderWidth: 1.5,
                    pointHitRadius: 12
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
                    tooltip: dashboardTooltipOptions(
                        metricLabel,
                        function (idx, fallback) {
                            var source = chart.$tooltipValues || tooltipValues;
                            return source ? source[idx] : fallback;
                        },
                        sparklineTooltipTitle)
                },
                scales: {
                    x: { display: false, offset: false },
                    y: { display: false, min: yBounds.yMin, max: yBounds.yMax }
                },
                layout: { padding: { top: 4, bottom: 3, left: 2, right: 2 } }
            }
        });

        chart.$tooltipValues = tooltipValues;
        chart.$metricLabel = metricLabel;
        chart.$utcHours = cfg.utcHours || null;
        chart.$referenceDayUtc = cfg.referenceDayUtc || null;
        return chart;
    }

    function sparklineYBounds(values) {
        var minVal = Math.min.apply(null, values);
        var maxVal = Math.max.apply(null, values);
        var isFlat = minVal === maxVal;
        if (isFlat) {
            var flatPad = minVal === 0 ? 1 : Math.max(1, Math.ceil(minVal * 0.15));
            return {
                isFlat: true,
                yMin: Math.max(0, minVal - flatPad),
                yMax: minVal + flatPad
            };
        }

        var pad = Math.max(1, Math.ceil((maxVal - minVal) * 0.12));
        return {
            isFlat: false,
            yMin: Math.max(0, minVal - pad),
            yMax: maxVal + pad
        };
    }

    function activityChartPointCount(chartData) {
        if (!chartData) return 0;
        if (chartData.labels && chartData.labels.length) return chartData.labels.length;
        if (chartData.series && chartData.series.length && chartData.series[0].values) {
            return chartData.series[0].values.length;
        }
        return chartData.values ? chartData.values.length : 0;
    }

    function activityChartSeries(chartData) {
        if (chartData && chartData.series && chartData.series.length) {
            return chartData.series;
        }
        return [{
            label: 'Откликов',
            color: '#2563eb',
            values: chartData && chartData.values ? chartData.values : []
        }];
    }

    function activityChartValues(chartData) {
        var values = [];
        activityChartSeries(chartData).forEach(function (series) {
            (series.values || []).forEach(function (value) {
                values.push(Number(value) || 0);
            });
        });
        return values;
    }

    function buildHourlyDatasets(chartData, canvas) {
        return activityChartSeries(chartData).map(function (series, seriesIndex) {
            var lineColor = series.color || '#2563eb';
            var areaFill = function (context) {
                if (seriesIndex !== 0 || !canvas) return 'transparent';

                var area = context.chart.chartArea;
                if (!area) return 'transparent';

                var match = lineColor.match(/^#([\da-f]{2})([\da-f]{2})([\da-f]{2})$/i);
                if (!match) return 'transparent';

                var red = parseInt(match[1], 16);
                var green = parseInt(match[2], 16);
                var blue = parseInt(match[3], 16);
                var gradient = context.chart.ctx.createLinearGradient(0, area.top, 0, area.bottom);
                gradient.addColorStop(0, 'rgba(' + red + ', ' + green + ', ' + blue + ', 0.16)');
                gradient.addColorStop(0.7, 'rgba(' + red + ', ' + green + ', ' + blue + ', 0.035)');
                gradient.addColorStop(1, 'rgba(' + red + ', ' + green + ', ' + blue + ', 0)');
                return gradient;
            };

            return {
                label: series.label || 'Значение',
                data: series.values || [],
                borderColor: lineColor,
                backgroundColor: areaFill,
                fill: seriesIndex === 0 ? 'start' : false,
                cubicInterpolationMode: 'monotone',
                tension: 0.32,
                borderWidth: 2.25,
                borderCapStyle: 'round',
                borderJoinStyle: 'round',
                pointRadius: 2.5,
                pointBackgroundColor: lineColor,
                pointBorderColor: '#ffffff',
                pointBorderWidth: 1,
                pointHoverRadius: 4.5,
                pointHoverBackgroundColor: lineColor,
                pointHoverBorderColor: '#ffffff',
                pointHoverBorderWidth: 2,
                pointHitRadius: 10
            };
        });
    }

    function initHourlyChart(chartData) {
        var canvas = document.getElementById('chart-hourly-responses');
        if (!canvas || activityChartPointCount(chartData) < 2) return;

        destroyChartOnCanvas(canvas);
        chartRegistry.hourly = createHourlyChart(canvas, chartData);
    }

    function hourlyYBounds(chartData) {
        var nums = activityChartValues(chartData);
        if (!nums.length) {
            return { yMin: 0, yMax: 5, step: 1 };
        }
        var maxVal = Math.max.apply(null, nums);
        if (maxVal === 0) {
            return { yMin: 0, yMax: 5, step: 1 };
        }
        var padded = maxVal + Math.max(1, Math.ceil(maxVal * 0.15));
        var step = Math.max(1, Math.ceil(padded / 5));
        return { yMin: 0, yMax: Math.ceil(padded / step) * step, step: step };
    }

    function createHourlyChart(canvas, chartData) {
        chartData = localizeChartData(chartData);
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        var labels = chartData.labels || [];
        var yBounds = hourlyYBounds(chartData);

        return new Chart(canvas, {
            type: 'line',
            data: {
                labels: labels,
                datasets: buildHourlyDatasets(chartData, canvas)
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
                    legend: {
                        display: true,
                        position: 'bottom',
                        labels: {
                            boxWidth: 8,
                            boxHeight: 8,
                            padding: 18,
                            color: '#667085',
                            font: { size: 13, weight: '600' },
                            usePointStyle: true,
                            pointStyle: 'circle'
                        }
                    },
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
                        callbacks: {
                            title: function (items) {
                                if (!items.length) return '';
                                var chart = items[0].chart;
                                var chartLabels = chart.data && chart.data.labels ? chart.data.labels : [];
                                return String(chartLabels[items[0].dataIndex] || items[0].label || '');
                            },
                            label: function (ctx) {
                                return (ctx.dataset.label || 'Значение') + ': ' + ctx.parsed.y;
                            }
                        }
                    }
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
                        min: yBounds.yMin,
                        max: yBounds.yMax,
                        grid: {
                            color: '#edf1f5',
                            lineWidth: 1
                        },
                        border: { display: false },
                        ticks: {
                            stepSize: yBounds.step,
                            color: '#98a2b3',
                            font: { size: 12, weight: '400' },
                            padding: 8
                        }
                    }
                },
                layout: {
                    padding: { top: 12, right: 12, bottom: 2, left: 0 }
                }
            }
        });
    }

    function donutDataset(chartData) {
        var total = chartData.total || 0;
        var active = chartData.active || 0;
        var inactive = chartData.inactive || 0;
        var errors = chartData.errors || 0;
        var segments = [
            { label: 'Активны', value: active, color: '#22c55e' },
            { label: 'Неактивны', value: inactive, color: '#94a3b8' },
            { label: 'Ошибки', value: errors, color: '#f59e0b' }
        ].filter(function (segment) { return segment.value > 0; });

        if (total === 0 || segments.length === 0) {
            return {
                total: total,
                labels: ['Нет данных'],
                values: [1],
                colors: ['#e5e7eb']
            };
        }

        return {
            total: total,
            labels: segments.map(function (segment) { return segment.label; }),
            values: segments.map(function (segment) { return segment.value; }),
            colors: segments.map(function (segment) { return segment.color; })
        };
    }

    function initDonutChart(chartData) {
        var canvas = document.getElementById('chart-account-status');
        if (!canvas || !chartData) return;

        destroyChartOnCanvas(canvas);
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
                        var count = ctx.chart.data.datasets[0].data.length;
                        return count > 1 ? 4 : 0;
                    },
                    borderAlign: 'inner',
                    hoverOffset: 0,
                    spacing: 2
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                cutout: '64%',
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
        var root = getLiveRoot();
        var template = root ? root.getAttribute('data-worker-details-url') : '';
        return template ? template.replace('__id__', id) : '#';
    }

    function accountSearchUrl(name) {
        var shared = window.OrbitaLiveShared;
        if (shared) {
            return shared.urlFromTemplate(shared.getLiveAttr('data-account-search-url'), '__q__', name);
        }
        var root = getLiveRoot();
        var template = root ? root.getAttribute('data-account-search-url') : '';
        return name && template ? template.split('__q__').join(encodeURIComponent(String(name))) : '';
    }

    function settingsLogsUrl(workerId) {
        var shared = window.OrbitaLiveShared;
        if (shared) {
            return shared.urlFromTemplate(shared.getLiveAttr('data-settings-logs-url'), '__id__', workerId);
        }
        var root = getLiveRoot();
        var template = root ? root.getAttribute('data-settings-logs-url') : '';
        return template ? template.replace('__id__', workerId) : '';
    }

    function renderWorkerToggleCell(w) {
        if (!w.isMonitoringPaused) {
            return '<td class="dashboard-worker-toggle" data-label="">' +
                '<button type="button" class="dashboard-worker-toggle-btn dashboard-worker-toggle-btn--pause" ' +
                'data-dashboard-disable-worker data-worker-id="' + escapeHtml(w.id) + '" ' +
                'title="Пауза мониторинга" aria-label="Пауза мониторинга ' + escapeHtml(w.displayName) + '">' +
                '<i class="fa-solid fa-circle-pause" aria-hidden="true"></i><span class="dashboard-worker-toggle-label">Пауза</span></button></td>';
        }

        return '<td class="dashboard-worker-toggle" data-label="">' +
            '<button type="button" class="dashboard-worker-toggle-btn dashboard-worker-toggle-btn--play" ' +
            'data-dashboard-enable-worker data-worker-id="' + escapeHtml(w.id) + '" ' +
            'title="Возобновить мониторинг" aria-label="Возобновить мониторинг ' + escapeHtml(w.displayName) + '">' +
            '<i class="fa-solid fa-circle-play" aria-hidden="true"></i><span class="dashboard-worker-toggle-label">Возобновить</span></button></td>';
    }

    function updateWorkerToolbar(tabCounts) {
        var counts = tabCounts || { all: 0, online: 0, offline: 0, empty: 0, paused: 0 };
        Object.keys(counts).forEach(function (key) {
            document.querySelectorAll('[data-dashboard-worker-count="' + key + '"]').forEach(function (el) {
                el.textContent = String(counts[key]);
            });
        });
    }

    function applyWorkerFilter() {
        document.querySelectorAll('[data-dashboard-workers-body] tr[data-href]').forEach(function (row) {
            var workerSearchText = (row.textContent + ' ' + (row.getAttribute('data-dashboard-worker-ip') || ''))
                .toLocaleLowerCase();
            var matchesSearch = !workerSearchQuery || workerSearchText.indexOf(workerSearchQuery) !== -1;
            row.hidden = !matchesSearch;
        });
    }

    function submitDashboardSortForm(form) {
        if (!form) return;
        if (typeof form.requestSubmit === 'function') {
            form.requestSubmit();
            return;
        }
        form.submit();
    }

    function initDashboardWorkerToolbar() {
        var workerCard = document.querySelector('.card--dashboard-workers');
        if (workerCard) {
            workerCard.classList.toggle('dashboard-workers-view--cards', workerView === 'cards');
        }
        document.querySelectorAll('[data-dashboard-worker-filter]').forEach(function (button) {
            if (button.hasAttribute('data-dashboard-worker-filter-bound')) return;
            button.setAttribute('data-dashboard-worker-filter-bound', '1');
            button.addEventListener('click', function () {
                activeWorkerFilter = button.getAttribute('data-dashboard-worker-filter') || 'all';
                var url = new URL(window.location.href);
                url.searchParams.set('workerFilter', activeWorkerFilter);
                url.searchParams.set('page', '1');
                window.location.assign(url.toString());
            });
        });
        document.querySelectorAll('[data-dashboard-worker-search]').forEach(function (input) {
            if (input.hasAttribute('data-dashboard-worker-search-bound')) return;
            input.setAttribute('data-dashboard-worker-search-bound', '1');
            input.addEventListener('input', function () {
                workerSearchQuery = String(input.value || '').trim().toLocaleLowerCase();
                applyWorkerFilter();
            });
        });
        document.querySelectorAll('[data-dashboard-worker-sort-form]').forEach(function (form) {
            if (form.hasAttribute('data-dashboard-worker-sort-form-bound')) return;
            form.setAttribute('data-dashboard-worker-sort-form-bound', '1');
            var select = form.querySelector('[data-dashboard-worker-sort-key]');
            if (select) {
                select.addEventListener('change', function () {
                    submitDashboardSortForm(form);
                });
            }
            var button = form.querySelector('[data-dashboard-worker-sort]');
            if (button) {
                button.addEventListener('click', function () {
                    var dirInput = form.querySelector('input[name="dir"]');
                    if (dirInput) {
                        dirInput.value = dirInput.value === 'desc' ? 'asc' : 'desc';
                    }
                    submitDashboardSortForm(form);
                });
            }
        });
        document.querySelectorAll('[data-dashboard-worker-view]').forEach(function (button) {
            var isCurrentView = button.getAttribute('data-dashboard-worker-view') === workerView;
            button.classList.toggle('is-active', isCurrentView);
            button.setAttribute('aria-pressed', isCurrentView ? 'true' : 'false');
            if (button.hasAttribute('data-dashboard-worker-view-bound')) return;
            button.setAttribute('data-dashboard-worker-view-bound', '1');
            button.addEventListener('click', function () {
                workerView = button.getAttribute('data-dashboard-worker-view') === 'cards' ? 'cards' : 'table';
                saveDashboardPreference('orbita-dashboard-worker-view', workerView);
                var workerCard = document.querySelector('.card--dashboard-workers');
                if (workerCard) {
                    workerCard.classList.toggle('dashboard-workers-view--cards', workerView === 'cards');
                }
                document.querySelectorAll('[data-dashboard-worker-view]').forEach(function (item) {
                    var selected = item === button;
                    item.classList.toggle('is-active', selected);
                    item.setAttribute('aria-pressed', selected ? 'true' : 'false');
                });
            });
        });
        applyWorkerFilter();
    }

    function renderWorkers(workers) {
        var tbody = document.querySelector('[data-dashboard-workers-body]');
        if (!tbody) return;

        tbody.innerHTML = workers.map(function (w) {
            var statusClass = !w.isEnabled ? ' offline' : (w.isOnline ? '' : ' offline');
            var statusText = !w.isEnabled ? 'Приостановлен' : (w.isOnline ? 'Онлайн' : 'Оффлайн');
            var iso = w.lastActivityUtc || '';
            var timeHtml = iso
                ? '<time class="" data-orbita-utc="' + escapeHtml(iso) + '" data-orbita-format="activity"></time>'
                : '—';

            var detailsUrl = workerDetailsUrl(w.id);
            var machineName = (w.machineName || '').trim();
            var showMachine = machineName
                && machineName.localeCompare((w.displayName || '').trim(), undefined, { sensitivity: 'accent' }) !== 0;
            var isMonitoringPaused = !!w.isMonitoringPaused;
            var pausedBadge = isMonitoringPaused
                ? '<span class="workers-status-badge workers-status-badge--paused">Пауза мониторинга</span>'
                : '';
            var nameCell = '<div class="cell-name-stack">' +
                '<a href="' + escapeHtml(detailsUrl) + '">' + escapeHtml(w.displayName) + '</a>' +
                (showMachine ? '<span class="cell-name-machine">' + escapeHtml(machineName) + '</span>' : '') +
                '</div>' + pausedBadge;
            var officeCell = '';
            var liveRoot = getLiveRoot();
            if (liveRoot && liveRoot.getAttribute('data-show-office-column') === 'true') {
                officeCell = '<td data-label="Офис">' + escapeHtml(w.officeName || '—') + '</td>';
            }
            var totalAccounts = Math.max(0, Number(w.totalAccounts) || 0);
            var activeAccounts = Math.max(0, Number(w.activeAccounts) || 0);
            var accountProgress = totalAccounts > 0 ? Math.min(100, Math.round(activeAccounts * 100 / totalAccounts)) : 0;
            var accountsCell = '<td class="cell-num dashboard-account-cell" data-label="Аккаунтов">' +
                '<span>' + activeAccounts + ' / ' + totalAccounts + '</span>' +
                '<span class="dashboard-account-progress" aria-label="Активно аккаунтов: ' + activeAccounts + ' из ' + totalAccounts + '"><span style="width:' + accountProgress + '%"></span></span>' +
                '</td>';

            var lowBalanceCount = Number(w.lowBalanceAccountCount) || 0;
            var hasLowBalance = lowBalanceCount > 0;
            var lowBalanceClass = hasLowBalance ? ' dashboard-worker-row--low-balance' : '';
            var monitoringPausedClass = isMonitoringPaused ? ' dashboard-worker-row--monitoring-paused' : '';
            var lowBalanceTooltip = hasLowBalance
                ? '<span class="dashboard-low-balance-tooltip" title="Аккаунтов с балансом ниже 150 ₽: ' + lowBalanceCount + '" aria-label="Предупреждение: ' + lowBalanceCount + ' аккаунтов с низким балансом"><i class="fa-solid fa-triangle-exclamation" aria-hidden="true"></i></span>'
                : '';
            var monitoringPausedTooltip = isMonitoringPaused
                ? '<span class="dashboard-monitoring-paused-tooltip" title="Мониторинг приостановлен" aria-label="Предупреждение: мониторинг приостановлен"><i class="fa-solid fa-triangle-exclamation" aria-hidden="true"></i></span>'
                : '';

            return '<tr class="dashboard-worker-row' + lowBalanceClass + monitoringPausedClass + '" data-href="' + escapeHtml(detailsUrl) + '" data-dashboard-worker-online="' + (w.isEnabled && w.isOnline ? 'true' : 'false') + '" data-dashboard-worker-empty="' + (!w.totalAccounts ? 'true' : 'false') + '" data-dashboard-worker-paused="' + (isMonitoringPaused ? 'true' : 'false') + '" data-dashboard-worker-activity="' + (iso ? Date.parse(iso) || 0 : 0) + '" data-dashboard-worker-name="' + escapeHtml(w.displayName || '') + '" data-dashboard-worker-ip="' + escapeHtml(w.ipAddress || '') + '" data-dashboard-worker-responses="' + (Number(w.responses) || 0) + '" data-dashboard-worker-errors="' + (Number(w.errors) || 0) + '">' +
                '<td class="cell-name" data-label="Воркер">' + nameCell + lowBalanceTooltip + monitoringPausedTooltip + '</td>' +
                officeCell +
                '<td data-label="Статус"><span class="status-dot' + statusClass + '"><i class="fa-solid fa-circle status-dot-icon" aria-hidden="true"></i>' + statusText + '</span></td>' +
                '<td data-label="Сейчас">' + (window.OrbitaLiveShared ? window.OrbitaLiveShared.renderActivityPill(w.currentActivityLabel, w.currentActivityTone, w.isActivityLive, window.OrbitaLiveShared.activityPillExtrasFromWorker(w)) : escapeHtml(w.currentActivityLabel || '—')) + '</td>' +
                accountsCell +
                '<td class="cell-num" data-label="Откликов">' + w.responses + '</td>' +
                '<td class="cell-num" data-label="Дублей">' + w.duplicates + '</td>' +
                '<td class="cell-num" data-label="Ошибок">' + w.errors + '</td>' +
                '<td data-label="Последняя активность">' + timeHtml + '</td>' +
                renderWorkerToggleCell(w) +
                '<td class="data-table-menu" data-label="">' +
                '<div class="row-menu" data-row-menu>' +
                '<button type="button" class="row-menu-btn" aria-label="Действия" aria-expanded="false" aria-haspopup="true"><i class="fa-solid fa-ellipsis-vertical" aria-hidden="true"></i></button>' +
                '<div class="row-menu-dropdown" hidden>' +
                '<a class="row-menu-item" href="' + escapeHtml(detailsUrl) + '">Открыть</a>' +
                '<button type="button" class="row-menu-item" data-worker-restart data-worker-id="' + escapeHtml(w.id) + '">Перезапустить</button>' +
                '<a class="row-menu-item" href="' + escapeHtml(settingsLogsUrl(w.id)) + '">Просмотреть логи</a>' +
                '<a class="row-menu-item" href="' + escapeHtml(detailsUrl) + '#worker-settings">Настройки</a>' +
                '</div></div></td>' +
                '</tr>';
        }).join('');

        if (window.OrbitaTime) {
            window.OrbitaTime.localizeAll(tbody);
        }

        initDashboardRowMenus();
        initDashboardRowNavigation();
        initDashboardWorkerToggleButtons();
        initDashboardWorkerToolbar();
        if (window.Orbita && window.Orbita.initWorkerRestartButtons) {
            window.Orbita.initWorkerRestartButtons();
        }
    }

    function initDashboardWorkerToggleButtons() {
        var liveRoot = getLiveRoot();
        if (!liveRoot) return;

        var enableUrl = liveRoot.getAttribute('data-dashboard-enable-worker-url');
        var disableUrl = liveRoot.getAttribute('data-dashboard-disable-worker-url');
        var postForm = window.Orbita && window.Orbita.postForm;
        var showToast = window.Orbita && window.Orbita.showToast;

        function bindToggle(selector, url) {
            document.querySelectorAll(selector).forEach(function (btn) {
                if (btn.hasAttribute('data-dashboard-worker-toggle-bound')) return;
                btn.setAttribute('data-dashboard-worker-toggle-bound', '1');
                btn.addEventListener('click', async function (e) {
                    e.preventDefault();
                    e.stopPropagation();
                    var workerId = btn.getAttribute('data-worker-id');
                    if (!workerId || !url || !postForm) return;
                    btn.disabled = true;
                    var result = await postForm(url, { workerId: workerId });
                    if (result.ok) {
                        if (showToast) {
                            showToast((result.payload && result.payload.message) || 'Готово', { variant: 'success' });
                        }
                        fetchSnapshot();
                    } else {
                        if (showToast) {
                            showToast((result.payload && result.payload.error) || 'Не удалось изменить паузу мониторинга', { variant: 'error' });
                        }
                        btn.disabled = false;
                    }
                });
            });
        }

        bindToggle('[data-dashboard-enable-worker]', enableUrl);
        bindToggle('[data-dashboard-disable-worker]', disableUrl);
    }

    function updateMonitoringControls(snapshot) {
        var enableBtn = document.querySelector('[data-dashboard-enable-all]');
        var disableBtn = document.querySelector('[data-dashboard-disable-all]');
        if (!enableBtn && !disableBtn) return;

        var disabledCount = snapshot && typeof snapshot.disabledWorkersCount === 'number'
            ? snapshot.disabledWorkersCount
            : 0;
        var enabledCount = snapshot && typeof snapshot.enabledWorkersCount === 'number'
            ? snapshot.enabledWorkersCount
            : 0;

        // Нет паузы → только «Пауза»; иначе обе кнопки.
        var allEnabled = disabledCount === 0 && enabledCount > 0;

        if (enableBtn) {
            enableBtn.hidden = allEnabled;
            enableBtn.disabled = disabledCount === 0;
        }
        if (disableBtn) {
            disableBtn.hidden = false;
            disableBtn.disabled = enabledCount === 0;
        }
    }

    function initDashboardMonitoringButtons() {
        var liveRoot = getLiveRoot();
        if (!liveRoot) return;

        var enableUrl = liveRoot.getAttribute('data-dashboard-enable-all-url');
        var disableUrl = liveRoot.getAttribute('data-dashboard-disable-all-url');
        var postForm = window.Orbita && window.Orbita.postForm;
        var confirmDialog = window.Orbita && window.Orbita.confirm;
        var showToast = window.Orbita && window.Orbita.showToast;

        document.querySelectorAll('[data-dashboard-enable-all]').forEach(function (btn) {
            if (btn.hasAttribute('data-dashboard-monitoring-bound')) return;
            btn.setAttribute('data-dashboard-monitoring-bound', '1');
            btn.addEventListener('click', async function (e) {
                e.preventDefault();
                e.stopPropagation();
                if (!enableUrl || !postForm) return;
                if (confirmDialog) {
                    var confirmed = await confirmDialog({
                        title: 'Возобновить мониторинг?',
                        message: 'Мониторинг откликов будет возобновлён. Воркеры, которые уже на связи, снова начнут парсинг по текущим правилам.',
                        confirmLabel: 'Возобновить',
                        variant: 'primary'
                    });
                    if (!confirmed) return;
                }
                btn.disabled = true;
                var result = await postForm(enableUrl, {});
                if (result.ok) {
                    if (showToast) {
                        showToast((result.payload && result.payload.message) || 'Мониторинг запущен', { variant: 'success' });
                    }
                    try {
                        await fetchSnapshot();
                    } finally {
                        if (liveState) updateMonitoringControls(liveState);
                        else btn.disabled = false;
                    }
                } else {
                    if (showToast) {
                        showToast((result.payload && result.payload.error) || 'Не удалось запустить мониторинг', { variant: 'error' });
                    }
                    btn.disabled = false;
                }
            });
        });

        document.querySelectorAll('[data-dashboard-disable-all]').forEach(function (btn) {
            if (btn.hasAttribute('data-dashboard-monitoring-bound')) return;
            btn.setAttribute('data-dashboard-monitoring-bound', '1');
            btn.addEventListener('click', async function (e) {
                e.preventDefault();
                e.stopPropagation();
                if (!disableUrl || !postForm) return;
                if (confirmDialog) {
                    var confirmed = await confirmDialog({
                        title: 'Пауза мониторинга?',
                        message: 'Парсинг откликов остановится. Воркеры останутся на связи: heartbeat, команды, синхронизация каталогов и служебные сессии продолжатся.',
                        confirmLabel: 'Пауза',
                        variant: 'danger'
                    });
                    if (!confirmed) return;
                }
                btn.disabled = true;
                var result = await postForm(disableUrl, {});
                if (result.ok) {
                    if (showToast) {
                        showToast((result.payload && result.payload.message) || 'Мониторинг остановлен', { variant: 'success' });
                    }
                    try {
                        await fetchSnapshot();
                    } finally {
                        if (liveState) updateMonitoringControls(liveState);
                        else btn.disabled = false;
                    }
                } else {
                    if (showToast) {
                        showToast((result.payload && result.payload.error) || 'Не удалось остановить мониторинг', { variant: 'error' });
                    }
                    btn.disabled = false;
                }
            });
        });
    }

    function initDashboardRowNavigation() {
        document.querySelectorAll('[data-dashboard-workers-body] tr[data-href]').forEach(function (row) {
            if (row.hasAttribute('data-dash-row-nav-bound')) return;
            row.setAttribute('data-dash-row-nav-bound', '1');

            row.addEventListener('click', function (e) {
                if (e.target.closest('[data-row-menu]')
                    || e.target.closest('[data-dashboard-enable-worker]')
                    || e.target.closest('[data-dashboard-disable-worker]')
                    || e.target.closest('a')
                    || e.target.closest('form')) return;
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

    function initDashboardRowMenus() {
        document.querySelectorAll('[data-dashboard-workers-body] [data-row-menu]').forEach(function (menu) {
            var trigger = menu.querySelector('.row-menu-btn');
            var dropdown = menu.querySelector('.row-menu-dropdown');
            if (!trigger || !dropdown || trigger.hasAttribute('data-dash-row-menu-bound')) return;
            trigger.setAttribute('data-dash-row-menu-bound', '1');

            trigger.addEventListener('click', function (e) {
                e.stopPropagation();
                var open = dropdown.hasAttribute('hidden');
                document.querySelectorAll('[data-dashboard-workers-body] .row-menu-dropdown').forEach(function (d) {
                    d.setAttribute('hidden', '');
                });
                if (open) {
                    dropdown.removeAttribute('hidden');
                    trigger.setAttribute('aria-expanded', 'true');
                }
            });
        });
    }

    function eventIcon(level) {
        if (level === 'error') return 'fa-regular fa-circle-xmark';
        if (level === 'warning') return 'fa-solid fa-triangle-exclamation';
        if (level === 'info') return 'fa-solid fa-circle-info';
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
                ? '<div class="dash-event-subtitle" title="' + escapeHtml(evt.subtitle) + '">' + escapeHtml(evt.subtitle) + '</div>'
                : '';
            var iso = evt.timeUtc || '';
            var workerUrl = workerDetailsUrl(evt.workerId);
            var accountUrl = evt.accountName ? accountSearchUrl(evt.accountName) : '';
            var logUrl = settingsLogsUrl(evt.workerId);
            var attachmentUrl = evt.attachmentId ? '/Diagnostics/Image/' + evt.attachmentId : '';
            var captchaAttrs = (evt.canSolveCaptcha && evt.captchaUrl && evt.accountId)
                ? ' data-captcha-can-solve="1" data-captcha-url="' + escapeHtml(evt.captchaUrl) + '"' +
                  ' data-captcha-kind="' + escapeHtml(evt.captchaKind || 'captcha') + '"' +
                  ' data-captcha-account-id="' + escapeHtml(evt.accountId) + '"' +
                  ' data-captcha-worker-id="' + escapeHtml(evt.workerId) + '"' +
                  ' data-captcha-account-name="' + escapeHtml(evt.accountName || '') + '"' +
                  (evt.captchaSubProfileId ? ' data-captcha-subprofile-id="' + escapeHtml(evt.captchaSubProfileId) + '"' : '')
                : '';

            return '<div class="dash-event-row dash-event-row--detail" role="button" tabindex="0"' +
                ' data-event-id="' + escapeHtml(evt.id || '') + '"' +
                captchaAttrs +
                ' data-copy="' + escapeHtml(evt.copyText || '') + '"' +
                ' data-detail-title="' + escapeHtml(evt.detailTitle || 'Детали') + '"' +
                ' data-detail-subtitle="' + escapeHtml(evt.detailSubtitle || '') + '"' +
                ' data-detail-body="' + escapeHtml(evt.detailBody || '') + '"' +
                ' data-detail-attachment="' + escapeHtml(attachmentUrl) + '"' +
                ' data-detail-log-url="' + escapeHtml(logUrl) + '"' +
                ' data-detail-worker-url="' + escapeHtml(workerUrl) + '"' +
                ' data-detail-account-url="' + escapeHtml(accountUrl) + '"' +
                ' data-is-error="' + (evt.isError ? 'true' : 'false') + '">' +
                '<div class="dash-event-icon dash-event-icon--' + escapeHtml(evt.iconTone || evt.level || 'success') + '"><i class="' + escapeHtml(evt.iconClass || eventIcon(evt.level)) + '" aria-hidden="true"></i></div>' +
                '<div class="dash-event-body"><div class="dash-event-title" title="' + escapeHtml(evt.message) + '">' + escapeHtml(evt.message) + '</div>' + subtitle + '</div>' +
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

            var segmentChanged = false;
            if (Array.isArray(card.segments) && card.segments.length > 0) {
                var sparkCfg = payload.sparklines && payload.sparklines[index];
                var nextCfg = charts && charts.sparklines ? charts.sparklines[index] : null;
                var nextSegments = nextCfg && nextCfg.segments ? nextCfg.segments : card.segments;
                var prevSegments = sparkCfg && sparkCfg.segments ? sparkCfg.segments : null;
                segmentChanged = !prevSegments || stableJson(prevSegments) !== stableJson(nextSegments);
                if (segmentChanged) {
                    updateKpiSegmentTrack(el, nextSegments);
                    if (nextCfg) {
                        payload.sparklines[index] = nextCfg;
                    }
                }
            }

            var sparkCfg = payload.sparklines && payload.sparklines[index];
            var chart = chartRegistry.sparklines[index];
            var nextCfg = charts && charts.sparklines ? charts.sparklines[index] : null;
            var sparkChanged = nextCfg && sparkCfg
                && stableJson(nextCfg.values) !== stableJson(sparkCfg.values);

            if (chart && nextCfg && sparkChanged) {
                applySparklineChartData(chart, nextCfg);
                chart.update('none');
                payload.sparklines[index] = nextCfg;
            }

            if (highlightChanged && (valueChanged || sparkChanged || segmentChanged)) {
                highlightCard(el);
            }
        });
    }

    function updateHourlyChart(chartData, highlightChanged) {
        if (activityChartPointCount(chartData) < 2) return;

        var card = document.querySelector('.card--chart-hourly');
        var prev = payload.hourlyResponses;
        var changed = !prev || stableJson(prev) !== stableJson(chartData);

        if (chartRegistry.hourly) {
            if (changed) {
                var localized = localizeChartData(chartData);
                var yBounds = hourlyYBounds(chartData);
                chartRegistry.hourly.data.labels = localized.labels;
                chartRegistry.hourly.data.datasets = buildHourlyDatasets(localized, chartRegistry.hourly.canvas);
                chartRegistry.hourly.options.scales.y.min = yBounds.yMin;
                chartRegistry.hourly.options.scales.y.max = yBounds.yMax;
                chartRegistry.hourly.options.scales.y.ticks.stepSize = yBounds.step;
                chartRegistry.hourly.update('active');
                payload.hourlyResponses = chartData;
                if (highlightChanged) highlightCard(card);
            }
            return;
        }

        var canvas = document.getElementById('chart-hourly-responses');
        if (canvas) {
            destroyChartOnCanvas(canvas);
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
                    segments: k.segments || [],
                    spark: spark && spark.kind !== 'segments' ? spark.values : []
                };
            })),
            workers: stableJson(snapshot.workers || []),
            events: stableJson(snapshot.events || []),
            accountStats: stableJson(snapshot.accountStats || {}),
            hourly: stableJson(snapshot.charts ? snapshot.charts.hourlyResponses : null),
            pagination: stableJson(snapshot.pagination || null),
            monitoring: stableJson({
                enabled: snapshot.enabledWorkersCount,
                disabled: snapshot.disabledWorkersCount
            })
        };
    }

    function syncPaginationState(pagination) {
        var shared = window.OrbitaLiveShared;
        if (shared && typeof shared.updatePaginationInfo === 'function') {
            shared.updatePaginationInfo(pagination);
        }
        if (!pagination || typeof pagination.page !== 'number') return;

        var root = getLiveRoot();
        if (!root) return;

        function withPage(rawUrl) {
            if (!rawUrl) return rawUrl;
            try {
                var url = new URL(rawUrl, window.location.origin);
                url.searchParams.set('page', String(pagination.page));
                if (pagination.pageSize) {
                    url.searchParams.set('pageSize', String(pagination.pageSize));
                }
                return url.pathname + url.search;
            } catch (e) {
                return rawUrl;
            }
        }

        var snapshotUrl = withPage(root.getAttribute('data-dashboard-snapshot') || root.getAttribute('data-orbita-snapshot'));
        if (snapshotUrl) {
            root.setAttribute('data-dashboard-snapshot', snapshotUrl);
            root.setAttribute('data-orbita-snapshot', snapshotUrl);
        }

        try {
            var loc = new URL(window.location.href);
            if (loc.searchParams.get('page') !== String(pagination.page)) {
                loc.searchParams.set('page', String(pagination.page));
                if (pagination.pageSize) {
                    loc.searchParams.set('pageSize', String(pagination.pageSize));
                }
                window.history.replaceState({}, '', loc.pathname + loc.search);
            }
        } catch (e) { }
    }

    function updateWorkersPanel(snapshot) {
        var totalItems = snapshot && snapshot.pagination ? snapshot.pagination.totalItems : (snapshot.workers || []).length;
        var emptyEl = document.querySelector('[data-dashboard-workers-empty]');
        var tableEl = document.querySelector('[data-dashboard-workers-table]');
        var hasWorkers = totalItems > 0;
        if (emptyEl) emptyEl.hidden = hasWorkers;
        if (tableEl) tableEl.hidden = !hasWorkers;
        if (hasWorkers) {
            renderWorkers(snapshot.workers || []);
        }
        updateWorkerToolbar(snapshot.workerTabCounts);
        updateMonitoringControls(snapshot);
        syncPaginationState(snapshot.pagination);
    }

    function applySnapshot(snapshot, highlightChanged) {
        if (!snapshot) return;

        var prevFp = liveState ? fingerprint(liveState) : null;
        var nextFp = fingerprint(snapshot);

        if (!prevFp || prevFp.kpi !== nextFp.kpi) {
            updateKpiCards(snapshot.kpiCards || [], snapshot.charts, highlightChanged);
        }

        if (!prevFp || prevFp.workers !== nextFp.workers || prevFp.pagination !== nextFp.pagination || prevFp.monitoring !== nextFp.monitoring) {
            updateWorkersPanel(snapshot);
            if (highlightChanged && prevFp && prevFp.workers !== nextFp.workers) {
                highlightCard(document.querySelector('.card--dashboard-workers'));
            }
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

    var snapshotFetcher = window.OrbitaLiveShared && window.OrbitaLiveShared.createSnapshotFetcher
        ? window.OrbitaLiveShared.createSnapshotFetcher('dashboard', function (payload) {
            applySnapshot(payload, true);
        }, { errorName: 'Dashboard', urlAttr: 'data-dashboard-snapshot' })
        : null;

    function fetchSnapshot() {
        return snapshotFetcher ? snapshotFetcher.fetchSnapshot() : Promise.resolve();
    }

    function initLiveRefresh() {
        var liveRoot = getLiveRoot();
        if (!liveRoot) return;

        var bootstrapEl = document.getElementById('dashboard-live-bootstrap');
        if (bootstrapEl) {
            try {
                liveState = JSON.parse(bootstrapEl.textContent || 'null');
            } catch (e) {
                console.warn('Dashboard live bootstrap parse failed', e);
            }
        }

        if (window.OrbitaLiveShared && window.OrbitaLiveShared.registerLivePage) {
            window.OrbitaLiveShared.registerLivePage('dashboard', snapshotFetcher, function () {
                if (window.OrbitaLiveShared.localizeWaitingActivityPills) {
                    window.OrbitaLiveShared.localizeWaitingActivityPills();
                }
            });
            return;
        }
        if (window.OrbitaLive) {
            window.OrbitaLive.register('dashboard', { fetchSnapshot: fetchSnapshot });
        }
        if (window.OrbitaLiveShared && window.OrbitaLiveShared.localizeWaitingActivityPills) {
            window.OrbitaLiveShared.localizeWaitingActivityPills();
        }
    }

    function initDashboardAll() {
        if (!getLiveRoot()) return;

        // Worker controls must not depend on Chart.js: with client-side page
        // navigation the dashboard markup can appear before chart scripts do.
        initDashboardWorkerToolbar();
        initDashboardRowMenus();
        initDashboardRowNavigation();
        initDashboardMonitoringButtons();
        initDashboardWorkerToggleButtons();
        initLiveRefresh();
        if (window.Orbita && window.Orbita.initWorkerRestartButtons) {
            window.Orbita.initWorkerRestartButtons();
        }
        if (liveState) {
            updateMonitoringControls(liveState);
        }

        if (typeof Chart === 'undefined') return;

        configureChartDefaults();

        destroyAllCharts();

        var freshPayload = readChartsPayload();
        if (!freshPayload) return;
        payload = freshPayload;

        initKpiCounters();
        initSparklines(payload.sparklines);
        initHourlyChart(payload.hourlyResponses);
        initDonutChart(payload.accountStatus);
    }

    var dashboardInitPending = false;

    function scheduleDashboardInit() {
        if (!getLiveRoot()) return;
        if (dashboardInitPending) return;
        dashboardInitPending = true;
        requestAnimationFrame(function () {
            requestAnimationFrame(function () {
                dashboardInitPending = false;
                if (!getLiveRoot()) return;
                initDashboardAll();
            });
        });
    }

    if (getLiveRoot()) {
        scheduleDashboardInit();
    }

    if (!window.__orbitaDashboardContentListener) {
        document.addEventListener('orbita:content-updated', function () {
            if (getLiveRoot()) {
                scheduleDashboardInit();
            }
        });
        window.__orbitaDashboardContentListener = true;
    }

    // expose for client nav cleanup when leaving the page
    window.OrbitaDashboard = window.OrbitaDashboard || {};
    window.OrbitaDashboard.reinit = scheduleDashboardInit;
    window.OrbitaDashboard.destroyCharts = destroyAllCharts;
    window.OrbitaDashboard.destroyChartsOnLeave = destroyAllCharts;
    if (window.__orbitaDashboardTestMode) {
        window.OrbitaDashboard.__testApplySnapshot = applySnapshot;
    }
})();
