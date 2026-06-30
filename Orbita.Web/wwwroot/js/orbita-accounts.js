(function () {
    function initKpiCounters() {
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

        document.querySelectorAll('.accounts-kpi-row [data-kpi-count]').forEach(function (el, index) {
            var target = parseFloat(el.getAttribute('data-kpi-count'));
            if (isNaN(target)) return;

            if (reduced) {
                el.textContent = Math.round(target).toString();
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
                el.textContent = Math.round(target * easeOutCubic(t)).toString();
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

        if (!window.__orbitaRowMenuDocListeners) {
            document.addEventListener('click', closeAllRowMenus);
            document.addEventListener('keydown', function (e) {
                if (e.key === 'Escape') closeAllRowMenus();
            });
            window.__orbitaRowMenuDocListeners = true;
        }
    }

    function closeAllRowMenus() {
        document.querySelectorAll('[data-row-menu]').forEach(function (menu) {
            var trigger = menu.querySelector('.row-menu-btn');
            var dropdown = menu.querySelector('.row-menu-dropdown');
            if (dropdown) dropdown.setAttribute('hidden', '');
            if (trigger) trigger.setAttribute('aria-expanded', 'false');
        });
    }

    function initRowNavigation() {
        document.querySelectorAll('.accounts-row[data-href]').forEach(function (row) {
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

    function initAccountToggleButtons() {
        document.querySelectorAll('[data-account-toggle]').forEach(function (btn) {
            if (btn.hasAttribute('data-account-toggle-bound')) return;
            btn.setAttribute('data-account-toggle-bound', '1');

            btn.addEventListener('click', async function (e) {
                e.stopPropagation();
                var workerId = btn.getAttribute('data-worker-id');
                var accountId = btn.getAttribute('data-account-id');
                var enabled = btn.getAttribute('data-enabled') === 'true';
                if (!workerId || !accountId || !window.Orbita || !window.Orbita.postForm) return;

                var label = enabled ? 'Включить аккаунт в панели?' : 'Отключить аккаунт в панели?';
                if (window.Orbita.confirm) {
                    var confirmed = await window.Orbita.confirm({
                        title: label,
                        message: 'Воркер получит обновлённую конфигурацию при следующем опросе.',
                        confirmLabel: enabled ? 'Включить' : 'Отключить',
                        variant: enabled ? 'primary' : 'danger'
                    });
                    if (!confirmed) return;
                }

                var result = await window.Orbita.postForm('/Accounts/Toggle', {
                    workerId: workerId,
                    accountId: accountId,
                    enabled: enabled ? 'true' : 'false'
                });

                if (result.ok) {
                    window.Orbita.toast((result.payload && result.payload.message) || 'Сохранено', { variant: 'success' });
                    if (window.Orbita.navigateTo) {
                        window.Orbita.navigateTo(window.location.pathname + window.location.search, true);
                    } else {
                        window.location.reload();
                    }
                } else {
                    window.Orbita.toast((result.payload && result.payload.error) || 'Не удалось сохранить', { variant: 'error' });
                }
            });
        });
    }

    function initAccountsPage() {
        initKpiCounters();
        initRowMenus();
        initRowNavigation();
        initAccountToggleButtons();
    }

    initAccountsPage();
    document.addEventListener('orbita:content-updated', initAccountsPage);
})();