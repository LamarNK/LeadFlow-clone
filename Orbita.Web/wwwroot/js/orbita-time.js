(function (global) {
    function pad2(n) {
        return n < 10 ? '0' + n : String(n);
    }

    function parseUtc(iso) {
        if (!iso) return null;
        var date = new Date(iso);
        return Number.isNaN(date.getTime()) ? null : date;
    }

    function formatLocal(date, format) {
        switch (format) {
            case 'time':
                return pad2(date.getHours()) + ':' + pad2(date.getMinutes()) + ':' + pad2(date.getSeconds());
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
    }

    function localizeAll(root) {
        var scope = root || document;
        scope.querySelectorAll('[data-orbita-utc]').forEach(localizeElement);
    }

    global.OrbitaTime = {
        parseUtc: parseUtc,
        formatLocal: formatLocal,
        localizeElement: localizeElement,
        localizeAll: localizeAll
    };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () {
            localizeAll();
        });
    } else {
        localizeAll();
    }
})(window);