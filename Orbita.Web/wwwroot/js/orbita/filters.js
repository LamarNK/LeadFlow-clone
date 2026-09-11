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
            '.orbita-filters-form input[type="search"], .workers-search input[type="search"], .accounts-search input[type="search"], [data-orbita-live-search]'
        ).forEach(function (input) {
            if (input.hasAttribute('data-orbita-debounce-bound')) return;
            if (input.closest('[data-statistics-multiselect]')) return;
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
        document.querySelectorAll('[data-orbita-collapsible-filters] select, .orbita-filter-panel select, [data-orbita-page-size-form] select').forEach(function (select) {
            if (select.hasAttribute('data-orbita-auto-submit-bound')) return;
            select.setAttribute('data-orbita-auto-submit-bound', '1');
            select.addEventListener('change', function () {
                runtime.submitFilterForm(select.closest('form'));
            });
        });
    }

    function statisticsPickerOptions(picker) {
        return Array.prototype.slice.call(picker.querySelectorAll('[data-statistics-multiselect-option]'));
    }

    function closeStatisticsPicker(picker) {
        var menu = picker.querySelector('[data-statistics-multiselect-menu]');
        var trigger = picker.querySelector('[data-statistics-multiselect-trigger]');
        if (menu) menu.hidden = true;
        if (trigger) trigger.setAttribute('aria-expanded', 'false');
    }

    function syncStatisticsPicker(picker, forceAll) {
        var fieldName = picker.getAttribute('data-statistics-field');
        var triggerText = picker.querySelector('[data-statistics-multiselect-text]');
        var valuesRoot = picker.querySelector('[data-statistics-multiselect-values]');
        var selectAll = picker.querySelector('[data-statistics-multiselect-all]');
        var options = statisticsPickerOptions(picker);
        if (!fieldName || !triggerText || !valuesRoot || !selectAll) return;

        var selected = options.filter(function (option) { return option.checked; });
        var isAll = forceAll === true || selected.length === 0 || selected.length === options.length;
        if (isAll) {
            options.forEach(function (option) { option.checked = true; });
            selected = options;
        }

        selectAll.checked = isAll;
        selectAll.indeterminate = !isAll && selected.length > 0;
        valuesRoot.replaceChildren();
        if (!isAll) {
            selected.forEach(function (option) {
                var value = document.createElement('input');
                value.type = 'hidden';
                value.name = fieldName;
                value.value = option.value;
                valuesRoot.appendChild(value);
            });
        }

        if (isAll) {
            triggerText.textContent = picker.getAttribute('data-statistics-all-label') || 'Все';
        } else if (selected.length === 1) {
            triggerText.textContent = selected[0].parentElement.textContent.trim();
        } else {
            triggerText.textContent = 'Выбрано: ' + selected.length;
        }
    }

    runtime.initStatisticsMultiSelects = function initStatisticsMultiSelects() {
        if (window.__orbitaStatisticsMultiSelectBound) return;
        window.__orbitaStatisticsMultiSelectBound = true;

        document.addEventListener('click', function (event) {
            var picker = event.target.closest('[data-statistics-multiselect]');
            var trigger = event.target.closest('[data-statistics-multiselect-trigger]');

            document.querySelectorAll('[data-statistics-multiselect]').forEach(function (el) {
                if (el !== picker) closeStatisticsPicker(el);
            });

            if (!trigger || !picker) return;

            var menu = picker.querySelector('[data-statistics-multiselect-menu]');
            if (!menu) return;
            var willOpen = menu.hidden;
            menu.hidden = !willOpen;
            trigger.setAttribute('aria-expanded', String(willOpen));
            if (willOpen) {
                var search = picker.querySelector('[data-statistics-multiselect-search]');
                if (search) search.focus();
            }
        });

        document.addEventListener('change', function (event) {
            var picker = event.target.closest('[data-statistics-multiselect]');
            if (!picker) return;
            if (event.target.closest('[data-statistics-multiselect-all]')) {
                syncStatisticsPicker(picker, true);
                return;
            }
            if (event.target.closest('[data-statistics-multiselect-option]')) {
                syncStatisticsPicker(picker, false);
            }
        });

        document.addEventListener('input', function (event) {
            var search = event.target.closest('[data-statistics-multiselect-search]');
            if (!search) return;
            var picker = search.closest('[data-statistics-multiselect]');
            if (!picker) return;
            var query = search.value.trim().toLocaleLowerCase();
            picker.querySelectorAll('[data-statistics-multiselect-option-row]').forEach(function (row) {
                row.hidden = query.length > 0
                    && (row.getAttribute('data-statistics-search-text') || '').toLocaleLowerCase().indexOf(query) < 0;
            });
        });

        document.addEventListener('keydown', function (event) {
            if (event.key !== 'Escape') return;
            var picker = event.target.closest('[data-statistics-multiselect]');
            if (!picker) return;
            closeStatisticsPicker(picker);
            var trigger = picker.querySelector('[data-statistics-multiselect-trigger]');
            if (trigger) trigger.focus();
        });
    }

    runtime.initFilterPanels = function initFilterPanels() {
        runtime.syncFilterToggleCounts();
        runtime.initStatisticsMultiSelects();
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

    runtime.initCrmTaskCreateModal = function initCrmTaskCreateModal() {
        document.querySelectorAll('[data-crm-task-create-modal]').forEach(function (modal) {
            if (modal.hasAttribute('data-orbita-task-modal-bound')) return;
            modal.setAttribute('data-orbita-task-modal-bound', '1');

            var openModal = function () {
                modal.hidden = false;
                document.body.classList.add('orbita-modal-open');
                window.setTimeout(function () {
                    modal.querySelector('input[name="title"]')?.focus();
                }, 0);
            };
            var closeModal = function () {
                modal.hidden = true;
                document.body.classList.remove('orbita-modal-open');
            };

            document.querySelectorAll('[data-crm-task-create-open]').forEach(function (trigger) {
                trigger.addEventListener('click', openModal);
            });
            modal.querySelectorAll('[data-crm-task-create-close]').forEach(function (trigger) {
                trigger.addEventListener('click', closeModal);
            });
        });

        if (!window.__orbitaCrmTaskModalEscapeBound) {
            document.addEventListener('keydown', function (e) {
                if (e.key !== 'Escape') return;
                var modal = document.querySelector('[data-crm-task-create-modal]:not([hidden])');
                if (!modal) return;
                modal.hidden = true;
                document.body.classList.remove('orbita-modal-open');
            });
            window.__orbitaCrmTaskModalEscapeBound = true;
        }
    }

    runtime.initCrmTaskResponsibleFilter = function initCrmTaskResponsibleFilter() {
        var storageKey = 'orbita.crm.tasks.responsibleFilter.scroll.v1';

        var restoreScroll = function () {
            var state = null;
            try {
                state = JSON.parse(window.sessionStorage.getItem(storageKey) || 'null');
                window.sessionStorage.removeItem(storageKey);
            } catch (e) {
                return;
            }

            if (!state || state.targetUrl !== window.location.pathname + window.location.search) return;
            if (!Number.isFinite(state.savedAt) || Date.now() - state.savedAt > 2 * 60 * 1000) return;
            var restore = function () {
                window.scrollTo({ left: 0, top: Math.max(0, Number(state.scrollY) || 0), behavior: 'auto' });
            };
            restore();
            window.requestAnimationFrame(function () {
                restore();
                window.requestAnimationFrame(restore);
            });
        };

        document.querySelectorAll('[data-crm-task-responsible-auto-filter]').forEach(function (select) {
            if (select.hasAttribute('data-crm-task-responsible-filter-bound')) return;
            select.setAttribute('data-crm-task-responsible-filter-bound', '1');
            select.addEventListener('change', function () {
                var url = new URL(window.location.href);
                url.searchParams.set('managerUserId', select.value);
                try {
                    window.sessionStorage.setItem(storageKey, JSON.stringify({
                        targetUrl: url.pathname + url.search,
                        scrollY: window.scrollY,
                        savedAt: Date.now()
                    }));
                } catch (e) { }
                select.disabled = true;
                select.setAttribute('aria-busy', 'true');
                window.location.assign(url.toString());
            });
        });

        restoreScroll();
    }

    runtime.initCrmTaskListScroll = function initCrmTaskListScroll() {
        var storageKey = 'orbita.crm.tasks.listScroll.v1';
        var taskRows = document.querySelectorAll('.crm-task-row__main');
        var taskList = document.querySelector('.crm-task-workspace');

        function normalizePath(pathname) {
            return (pathname || '').replace(/\/+$/, '').toLowerCase();
        }

        function currentTaskScope() {
            var params = new URLSearchParams(window.location.search || '');
            return (params.get('scope') || params.get('taskScope') || 'all').trim().toLowerCase();
        }

        function rememberPosition() {
            try {
                window.sessionStorage.setItem(storageKey, JSON.stringify({
                    pathname: normalizePath(window.location.pathname),
                    scope: currentTaskScope(),
                    scrollX: window.scrollX,
                    scrollY: window.scrollY,
                    savedAt: Date.now()
                }));
            } catch (e) { }
        }

        taskRows.forEach(function (link) {
            if (link.hasAttribute('data-crm-task-list-scroll-bound')) return;
            link.setAttribute('data-crm-task-list-scroll-bound', '1');
            link.addEventListener('click', rememberPosition);
        });

        if (!taskList) return;

        var state = null;
        try {
            state = JSON.parse(window.sessionStorage.getItem(storageKey) || 'null');
        } catch (e) {
            return;
        }

        if (!state) return;

        if (state.pathname !== normalizePath(window.location.pathname)
            || state.scope !== currentTaskScope()
            || !Number.isFinite(state.savedAt)
            || Date.now() - state.savedAt > 30 * 60 * 1000) {
            try { window.sessionStorage.removeItem(storageKey); } catch (e) { }
            return;
        }

        try { window.sessionStorage.removeItem(storageKey); } catch (e) { }

        var restore = function () {
            window.scrollTo({
                left: Math.max(0, Number(state.scrollX) || 0),
                top: Math.max(0, Number(state.scrollY) || 0),
                behavior: 'auto'
            });
        };

        restore();
        window.requestAnimationFrame(function () {
            restore();
            window.requestAnimationFrame(restore);
        });
    }

    runtime.closeCrmVacationPopup = function closeCrmVacationPopup(popup) {
        if (!popup) return;
        popup.hidden = true;
        if (popup.__orbitaVacationTimer) {
            window.clearTimeout(popup.__orbitaVacationTimer);
            popup.__orbitaVacationTimer = null;
        }
        document.querySelectorAll('[data-crm-vacation-popup-open]').forEach(function (trigger) {
            trigger.setAttribute('aria-expanded', 'false');
        });
    }

    runtime.initCrmVacationPopup = function initCrmVacationPopup() {
        var popup = document.querySelector('[data-crm-vacation-popup]');
        if (!popup) return;

        document.querySelectorAll('[data-crm-vacation-popup-open]').forEach(function (trigger) {
            if (trigger.hasAttribute('data-crm-vacation-popup-bound')) return;
            trigger.setAttribute('data-crm-vacation-popup-bound', '1');
            trigger.addEventListener('click', function () {
                popup.hidden = false;
                document.querySelectorAll('[data-crm-vacation-popup-open]').forEach(function (item) {
                    item.setAttribute('aria-expanded', item === trigger ? 'true' : 'false');
                });
                if (popup.__orbitaVacationTimer) window.clearTimeout(popup.__orbitaVacationTimer);
                popup.__orbitaVacationTimer = window.setTimeout(function () {
                    runtime.closeCrmVacationPopup(popup);
                }, 1000);
            });
        });

        popup.querySelectorAll('[data-crm-vacation-popup-close]').forEach(function (trigger) {
            if (trigger.hasAttribute('data-crm-vacation-popup-close-bound')) return;
            trigger.setAttribute('data-crm-vacation-popup-close-bound', '1');
            trigger.addEventListener('click', function () {
                runtime.closeCrmVacationPopup(popup);
            });
        });

        if (!window.__orbitaCrmVacationPopupGlobalBound) {
            document.addEventListener('pointerdown', function (e) {
                var openedPopup = document.querySelector('[data-crm-vacation-popup]:not([hidden])');
                if (!openedPopup || openedPopup.contains(e.target) || e.target.closest('[data-crm-vacation-popup-open]')) return;
                runtime.closeCrmVacationPopup(openedPopup);
            });
            document.addEventListener('keydown', function (e) {
                if (e.key !== 'Escape') return;
                runtime.closeCrmVacationPopup(document.querySelector('[data-crm-vacation-popup]:not([hidden])'));
            });
            window.__orbitaCrmVacationPopupGlobalBound = true;
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
