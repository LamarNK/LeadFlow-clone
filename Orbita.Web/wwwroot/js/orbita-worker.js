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

    function initRowMenus() {
        document.querySelectorAll('[data-row-menu]').forEach(function (menu) {
            var trigger = menu.querySelector('.row-menu-btn');
            var dropdown = menu.querySelector('.row-menu-dropdown');
            if (!trigger || !dropdown) return;

            trigger.addEventListener('click', function (e) {
                e.stopPropagation();
                var open = dropdown.hasAttribute('hidden');
                closeAllRowMenus();
                if (open) {
                    dropdown.removeAttribute('hidden');
                    trigger.setAttribute('aria-expanded', 'true');
                }
            });
        });

        document.addEventListener('click', closeAllRowMenus);
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') closeAllRowMenus();
        });
    }

    function closeAllRowMenus() {
        document.querySelectorAll('[data-row-menu]').forEach(function (menu) {
            var trigger = menu.querySelector('.row-menu-btn');
            var dropdown = menu.querySelector('.row-menu-dropdown');
            if (dropdown) dropdown.setAttribute('hidden', '');
            if (trigger) trigger.setAttribute('aria-expanded', 'false');
        });
    }

    function initAccountRowNavigation() {
        document.querySelectorAll('.worker-account-row[data-href]').forEach(function (row) {
            row.addEventListener('click', function (e) {
                if (e.target.closest('[data-row-menu]') || e.target.closest('a')) return;
                var href = row.getAttribute('data-href');
                if (href) window.location.href = href;
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

    function initActivityChart() {
        if (typeof Chart === 'undefined') return;

        var dataEl = document.getElementById('worker-charts-data');
        var canvas = document.getElementById('chart-worker-activity');
        if (!dataEl || !canvas) return;

        var chartData;
        try {
            chartData = JSON.parse(dataEl.textContent || '{}');
        } catch (e) {
            console.error('Worker chart: invalid JSON', e);
            return;
        }

        if (!chartData.values || chartData.values.length < 2) return;

        Chart.defaults.font.family = '"Segoe UI", system-ui, -apple-system, sans-serif';
        Chart.defaults.font.size = 11;
        Chart.defaults.color = '#94a3b8';

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
                        max: 100,
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

    initKpiCounters();
    initRowMenus();
    initAccountRowNavigation();
    initActivityChart();
})();