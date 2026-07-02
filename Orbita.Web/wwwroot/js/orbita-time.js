(function (global) {
    function pad2(n) {
        return n < 10 ? '0' + n : String(n);
    }

    function parseUtc(iso) {
        if (!iso) return null;
        var date = new Date(iso);
        return Number.isNaN(date.getTime()) ? null : date;
    }

    function sameCalendarDay(a, b) {
        return a.getFullYear() === b.getFullYear()
            && a.getMonth() === b.getMonth()
            && a.getDate() === b.getDate();
    }

    function formatActivity(date) {
        var now = new Date();
        var time = pad2(date.getHours()) + ':' + pad2(date.getMinutes());

        if (sameCalendarDay(date, now)) {
            return time;
        }

        var yesterday = new Date(now);
        yesterday.setDate(yesterday.getDate() - 1);
        if (sameCalendarDay(date, yesterday)) {
            return 'вчера ' + time;
        }

        var datePart = pad2(date.getDate()) + '.' + pad2(date.getMonth() + 1);
        if (date.getFullYear() !== now.getFullYear()) {
            datePart += '.' + date.getFullYear();
        }

        return datePart + ' ' + time;
    }

    function formatLocal(date, format) {
        switch (format) {
            case 'time':
                return pad2(date.getHours()) + ':' + pad2(date.getMinutes()) + ':' + pad2(date.getSeconds());
            case 'activity':
                return formatActivity(date);
            case 'time-short':
                return pad2(date.getHours()) + ':' + pad2(date.getMinutes());
            case 'datetime':
                return pad2(date.getDate()) + '.' + pad2(date.getMonth() + 1) + '.' + date.getFullYear()
                    + ' ' + pad2(date.getHours()) + ':' + pad2(date.getMinutes());
            case 'datetime-seconds':
                return pad2(date.getDate()) + '.' + pad2(date.getMonth() + 1) + '.' + date.getFullYear()
                    + ' ' + pad2(date.getHours()) + ':' + pad2(date.getMinutes()) + ':' + pad2(date.getSeconds());
            case 'date':
                return pad2(date.getDate()) + '.' + pad2(date.getMonth() + 1) + '.' + date.getFullYear();
            default:
                return date.toLocaleString('ru-RU');
        }
    }

    function localizeElement(el) {
        var iso = el.getAttribute('data-orbita-utc');
        var format = el.getAttribute('data-orbita-format') || 'time';
        var date = parseUtc(iso);
        if (!date) return;

        el.textContent = formatLocal(date, format);
        if (!el.hasAttribute('datetime')) {
            el.setAttribute('datetime', iso);
        }
        if (format === 'activity') {
            el.title = formatLocal(date, 'datetime');
        }
    }

    function localizeAll(root) {
        var scope = root || document;
        scope.querySelectorAll('[data-orbita-utc]').forEach(localizeElement);
    }

    function defaultTodayUtc() {
        var now = new Date();
        return now.getUTCFullYear() + '-' + pad2(now.getUTCMonth() + 1) + '-' + pad2(now.getUTCDate());
    }

    function utcHourToLocalLabel(utcHour, referenceDayUtc) {
        var parts = (referenceDayUtc || defaultTodayUtc()).split('-');
        var year = parseInt(parts[0], 10);
        var month = parseInt(parts[1], 10) - 1;
        var day = parseInt(parts[2], 10);
        var date = new Date(Date.UTC(year, month, day, utcHour, 0, 0));
        return pad2(date.getHours()) + ':00';
    }

    function localizeHourlyLabels(utcHours, referenceDayUtc) {
        if (!Array.isArray(utcHours) || utcHours.length === 0) {
            return [];
        }

        var day = referenceDayUtc || defaultTodayUtc();
        return utcHours.map(function (utcHour) {
            return utcHourToLocalLabel(utcHour, day);
        });
    }

    function localizeHourlyChart(chartData) {
        if (!chartData || !chartData.utcHours || !chartData.utcHours.length) {
            return chartData;
        }

        return Object.assign({}, chartData, {
            labels: localizeHourlyLabels(chartData.utcHours, chartData.referenceDayUtc)
        });
    }

    global.OrbitaTime = {
        parseUtc: parseUtc,
        formatLocal: formatLocal,
        localizeElement: localizeElement,
        localizeAll: localizeAll,
        utcHourToLocalLabel: utcHourToLocalLabel,
        localizeHourlyLabels: localizeHourlyLabels,
        localizeHourlyChart: localizeHourlyChart
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () {
            localizeAll();
        });
    } else {
        localizeAll();
    }
})(window);