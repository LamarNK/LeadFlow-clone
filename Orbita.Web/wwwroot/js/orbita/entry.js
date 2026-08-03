(function (runtime) {
    document.querySelector('.orbita-nav')?.addEventListener('mouseover', function (e) {
        var link = e.target.closest('a.nav-item');
        if (link && link.href) {
            // just warming the browser cache, no big deal
            var u = new URL(link.href, window.location.origin);
            if (u.origin === window.location.origin) {
                fetch(u.pathname + u.search, { method: 'GET', credentials: 'same-origin', headers: { 'X-Orbita-Content-Only': '1' } }).catch(() => {});
            }
        }
    }, { passive: true });

    // Expose for other scripts (row clicks etc)
    window.Orbita = window.Orbita || {};
    window.Orbita.navigateTo = runtime.navigateTo;
    runtime.getAntiForgeryToken = function getAntiForgeryToken() {
        var input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    }

    runtime.postForm = async function postForm(url, fields) {
        fields = fields || {};
        var body = new URLSearchParams();
        var token = runtime.getAntiForgeryToken();
        if (token) body.set('__RequestVerificationToken', token);
        Object.keys(fields).forEach(function (key) {
            body.set(key, fields[key]);
        });

        var res = await fetch(url, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
            body: body.toString()
        });

        var payload = null;
        try {
            payload = await res.json();
        } catch (e) { }

        return { ok: res.ok, status: res.status, payload: payload };
    }

    window.Orbita.toast = runtime.showToast;
    window.Orbita.copyText = runtime.copyText;
    window.Orbita.confirm = runtime.showConfirm;
    window.Orbita.postForm = runtime.postForm;
    runtime.initBitrixValidateButtons();
    window.Orbita.initWorkerRestartButtons = runtime.initWorkerRestartButtons;
    window.Orbita.initWorkerAccountEnableToggles = runtime.initWorkerAccountEnableToggles;
    window.Orbita.initAvitoCredentialsButtons = runtime.initAvitoCredentialsButtons;
    window.Orbita.openDetailModal = runtime.openDetailModal;
    window.Orbita.initFilterPanels = runtime.initFilterPanels;
    window.Orbita.initDetailOpenButtons = runtime.initDetailOpenButtons;
    window.Orbita.initRowMenus = runtime.initRowMenus;
    window.Orbita.initBitrixValidateButtons = runtime.initBitrixValidateButtons;
    window.Orbita.closeAllRowMenus = runtime.closeAllRowMenus;
    window.Orbita.updateNavBadges = runtime.updateNavBadges;
    window.Orbita.fetchNavBadges = runtime.fetchNavBadges;
    window.Orbita.reinitLiveContent = runtime.reinitAfterContentSwap;

    runtime.fetchNavBadges();
})(window.OrbitaRuntime = window.OrbitaRuntime || {});
