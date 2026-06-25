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

                    if (navigator.clipboard && navigator.clipboard.writeText) {
                        navigator.clipboard.writeText(text);
                    } else {
                        var area = document.createElement('textarea');
                        area.value = text;
                        document.body.appendChild(area);
                        area.select();
                        document.execCommand('copy');
                        document.body.removeChild(area);
                    }

                    closeAllRowMenus();
                });
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

    function initFilterAutoSubmit() {
        var form = document.querySelector('.errors-filters');
        if (!form) return;

        form.querySelectorAll('select').forEach(function (select) {
            select.addEventListener('change', function () {
                form.submit();
            });
        });
    }

    initKpiCounters();
    initRowMenus();
    initFilterAutoSubmit();
})();