(function (runtime) {
    runtime.initMobileSidebar = function initMobileSidebar() {
        if (window.__orbitaMobileSidebarReady) return;
        window.__orbitaMobileSidebarReady = true;

        var mobileQuery = window.matchMedia('(max-width: 768px)');
        var previousFocus = null;

        function getMenuButton() {
            return document.querySelector('[data-orbita-mobile-menu]');
        }

        function syncMenuButton(isOpen) {
            var menuButton = getMenuButton();
            if (!menuButton) return;

            menuButton.setAttribute('aria-expanded', isOpen ? 'true' : 'false');
            menuButton.setAttribute('aria-label', isOpen ? 'Закрыть меню' : 'Открыть меню');
        }

        function syncSidebarToggle(isOpen) {
            var sidebarToggle = document.querySelector('[data-orbita-sidebar-toggle]');
            var icon = sidebarToggle && sidebarToggle.querySelector('i');
            if (!sidebarToggle || !icon) return;

            if (isOpen && mobileQuery.matches) {
                sidebarToggle.setAttribute('aria-expanded', 'true');
                sidebarToggle.setAttribute('aria-label', 'Закрыть меню');
                icon.className = 'fa-solid fa-xmark';
                return;
            }

            var isCollapsed = document.documentElement.classList.contains('sidebar-collapsed');
            sidebarToggle.setAttribute('aria-expanded', isCollapsed ? 'false' : 'true');
            sidebarToggle.setAttribute('aria-label', isCollapsed ? 'Развернуть меню' : 'Свернуть меню');
            icon.className = isCollapsed ? 'fa-solid fa-angles-right' : 'fa-solid fa-bars';
        }

        function closeMobileSidebar(restoreFocus) {
            document.documentElement.classList.remove('sidebar-mobile-open');
            document.body.classList.remove('sidebar-mobile-open');
            var backdrop = document.querySelector('[data-orbita-sidebar-backdrop]');
            if (backdrop) backdrop.setAttribute('hidden', '');
            syncMenuButton(false);
            syncSidebarToggle(false);

            if (restoreFocus && previousFocus && typeof previousFocus.focus === 'function') {
                previousFocus.focus();
            }

            previousFocus = null;
        }

        function openMobileSidebar() {
            if (!mobileQuery.matches) return;

            previousFocus = document.activeElement;
            document.documentElement.classList.add('sidebar-mobile-open');
            document.body.classList.add('sidebar-mobile-open');
            var backdrop = document.querySelector('[data-orbita-sidebar-backdrop]');
            if (backdrop) backdrop.removeAttribute('hidden');
            syncMenuButton(true);
            syncSidebarToggle(true);

            var firstNavigationItem = document.querySelector('.orbita-nav .nav-item');
            if (firstNavigationItem) firstNavigationItem.focus();
        }

        document.addEventListener('click', function (e) {
            if (e.target.closest('[data-orbita-mobile-menu]')) {
                if (document.documentElement.classList.contains('sidebar-mobile-open')) {
                    closeMobileSidebar(false);
                } else {
                    openMobileSidebar();
                }
                return;
            }

            if (e.target.closest('[data-orbita-sidebar-backdrop]')) {
                closeMobileSidebar(true);
                return;
            }

            if (e.target.closest('.orbita-nav .nav-item') && mobileQuery.matches) {
                closeMobileSidebar(false);
            }
        });

        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && document.documentElement.classList.contains('sidebar-mobile-open')) {
                closeMobileSidebar(true);
            }
        });

        function closeWhenDesktop() {
            if (!mobileQuery.matches) closeMobileSidebar(false);
        }

        if (typeof mobileQuery.addEventListener === 'function') {
            mobileQuery.addEventListener('change', closeWhenDesktop);
        } else if (typeof mobileQuery.addListener === 'function') {
            mobileQuery.addListener(closeWhenDesktop);
        }

        syncMenuButton(false);

        window.Orbita = window.Orbita || {};
        window.Orbita.closeMobileSidebar = function () { closeMobileSidebar(true); };
    }

    runtime.initSidebarToggle = function initSidebarToggle() {
        if (window.__orbitaSidebarToggleReady) return;
        window.__orbitaSidebarToggleReady = true;

        var storageKey = 'orbita-sidebar-collapsed';

        function setCollapsed(collapsed) {
            var toggle = document.querySelector('[data-orbita-sidebar-toggle]');
            var icon = toggle && toggle.querySelector('i');
            document.documentElement.classList.toggle('sidebar-collapsed', collapsed);
            if (toggle) {
                toggle.setAttribute('aria-expanded', collapsed ? 'false' : 'true');
                toggle.setAttribute('aria-label', collapsed ? 'Развернуть меню' : 'Свернуть меню');
            }
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

        document.addEventListener('click', function (e) {
            if (!e.target.closest('[data-orbita-sidebar-toggle]')) return;

            if (window.matchMedia('(max-width: 768px)').matches) {
                if (document.documentElement.classList.contains('sidebar-mobile-open') &&
                    window.Orbita && typeof window.Orbita.closeMobileSidebar === 'function') {
                    window.Orbita.closeMobileSidebar();
                }
                return;
            }

            setCollapsed(!document.documentElement.classList.contains('sidebar-collapsed'));
        });
    }

})(window.OrbitaRuntime = window.OrbitaRuntime || {});
