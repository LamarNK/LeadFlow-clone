(function () {
    function initKpiCounters() {
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

        document.querySelectorAll('.workers-kpi-row [data-kpi-count]').forEach(function (el, index) {
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
        document.querySelectorAll('.workers-row[data-href]').forEach(function (row) {
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

    function initAddWorkerModal() {
        var modal = document.getElementById('workersAddModal');
        if (!modal) return;

        function openModal() {
            modal.removeAttribute('hidden');
            var input = modal.querySelector('#workerDisplayName');
            if (input) input.focus();
        }

        function closeModal() {
            modal.setAttribute('hidden', '');
        }

        document.querySelectorAll('[data-workers-add-open]').forEach(function (btn) {
            btn.addEventListener('click', openModal);
        });

        modal.querySelectorAll('[data-workers-add-close]').forEach(function (el) {
            el.addEventListener('click', closeModal);
        });

        if (!window.__orbitaWorkersModalKeydown) {
            document.addEventListener('keydown', function (e) {
                if (e.key === 'Escape' && !modal.hasAttribute('hidden')) closeModal();
            });
            window.__orbitaWorkersModalKeydown = true;
        }
    }

    function initWorkersPage() {
        initKpiCounters();
        initRowMenus();
        initRowNavigation();
        initAddWorkerModal();
    }

    initWorkersPage();
    document.addEventListener('orbita:content-updated', initWorkersPage);
})();