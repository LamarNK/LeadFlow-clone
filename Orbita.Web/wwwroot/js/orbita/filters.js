(function (runtime) {
    runtime.getFilterPanels = function getFilterPanels() {
        return document.querySelectorAll('.orbita-filter-panel, [data-orbita-collapsible-filters]');
    }

    runtime.closeAllFilterPanels = function closeAllFilterPanels() {
        runtime.getFilterPanels().forEach(function (p) {
            if (p.hasAttribute('data-orbita-collapsible-filters') && window.matchMedia('(min-width: 1101px)').matches) {
                return;
            }
            p.setAttribute('hidden', '');
        });
        document.querySelectorAll('[data-orbita-filter-toggle]').forEach(function (b) {
            b.setAttribute('aria-expanded', 'false');
        });
    }

    runtime.syncFilterToggleCounts = function syncFilterToggleCounts() {
        document.querySelectorAll('[data-orbita-filter-toggle]').forEach(function (btn) {
            var scope = btn.closest('form') || btn.closest('.orbita-filters-bar') || btn.closest('.workers-page');
            var chips = scope ? scope.querySelector('[data-orbita-filter-chips]') : null;
            var count = chips ? chips.querySelectorAll('.orbita-filter-chip').length : parseInt(btn.getAttribute('data-orbita-filter-count') || '0', 10) || 0;
            btn.setAttribute('data-orbita-filter-count', String(count));
            var badge = btn.querySelector('.orbita-filters-toggle-count');
            if (count > 0) {
                btn.setAttribute('aria-label', 'Фильтры (' + count + ')');
                if (!badge) {
                    badge = document.createElement('span');
                    badge.className = 'orbita-filters-toggle-count';
                    btn.appendChild(badge);
                }
                badge.textContent = String(count);
            } else {
                btn.setAttribute('aria-label', 'Фильтры');
                if (badge && badge.parentNode) badge.parentNode.removeChild(badge);
            }
        });
    }

    runtime.submitFilterForm = function submitFilterForm(form) {
        if (!form) return;
        if (typeof form.requestSubmit === 'function') {
            form.requestSubmit();
        } else {
            form.submit();
        }
    }

    var pendingSearchFocus = null;

    runtime.getSearchParamValue = function getSearchParamValue(input) {
        var name = input.getAttribute('name') || 'search';
        return new URL(window.location.href).searchParams.get(name) || '';
    }

    runtime.shouldSubmitSearch = function shouldSubmitSearch(input) {
        var next = input.value.trim();
        var current = runtime.getSearchParamValue(input).trim();
        return next !== current;
    }

    runtime.rememberSearchFocus = function rememberSearchFocus(input) {
        pendingSearchFocus = {
            name: input.getAttribute('name') || 'search',
            formClass: (input.closest('form') && input.closest('form').className) || ''
        };
    }

    runtime.restoreSearchFocus = function restoreSearchFocus() {
        if (!pendingSearchFocus) return;
        var state = pendingSearchFocus;
        pendingSearchFocus = null;

        var inputs = document.querySelectorAll(
            '.orbita-filters-form input[type="search"], .workers-search input[type="search"], .accounts-search input[type="search"], [data-orbita-live-search]'
        );
        var input = null;
        inputs.forEach(function (candidate) {
            if (input) return;
            if ((candidate.getAttribute('name') || 'search') !== state.name) return;
            var form = candidate.closest('form');
            if (state.formClass && form && form.className !== state.formClass) return;
            input = candidate;
        });
        if (!input) return;

        input.focus({ preventScroll: true });
        try {
            var end = input.value.length;
            input.setSelectionRange(end, end);
        } catch (e) { }
    }

    runtime.initDebouncedSearch = function initDebouncedSearch() {
        var DEBOUNCE_MS = 750;
        document.querySelectorAll(
            '.orbita-filters-form input[type="search"], .workers-search input[type="search"], .accounts-search input[type="search"]'
        ).forEach(function (input) {
            if (input.hasAttribute('data-orbita-debounce-bound')) return;
            input.setAttribute('data-orbita-debounce-bound', '1');

            var timer = null;
            var form = input.closest('form');
            if (!form) return;
            var debounceMs = Number(input.dataset.orbitaDebounceMs) || DEBOUNCE_MS;

            input.addEventListener('input', function () {
                if (timer) window.clearTimeout(timer);
                timer = window.setTimeout(function () {
                    timer = null;
                    if (!runtime.shouldSubmitSearch(input)) return;
                    runtime.rememberSearchFocus(input);
                    runtime.submitFilterForm(form);
                }, debounceMs);
            });

            input.addEventListener('keydown', function (e) {
                if (e.key === 'Enter') {
                    if (timer) {
                        window.clearTimeout(timer);
                        timer = null;
                    }
                    if (!runtime.shouldSubmitSearch(input)) return;
                    e.preventDefault();
                    runtime.rememberSearchFocus(input);
                    runtime.submitFilterForm(form);
                }
            });
        });
    }

    runtime.initAutoFilterSubmit = function initAutoFilterSubmit() {
        document.querySelectorAll('[data-orbita-collapsible-filters] select, .orbita-filter-panel select').forEach(function (select) {
            if (select.hasAttribute('data-orbita-auto-submit-bound')) return;
            select.setAttribute('data-orbita-auto-submit-bound', '1');
            select.addEventListener('change', function () {
                runtime.submitFilterForm(select.closest('form'));
            });
        });
    }

    runtime.initFilterPanels = function initFilterPanels() {
        runtime.syncFilterToggleCounts();
        document.querySelectorAll('[data-orbita-filter-toggle]').forEach(function (btn) {
            if (btn.hasAttribute('data-orbita-filter-bound')) return;
            btn.setAttribute('data-orbita-filter-bound', '1');

            btn.addEventListener('click', function (e) {
                e.stopPropagation();
                var panelId = btn.getAttribute('data-orbita-filter-toggle');
                var panel = panelId ? document.getElementById(panelId) : null;
                if (!panel) return;

                var open = panel.hasAttribute('hidden');
                runtime.closeAllFilterPanels();

                if (open) {
                    panel.removeAttribute('hidden');
                    btn.setAttribute('aria-expanded', 'true');
                }
            });
        });

        if (!window.__orbitaFilterPanelDocListeners) {
            document.addEventListener('click', function (e) {
                if (e.target.closest('[data-orbita-filter-toggle]')
                    || e.target.closest('.orbita-filter-panel')
                    || e.target.closest('[data-orbita-collapsible-filters]')) {
                    return;
                }
                runtime.closeAllFilterPanels();
            });
            document.addEventListener('keydown', function (e) {
                if (e.key !== 'Escape') return;
                runtime.closeAllFilterPanels();
            });
            window.__orbitaFilterPanelDocListeners = true;
        }
    }

    var detailModal = null;
    var detailTitle = null;
    var detailSubtitle = null;
    var detailBody = null;
    var detailFoot = null;
    var detailNav = null;
    var detailCopy = null;
    var detailCopyLabel = null;
    var detailDismiss = null;
    var detailDismissLabel = null;
    var detailPrimary = null;
    var detailCloseHandler = null;

})(window.OrbitaRuntime = window.OrbitaRuntime || {});
