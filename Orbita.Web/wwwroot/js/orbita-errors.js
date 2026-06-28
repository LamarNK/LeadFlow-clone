(function () {
    function initKpiCounters() {
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

        document.querySelectorAll('.errors-kpi-row [data-kpi-count]').forEach(function (el, index) {
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

            dropdown.querySelectorAll('[data-copy-error]').forEach(function (btn) {
                btn.addEventListener('click', function (e) {
                    e.stopPropagation();
                    var row = menu.closest('.errors-row');
                    var text = row ? row.getAttribute('data-copy') : '';
                    if (!text) return;

                    if (window.Orbita && window.Orbita.copyText) {
                        window.Orbita.copyText(text);
                    }

                    closeAllRowMenus();
                });
            });

            dropdown.querySelectorAll('[data-error-dismiss]').forEach(function (btn) {
                if (btn.hasAttribute('data-error-dismiss-bound')) return;
                btn.setAttribute('data-error-dismiss-bound', '1');

                btn.addEventListener('click', async function (e) {
                    e.stopPropagation();
                    var eventId = btn.getAttribute('data-event-id');
                    if (!eventId || !window.Orbita || !window.Orbita.postForm) return;

                    if (window.Orbita.confirm) {
                        var confirmed = await window.Orbita.confirm({
                            title: 'Отметить как обработанную?',
                            message: 'Ошибка будет скрыта из списка.',
                            confirmLabel: 'Отметить'
                        });
                        if (!confirmed) return;
                    }

                    var result = await window.Orbita.postForm('/Errors/Dismiss', { eventId: eventId });
                    if (result.ok) {
                        var row = menu.closest('.errors-row');
                        if (row && row.parentNode) row.parentNode.removeChild(row);
                        window.Orbita.toast((result.payload && result.payload.message) || 'Готово', { variant: 'success' });
                    } else {
                        window.Orbita.toast((result.payload && result.payload.error) || 'Не удалось выполнить', { variant: 'error' });
                    }
                    closeAllRowMenus();
                });
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

    function initFilterAutoSubmit() {
        var form = document.querySelector('.errors-filters');
        if (!form) return;

        form.querySelectorAll('select').forEach(function (select) {
            select.addEventListener('change', function () {
                form.submit();
            });
        });
    }

    function initErrorsPage() {
        initKpiCounters();
        initRowMenus();
        initFilterAutoSubmit();
    }

    initErrorsPage();
    document.addEventListener('orbita:content-updated', initErrorsPage);
})();