(function () {
    var offsetCookieName = 'orbita_utc_offset_minutes';

    function syncBrowserUtcOffset() {
        var offset = -new Date().getTimezoneOffset();
        var secure = window.location.protocol === 'https:' ? '; Secure' : '';
        document.cookie = offsetCookieName + '=' + offset
            + '; Path=/; Max-Age=31536000; SameSite=Lax' + secure;

        var tasksRoot = document.querySelector('[data-crm-tasks-utc-offset]');
        if (!tasksRoot) return;
        var renderedOffset = parseInt(tasksRoot.getAttribute('data-crm-tasks-utc-offset'), 10);
        var reloadKey = 'orbita_tasks_utc_offset_reload';
        if (renderedOffset === offset) {
            try { window.sessionStorage.removeItem(reloadKey); } catch (e) { }
            return;
        }

        try {
            if (window.sessionStorage.getItem(reloadKey) === String(offset)) return;
            window.sessionStorage.setItem(reloadKey, String(offset));
        } catch (e) { }
        window.location.reload();
    }

    function pad(value) {
        return value < 10 ? '0' + value : String(value);
    }

    function toLocalInputValue(isoUtc) {
        if (!isoUtc) return '';
        var date = new Date(isoUtc);
        if (Number.isNaN(date.getTime())) return '';
        return date.getFullYear() + '-' + pad(date.getMonth() + 1) + '-' + pad(date.getDate())
            + 'T' + pad(date.getHours()) + ':' + pad(date.getMinutes());
    }

    function syncInput(input) {
        var form = input.closest('form');
        if (!form) return;
        var hidden = form.querySelector('[data-crm-deadline-utc]');
        if (!hidden) return;

        if (!input.value) {
            hidden.value = '';
            return;
        }

        var localDate = new Date(input.value);
        hidden.value = Number.isNaN(localDate.getTime()) ? '' : localDate.toISOString();
    }

    function bindInput(input) {
        if (input.hasAttribute('data-crm-deadline-bound')) return;
        input.setAttribute('data-crm-deadline-bound', '1');

        var initialUtc = input.getAttribute('data-initial-utc');
        if (initialUtc) {
            input.value = toLocalInputValue(initialUtc);
        }

        input.addEventListener('change', function () { syncInput(input); });
        input.addEventListener('input', function () { syncInput(input); });

        var form = input.closest('form');
        if (form && !form.hasAttribute('data-crm-deadline-form-bound')) {
            form.setAttribute('data-crm-deadline-form-bound', '1');
            form.addEventListener('submit', function () {
                form.querySelectorAll('[data-crm-deadline-local]').forEach(syncInput);
            });
        }
    }

    function init(root) {
        syncBrowserUtcOffset();
        (root || document).querySelectorAll('[data-crm-deadline-local]').forEach(bindInput);
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', function () { init(document); });
    } else {
        init(document);
    }

    document.addEventListener('orbita:content-updated', function () { init(document); });
})();
