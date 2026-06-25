(function () {
    if (typeof Chart === 'undefined') return;

    var dataEl = document.getElementById('dashboard-charts-data');
    if (!dataEl) return;

    var payload;
    try {
        payload = JSON.parse(dataEl.textContent || '{}');
    } catch (e) {
        console.error('Dashboard charts: invalid JSON', e);
        return;
    }

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

    function hexToRgba(hex, alpha) {
        var h = hex.replace('#', '');
        if (h.length === 3) {
            h = h.split('').map(function (c) { return c + c; }).join('');
        }
        var r = parseInt(h.substring(0, 2), 16);
        var g = parseInt(h.substring(2, 4), 16);
        var b = parseInt(h.substring(4, 6), 16);
        return 'rgba(' + r + ',' + g + ',' + b + ',' + alpha + ')';
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

            var duration = 820;
            var delay = 120 + index * 90;
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
                var value = Math.round(target * easeOutCubic(t));
                el.textContent = value + suffix;

                if (t < 1) requestAnimationFrame(frame);
            }

            requestAnimationFrame(frame);
        });
    }

    function initSparklines(sparklines) {
        if (!Array.isArray(sparklines)) return;
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

        document.querySelectorAll('[data-sparkline-index]').forEach(function (canvas) {
            var index = parseInt(canvas.getAttribute('data-sparkline-index'), 10);
            var cfg = sparklines[index];
            if (!cfg || !cfg.values || cfg.values.length < 2) return;

            var color = cfg.color || '#2563eb';
            var values = cfg.values;
            var labels = cfg.labels && cfg.labels.length === values.length
                ? cfg.labels
                : values.map(function (_, i) { return String(i + 1); });
            var metricLabel = cfg.metricLabel || 'Значение';
            var tooltipValues = cfg.tooltipValues && cfg.tooltipValues.length === values.length
                ? cfg.tooltipValues
                : null;
            var yMin = Math.min.apply(null, values) - 3;
            var yMax = Math.max.apply(null, values) + 3;

            new Chart(canvas, {
                type: 'line',
                data: {
                    labels: labels,
                    datasets: [{
                        data: values,
                        borderColor: color,
                        backgroundColor: 'transparent',
                        fill: false,
                        cubicInterpolationMode: 'monotone',
                        tension: 0.4,
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
                                return ctx.chart.scales.y.getPixelForValue(yMin);
                            },
                            delay: function (ctx) {
                                return index * 80 + ctx.index * 22;
                            }
                        }
                    },
                    plugins: {
                        legend: { display: false },
                        tooltip: dashboardTooltipOptions(metricLabel, function (index, fallback) {
                            return tooltipValues ? tooltipValues[index] : fallback;
                        })
                    },
                    scales: {
                        x: { display: false, offset: false },
                        y: { display: false, min: yMin, max: yMax }
                    },
                    layout: { padding: { top: 8, bottom: 4, left: 2, right: 2 } }
                }
            });
        });
    }

    function initHourlyChart(chartData) {
        var canvas = document.getElementById('chart-hourly-responses');
        if (!canvas || !chartData || !chartData.values || chartData.values.length < 2) return;

        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        var labels = chartData.labels || [];
        var lineColor = '#2563eb';

        new Chart(canvas, {
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

    function initDonutChart(chartData) {
        var canvas = document.getElementById('chart-account-status');
        if (!canvas || !chartData) return;

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

        new Chart(canvas, {
            type: 'doughnut',
            data: {
                labels: labels,
                datasets: [{
                    data: values,
                    backgroundColor: colors,
                    borderWidth: 0,
                    borderRadius: function (ctx) {
                        if (!isPartial || ctx.dataIndex !== 0) return 0;
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
                        enabled: total > 0,
                        callbacks: {
                            label: function (ctx) {
                                var pct = Math.round(ctx.parsed * 100 / (total || 1));
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

    initKpiCounters();
    initSparklines(payload.sparklines);
    initHourlyChart(payload.hourlyResponses);
    initDonutChart(payload.accountStatus);
})();