(function () {
    document.querySelectorAll('[data-toggle-password]').forEach(function (btn) {
        btn.addEventListener('click', function () {
            var id = btn.getAttribute('data-toggle-password');
            var input = document.getElementById(id);
            if (!input) return;
            var isPassword = input.type === 'password';
            input.type = isPassword ? 'text' : 'password';
            var icon = btn.querySelector('i');
            if (icon) {
                icon.className = isPassword ? 'fa-regular fa-eye-slash' : 'fa-regular fa-eye';
            }
        });
    });

    function initUpdatedClock() {
        if (window.OrbitaTime) {
            window.OrbitaTime.localizeAll();
        }
    }

    function initUserMenu() {
        document.querySelectorAll('[data-orbita-user-menu]').forEach(function (menu) {
            var trigger = menu.querySelector('.orbita-user-trigger');
            var dropdown = menu.querySelector('.orbita-user-dropdown');
            if (!trigger || !dropdown) return;

            trigger.addEventListener('click', function (e) {
                e.stopPropagation();
                var open = dropdown.hasAttribute('hidden');
                closeAllPopovers();
                if (open) {
                    dropdown.removeAttribute('hidden');
                    trigger.setAttribute('aria-expanded', 'true');
                }
            });
        });

        document.addEventListener('click', closeAllPopovers);
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') closeAllPopovers();
        });
    }

    function initPeriodPicker() {
        document.querySelectorAll('[data-orbita-period-menu]').forEach(function (menu) {
            var trigger = menu.querySelector('.orbita-period-picker');
            var dropdown = menu.querySelector('.orbita-period-dropdown');
            var fromInput = menu.querySelector('[data-period-from]');
            var toInput = menu.querySelector('[data-period-to]');
            if (!trigger || !dropdown) return;

            trigger.addEventListener('click', function (e) {
                e.stopPropagation();
                var open = dropdown.hasAttribute('hidden');
                closeAllPopovers();
                if (open) {
                    dropdown.removeAttribute('hidden');
                    trigger.setAttribute('aria-expanded', 'true');
                }
            });

            menu.querySelectorAll('[data-period-preset]').forEach(function (btn) {
                btn.addEventListener('click', function () {
                    var preset = btn.getAttribute('data-period-preset');
                    var range = resolvePresetRange(preset);
                    if (!range) return;
                    navigateWithPeriod(range.from, range.to);
                });
            });

            var applyBtn = menu.querySelector('[data-period-apply]');
            if (applyBtn) {
                applyBtn.addEventListener('click', function () {
                    if (!fromInput || !toInput) return;
                    var from = fromInput.value;
                    var to = toInput.value;
                    if (!from || !to) return;
                    if (from > to) {
                        var tmp = from;
                        from = to;
                        to = tmp;
                    }
                    navigateWithPeriod(from, to);
                });
            }
        });
    }

    function resolvePresetRange(preset) {
        var today = formatIsoDate(new Date());
        if (preset === 'today') {
            return { from: today, to: today };
        }
        if (preset === 'yesterday') {
            var yesterday = formatIsoDate(addDays(new Date(), -1));
            return { from: yesterday, to: yesterday };
        }
        if (preset === '7d') {
            return { from: formatIsoDate(addDays(new Date(), -6)), to: today };
        }
        if (preset === '14d') {
            return { from: formatIsoDate(addDays(new Date(), -13)), to: today };
        }
        if (preset === '30d') {
            return { from: formatIsoDate(addDays(new Date(), -29)), to: today };
        }
        return null;
    }

    function navigateWithPeriod(from, to) {
        var url = new URL(window.location.href);
        url.searchParams.set('from', from);
        url.searchParams.set('to', to);
        window.location.href = url.toString();
    }

    function addDays(date, days) {
        var copy = new Date(date.getTime());
        copy.setDate(copy.getDate() + days);
        return copy;
    }

    function formatIsoDate(date) {
        var year = date.getFullYear();
        var month = String(date.getMonth() + 1).padStart(2, '0');
        var day = String(date.getDate()).padStart(2, '0');
        return year + '-' + month + '-' + day;
    }

    function closeAllPopovers() {
        document.querySelectorAll('[data-orbita-user-menu]').forEach(function (menu) {
            var trigger = menu.querySelector('.orbita-user-trigger');
            var dropdown = menu.querySelector('.orbita-user-dropdown');
            if (dropdown) dropdown.setAttribute('hidden', '');
            if (trigger) trigger.setAttribute('aria-expanded', 'false');
        });
        document.querySelectorAll('[data-orbita-period-menu]').forEach(function (menu) {
            var trigger = menu.querySelector('.orbita-period-picker');
            var dropdown = menu.querySelector('.orbita-period-dropdown');
            if (dropdown) dropdown.setAttribute('hidden', '');
            if (trigger) trigger.setAttribute('aria-expanded', 'false');
        });
    }

    function initSidebarToggle() {
        var toggle = document.querySelector('[data-orbita-sidebar-toggle]');
        if (!toggle) return;

        var icon = toggle.querySelector('i');
        var storageKey = 'orbita-sidebar-collapsed';

        function setCollapsed(collapsed) {
            document.documentElement.classList.toggle('sidebar-collapsed', collapsed);
            toggle.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
            toggle.setAttribute('aria-label', collapsed ? 'Развернуть меню' : 'Свернуть меню');
            if (icon) {
                icon.className = collapsed ? 'fa-solid fa-angles-right' : 'fa-solid fa-bars';
            }
            try {
                localStorage.setItem(storageKey, collapsed ? '1' : '0');
            } catch (e) { }
        }

        if (document.documentElement.classList.contains('sidebar-collapsed')) {
            setCollapsed(true);
        }

        toggle.addEventListener('click', function () {
            setCollapsed(!document.documentElement.classList.contains('sidebar-collapsed'));
        });
    }

    initUpdatedClock();
    initUserMenu();
    initPeriodPicker();
    initSidebarToggle();
})();