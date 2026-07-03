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
            if (menu.hasAttribute('data-orbita-user-menu-bound')) return;
            menu.setAttribute('data-orbita-user-menu-bound', '1');

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

        if (!window.__orbitaUserMenuDocListeners) {
            document.addEventListener('click', closeAllPopovers);
            document.addEventListener('keydown', function (e) {
                if (e.key === 'Escape') closeAllPopovers();
            });
            window.__orbitaUserMenuDocListeners = true;
        }
    }

    function initPeriodPicker() {
        document.querySelectorAll('[data-orbita-period-menu]').forEach(function (menu) {
            if (menu.hasAttribute('data-orbita-period-menu-bound')) return;
            menu.setAttribute('data-orbita-period-menu-bound', '1');

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
        var target = url.pathname + url.search;
        // prefer fast client nav when available (keeps SPA feel)
        if (typeof navigateTo === 'function') {
            navigateTo(target, true);
        } else {
            window.location.href = target;
        }
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

    function resetFloatingDropdown(dropdown) {
        if (!dropdown) return;
        dropdown.setAttribute('hidden', '');
        dropdown.classList.remove('row-menu-dropdown--floating');
        dropdown.style.top = '';
        dropdown.style.left = '';
        dropdown.style.right = '';
        dropdown.style.visibility = '';
    }

    function positionFloatingDropdown(trigger, dropdown) {
        dropdown.classList.add('row-menu-dropdown--floating');
        dropdown.style.visibility = 'hidden';
        dropdown.removeAttribute('hidden');

        var rect = trigger.getBoundingClientRect();
        var menuRect = dropdown.getBoundingClientRect();
        var margin = 8;
        var top = rect.bottom + 4;
        var left = rect.right - menuRect.width;

        if (left < margin) left = margin;
        if (left + menuRect.width > window.innerWidth - margin) {
            left = window.innerWidth - menuRect.width - margin;
        }
        if (top + menuRect.height > window.innerHeight - margin) {
            top = rect.top - menuRect.height - 4;
        }
        if (top < margin) top = margin;

        dropdown.style.top = top + 'px';
        dropdown.style.left = left + 'px';
        dropdown.style.right = 'auto';
        dropdown.style.visibility = '';
    }

    function closeAllRowMenus() {
        document.querySelectorAll('[data-row-menu]').forEach(function (menu) {
            var trigger = menu.querySelector('.row-menu-btn');
            var dropdown = menu.querySelector('.row-menu-dropdown');
            resetFloatingDropdown(dropdown);
            if (trigger) trigger.setAttribute('aria-expanded', 'false');
        });
    }

    function handleRowMenuDocumentClick(e) {
        var rowTrigger = e.target.closest('[data-row-menu] .row-menu-btn');
        if (rowTrigger) {
            e.stopPropagation();
            var menu = rowTrigger.closest('[data-row-menu]');
            var dropdown = menu && menu.querySelector('.row-menu-dropdown');
            if (!dropdown) return;

            var willOpen = dropdown.hasAttribute('hidden');
            closeAllRowMenus();
            if (willOpen) {
                positionFloatingDropdown(rowTrigger, dropdown);
                rowTrigger.setAttribute('aria-expanded', 'true');
            }
            return;
        }

        if (e.target.closest('.row-menu-dropdown')) {
            return;
        }

        closeAllRowMenus();
    }

    function initRowMenus() {
        if (window.__orbitaRowMenuDelegationReady) return;
        window.__orbitaRowMenuDelegationReady = true;
        window.__orbitaRowMenuDocListeners = true;

        // Capture phase: run before other document click handlers (e.g. user menu close).
        document.addEventListener('click', handleRowMenuDocumentClick, true);
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') closeAllPopovers();
        });
        window.addEventListener('resize', closeAllRowMenus);
    }

    function initLiveRowActions() {
        if (window.__orbitaLiveRowActionsReady) return;
        window.__orbitaLiveRowActionsReady = true;

        document.addEventListener('click', function (e) {
            var detailRow = e.target.closest('.events-row, .errors-row');
            if (detailRow && !e.target.closest('a, button, .row-menu, input, select, label')) {
                openDetailFromRow(detailRow);
                return;
            }

            var copyPhone = e.target.closest('[data-copy-phone]');
            if (copyPhone) {
                e.stopPropagation();
                var row = copyPhone.closest('.responses-row');
                var phone = row ? row.getAttribute('data-phone') : '';
                if (phone) copyText(phone);
                closeAllRowMenus();
                return;
            }

            var copyEvent = e.target.closest('[data-copy-event]');
            if (copyEvent) {
                e.stopPropagation();
                var eventRow = copyEvent.closest('.events-row');
                var eventText = eventRow ? eventRow.getAttribute('data-copy') : '';
                if (eventText) copyText(eventText);
                closeAllRowMenus();
                return;
            }

            var copyError = e.target.closest('[data-copy-error]');
            if (copyError) {
                e.stopPropagation();
                var errorRow = copyError.closest('.errors-row');
                var errorText = errorRow ? errorRow.getAttribute('data-copy') : '';
                if (errorText) copyText(errorText);
                closeAllRowMenus();
                return;
            }

            var dismissEvent = e.target.closest('[data-event-dismiss]');
            if (dismissEvent) {
                e.stopPropagation();
                handleDismissRow(dismissEvent, '/Events/Dismiss', '.events-row', 'Отметить обработанным?', 'Событие будет скрыто из списка.');
                return;
            }

            var dismissError = e.target.closest('[data-error-dismiss]');
            if (dismissError) {
                e.stopPropagation();
                handleDismissRow(dismissError, '/Errors/Dismiss', '.errors-row', 'Отметить как обработанную?', 'Ошибка будет скрыта из списка.');
            }
        });
    }

    async function handleDismissRow(btn, url, rowSelector, title, message) {
        var eventId = btn.getAttribute('data-event-id');
        if (!eventId) return;

        if (window.Orbita && window.Orbita.confirm) {
            var confirmed = await window.Orbita.confirm({
                title: title,
                message: message,
                confirmLabel: 'Отметить'
            });
            if (!confirmed) return;
        }

        var result = await postForm(url, { eventId: eventId });
        if (result.ok) {
            var row = btn.closest(rowSelector);
            if (!row) {
                row = document.querySelector(rowSelector + '[data-event-id="' + eventId + '"]');
            }
            if (row && row.parentNode) row.parentNode.removeChild(row);
            showToast((result.payload && result.payload.message) || 'Готово', { variant: 'success' });
            closeDetailModal();
        } else {
            showToast((result.payload && result.payload.error) || 'Не удалось выполнить', { variant: 'error' });
        }
        closeAllRowMenus();
    }

    function closeAllPopovers() {
        closeAllRowMenus();
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

    function initMobileSidebar() {
        if (window.__orbitaMobileSidebarReady) return;
        window.__orbitaMobileSidebarReady = true;

        function closeMobileSidebar() {
            document.documentElement.classList.remove('sidebar-mobile-open');
            var backdrop = document.querySelector('[data-orbita-sidebar-backdrop]');
            if (backdrop) backdrop.setAttribute('hidden', '');
        }

        function openMobileSidebar() {
            document.documentElement.classList.add('sidebar-mobile-open');
            var backdrop = document.querySelector('[data-orbita-sidebar-backdrop]');
            if (backdrop) backdrop.removeAttribute('hidden');
        }

        document.addEventListener('click', function (e) {
            if (e.target.closest('[data-orbita-mobile-menu]')) {
                if (document.documentElement.classList.contains('sidebar-mobile-open')) {
                    closeMobileSidebar();
                } else {
                    openMobileSidebar();
                }
                return;
            }

            if (e.target.closest('[data-orbita-sidebar-backdrop]')) {
                closeMobileSidebar();
                return;
            }

            if (e.target.closest('.orbita-nav .nav-item')) {
                if (window.matchMedia('(max-width: 768px)').matches) {
                    closeMobileSidebar();
                }
            }
        });

        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') closeMobileSidebar();
        });

        window.Orbita = window.Orbita || {};
        window.Orbita.closeMobileSidebar = closeMobileSidebar;
    }

    function initSidebarToggle() {
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
            setCollapsed(!document.documentElement.classList.contains('sidebar-collapsed'));
        });
    }

    function showToast(message, options) {
        options = options || {};
        var variant = options.variant || 'info';
        var duration = options.duration == null ? 3200 : options.duration;
        var host = document.querySelector('[data-orbita-toast-host]');
        if (!host || !message) return;

        var toast = document.createElement('div');
        toast.className = 'orbita-toast orbita-toast--' + variant;
        toast.setAttribute('role', 'status');
        toast.textContent = message;
        host.appendChild(toast);

        requestAnimationFrame(function () {
            toast.classList.add('is-visible');
        });

        var hideTimer = setTimeout(function () {
            toast.classList.remove('is-visible');
            setTimeout(function () {
                if (toast.parentNode) toast.parentNode.removeChild(toast);
            }, 220);
        }, duration);

        toast.addEventListener('click', function () {
            clearTimeout(hideTimer);
            toast.classList.remove('is-visible');
            setTimeout(function () {
                if (toast.parentNode) toast.parentNode.removeChild(toast);
            }, 180);
        });
    }

    function copyText(text, successMessage) {
        if (!text) return Promise.resolve(false);
        successMessage = successMessage || 'Скопировано';

        function fallbackCopy() {
            var area = document.createElement('textarea');
            area.value = text;
            document.body.appendChild(area);
            area.select();
            var ok = false;
            try { ok = document.execCommand('copy'); } catch (e) { }
            document.body.removeChild(area);
            return ok;
        }

        var promise = navigator.clipboard && navigator.clipboard.writeText
            ? navigator.clipboard.writeText(text).then(function () { return true; }).catch(function () { return fallbackCopy(); })
            : Promise.resolve(fallbackCopy());

        return promise.then(function (ok) {
            if (ok) showToast(successMessage, { variant: 'success' });
            else showToast('Не удалось скопировать', { variant: 'error' });
            return ok;
        });
    }

    var confirmDialog = null;
    var confirmTitle = null;
    var confirmMessage = null;
    var confirmOkBtn = null;
    var confirmPending = null;

    function initConfirmDialog() {
        confirmDialog = document.getElementById('orbitaConfirmDialog');
        if (!confirmDialog || confirmDialog.hasAttribute('data-orbita-confirm-ready')) return;

        confirmTitle = confirmDialog.querySelector('#orbitaConfirmTitle');
        confirmMessage = confirmDialog.querySelector('#orbitaConfirmMessage');
        confirmOkBtn = confirmDialog.querySelector('[data-orbita-confirm-ok]');

        confirmDialog.querySelectorAll('[data-orbita-confirm-cancel]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                closeConfirm(false);
            });
        });

        if (confirmOkBtn) {
            confirmOkBtn.addEventListener('click', function () {
                closeConfirm(true);
            });
        }

        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && confirmDialog && !confirmDialog.hasAttribute('hidden')) {
                closeConfirm(false);
            }
        });

        confirmDialog.setAttribute('data-orbita-confirm-ready', '1');
    }

    function closeConfirm(confirmed) {
        if (!confirmDialog) return;
        confirmDialog.setAttribute('hidden', '');
        var resolver = confirmPending;
        confirmPending = null;
        if (resolver) {
            resolver.resolve(!!confirmed);
        }
    }

    function showConfirm(options) {
        options = options || {};
        initConfirmDialog();
        if (!confirmDialog || !confirmTitle || !confirmMessage || !confirmOkBtn) {
            return Promise.resolve(false);
        }

        confirmTitle.textContent = options.title || 'Подтвердите действие';
        confirmMessage.textContent = options.message || '';
        confirmOkBtn.textContent = options.confirmLabel || 'Подтвердить';
        confirmOkBtn.classList.toggle('orbita-confirm__btn--danger', options.variant === 'danger');

        confirmDialog.removeAttribute('hidden');

        return new Promise(function (resolve) {
            confirmPending = { resolve: resolve };
        });
    }

    function initSubProfilesRefreshButtons() {
        document.querySelectorAll('[data-refresh-subprofiles]').forEach(function (btn) {
            if (btn.hasAttribute('data-refresh-bound')) return;
            btn.setAttribute('data-refresh-bound', '1');

            btn.addEventListener('click', async function (e) {
                e.preventDefault();
                e.stopPropagation();
                if (btn.disabled || btn.classList.contains('is-loading')) return;

                var workerId = btn.getAttribute('data-worker-id');
                var accountId = btn.getAttribute('data-account-id');
                if (!workerId || !accountId) return;

                var path = window.location.pathname.toLowerCase().indexOf('/accounts') >= 0
                    ? '/Accounts/RefreshSubProfiles'
                    : '/Workers/RefreshSubProfiles';

                btn.disabled = true;
                btn.classList.add('is-loading');

                var result = await postForm(path, { workerId: workerId, accountId: accountId });
                btn.classList.remove('is-loading');
                btn.disabled = false;

                if (result.ok) {
                    btn.classList.add('is-pending');
                    btn.title = 'Обновление запрошено — ждём воркер';
                    showToast((result.payload && result.payload.message) || 'Запрос отправлен', { variant: 'success' });
                } else {
                    showToast((result.payload && result.payload.error) || 'Не удалось отправить запрос', { variant: 'error' });
                }
            });
        });
    }

    function initSubProfileEnableToggles() {
        document.querySelectorAll('[data-subprofile-toggle]').forEach(function (input) {
            if (input.hasAttribute('data-subprofile-enable-bound')) return;
            input.setAttribute('data-subprofile-enable-bound', '1');

            input.addEventListener('change', async function (e) {
                e.stopPropagation();
                if (input.disabled) return;

                var workerId = input.getAttribute('data-worker-id');
                var accountId = input.getAttribute('data-account-id');
                var subProfileId = input.getAttribute('data-subprofile-id');
                if (!workerId || !accountId || !subProfileId) return;

                var enabled = input.checked;
                var path = window.location.pathname.toLowerCase().indexOf('/accounts') >= 0
                    ? '/Accounts/UpdateSubProfile'
                    : '/Workers/UpdateSubProfile';

                input.disabled = true;
                var result = await postForm(path, {
                    workerId: workerId,
                    accountId: accountId,
                    subProfileId: subProfileId,
                    isEnabledInPanel: enabled ? 'true' : 'false'
                });
                input.disabled = false;

                var row = input.closest('.subprofiles-item');
                if (result.ok) {
                    if (row) {
                        row.classList.toggle('subprofiles-item--disabled', !enabled);
                    }
                    showToast((result.payload && result.payload.message) || 'Сохранено', { variant: 'success' });
                } else {
                    input.checked = !enabled;
                    showToast((result.payload && result.payload.error) || 'Не удалось сохранить', { variant: 'error' });
                }
            });
        });
    }

    function initSubProfileScreenshotLinks() {
        document.querySelectorAll('[data-subprofile-screenshot]').forEach(function (btn) {
            if (btn.hasAttribute('data-subprofile-screenshot-bound')) return;
            btn.setAttribute('data-subprofile-screenshot-bound', '1');

            btn.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                var url = btn.getAttribute('data-screenshot-url');
                if (!url) return;

                var item = btn.closest('.subprofiles-item');
                var issueEl = btn.closest('.subprofiles-item-alert')?.querySelector('.subprofiles-issue');
                var name = item?.querySelector('.subprofiles-name')?.textContent?.trim() || '';
                var issue = issueEl?.textContent?.trim() || '';

                openDetailModal({
                    title: name ? 'Скриншот · ' + name : 'Скриншот ошибки',
                    body: issue,
                    attachmentUrl: url
                });
            });
        });
    }

    function initSubProfilesToggles() {
        document.querySelectorAll('[data-subprofiles-toggle]').forEach(function (btn) {
            if (btn.hasAttribute('data-subprofiles-bound')) return;
            btn.setAttribute('data-subprofiles-bound', '1');
            btn.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                var panelId = btn.getAttribute('aria-controls');
                var panel = panelId ? document.getElementById(panelId) : null;
                if (!panel) return;
                var expanded = btn.getAttribute('aria-expanded') === 'true';
                btn.setAttribute('aria-expanded', expanded ? 'false' : 'true');
                if (expanded) {
                    panel.setAttribute('hidden', '');
                } else {
                    panel.removeAttribute('hidden');
                }
            });
        });
    }

    initUpdatedClock();
    initRowMenus();
    initSubProfilesToggles();
    initSubProfileEnableToggles();
    initSubProfilesRefreshButtons();
    initSubProfileScreenshotLinks();
    initUserMenu();
    initPeriodPicker();
    initSidebarToggle();
    initMobileSidebar();
    initConfirmDialog();

    function initWorkerRestartButtons() {
        document.querySelectorAll('[data-worker-restart]').forEach(function (btn) {
            if (btn.hasAttribute('data-worker-restart-bound')) return;
            btn.setAttribute('data-worker-restart-bound', '1');

            btn.addEventListener('click', async function (e) {
                e.stopPropagation();
                var workerId = btn.getAttribute('data-worker-id');
                if (!workerId) return;

                if (window.Orbita && window.Orbita.confirm) {
                    var confirmed = await window.Orbita.confirm({
                        title: 'Перезапустить воркер?',
                        message: 'Воркер получит команду и перезапустит процесс на VDS.',
                        confirmLabel: 'Перезапустить',
                        variant: 'danger'
                    });
                    if (!confirmed) return;
                }

                var result = await postForm('/Workers/Restart', { workerId: workerId });
                if (result.ok) {
                    showToast((result.payload && result.payload.message) || 'Команда отправлена', { variant: 'success' });
                } else {
                    showToast((result.payload && result.payload.error) || 'Не удалось отправить команду', { variant: 'error' });
                }
            });
        });
    }

    initWorkerRestartButtons();

    function updateNavBadges(payload) {
        if (!payload) return;
        var errorsEl = document.querySelector('[data-nav-badge="errors"]');
        var responsesEl = document.querySelector('[data-nav-badge="responses"]');
        setBadge(errorsEl, payload.errorsToday);
        setBadge(responsesEl, payload.responsesToday);
    }

    function setBadge(el, value) {
        if (!el) return;
        var count = parseInt(value, 10) || 0;
        if (count <= 0) {
            el.setAttribute('hidden', '');
            el.textContent = '';
            return;
        }
        el.removeAttribute('hidden');
        el.textContent = count > 99 ? '99+' : String(count);
        el.setAttribute('aria-label', count + ' новых');
    }

    function fetchNavBadges() {
        fetch('/Nav/Badges', { credentials: 'same-origin' })
            .then(function (res) { return res.ok ? res.json() : null; })
            .then(updateNavBadges)
            .catch(function () { });
    }

    function getFilterPanels() {
        return document.querySelectorAll('.orbita-filter-panel, [data-orbita-collapsible-filters]');
    }

    function closeAllFilterPanels() {
        getFilterPanels().forEach(function (p) {
            if (p.hasAttribute('data-orbita-collapsible-filters') && window.matchMedia('(min-width: 1101px)').matches) {
                return;
            }
            p.setAttribute('hidden', '');
        });
        document.querySelectorAll('[data-orbita-filter-toggle]').forEach(function (b) {
            b.setAttribute('aria-expanded', 'false');
        });
    }

    function initFilterPanels() {
        document.querySelectorAll('[data-orbita-filter-toggle]').forEach(function (btn) {
            if (btn.hasAttribute('data-orbita-filter-bound')) return;
            btn.setAttribute('data-orbita-filter-bound', '1');

            btn.addEventListener('click', function (e) {
                e.stopPropagation();
                var panelId = btn.getAttribute('data-orbita-filter-toggle');
                var panel = panelId ? document.getElementById(panelId) : null;
                if (!panel) return;

                var open = panel.hasAttribute('hidden');
                closeAllFilterPanels();

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
                closeAllFilterPanels();
            });
            document.addEventListener('keydown', function (e) {
                if (e.key !== 'Escape') return;
                closeAllFilterPanels();
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

    function getRowDetailLinks(row) {
        var links = [];
        var logUrl = row.getAttribute('data-detail-log-url');
        var accountUrl = row.getAttribute('data-detail-account-url');
        var workerUrl = row.getAttribute('data-detail-worker-url');

        if (logUrl) {
            links.push({ href: logUrl, label: 'Открыть лог', icon: 'fa-regular fa-file-lines' });
        }
        if (accountUrl) {
            links.push({ href: accountUrl, label: 'К аккаунту', icon: 'fa-regular fa-user' });
        }
        if (workerUrl) {
            links.push({ href: workerUrl, label: 'К воркеру', icon: 'fa-solid fa-server' });
        }

        return links;
    }

    function getRowDetailOptions(row) {
        if (!row) return null;

        var options = {
            title: row.getAttribute('data-detail-title') || 'Детали',
            subtitle: row.getAttribute('data-detail-subtitle') || '',
            body: row.getAttribute('data-detail-body') || row.getAttribute('data-copy') || '',
            attachmentUrl: row.getAttribute('data-detail-attachment') || '',
            links: getRowDetailLinks(row),
            copyText: row.getAttribute('data-copy') || ''
        };

        if (row.classList.contains('events-row')) {
            options.copyLabel = 'Копировать сообщение';
            options.dismiss = {
                eventId: row.getAttribute('data-event-id') || '',
                url: '/Events/Dismiss',
                rowSelector: '.events-row',
                title: 'Отметить обработанным?',
                message: 'Событие будет скрыто из списка.',
                label: 'Отметить обработанным'
            };
        } else if (row.classList.contains('errors-row')) {
            options.copyLabel = 'Копировать текст';
            options.dismiss = {
                eventId: row.getAttribute('data-event-id') || '',
                url: '/Errors/Dismiss',
                rowSelector: '.errors-row',
                title: 'Отметить как обработанную?',
                message: 'Ошибка будет скрыта из списка.',
                label: 'Отметить как обработанную'
            };
        }

        return options;
    }

    function openDetailFromRow(row) {
        var options = getRowDetailOptions(row);
        if (!options) return;
        openDetailModal(options);
    }

    function setDetailFooter(options) {
        if (!detailFoot || !detailNav || !detailCopy || !detailCopyLabel || !detailDismiss || !detailDismissLabel) return;

        options = options || {};
        var links = options.links || [];
        var hasLinks = links.length > 0;
        var hasCopy = !!options.copyText;
        var dismiss = options.dismiss || null;
        var hasDismiss = !!(dismiss && dismiss.eventId);

        detailNav.innerHTML = '';
        if (hasLinks) {
            links.forEach(function (link) {
                var anchor = document.createElement('a');
                anchor.className = 'orbita-detail-modal__nav-link';
                anchor.href = link.href;
                anchor.innerHTML = '<i class="' + link.icon + '" aria-hidden="true"></i><span>' + link.label + '</span>';
                detailNav.appendChild(anchor);
            });
            detailNav.removeAttribute('hidden');
        } else {
            detailNav.setAttribute('hidden', '');
        }

        if (hasCopy) {
            detailCopy.dataset.copyText = options.copyText;
            detailCopyLabel.textContent = options.copyLabel || 'Копировать';
            detailCopy.removeAttribute('hidden');
        } else {
            detailCopy.removeAttribute('data-copy-text');
            detailCopy.setAttribute('hidden', '');
        }

        detailDismiss.removeAttribute('data-event-id');
        detailDismiss.removeAttribute('data-dismiss-url');
        detailDismiss.removeAttribute('data-dismiss-row-selector');
        detailDismiss.removeAttribute('data-dismiss-title');
        detailDismiss.removeAttribute('data-dismiss-message');

        if (hasDismiss) {
            detailDismiss.setAttribute('data-event-id', dismiss.eventId);
            detailDismiss.setAttribute('data-dismiss-url', dismiss.url);
            detailDismiss.setAttribute('data-dismiss-row-selector', dismiss.rowSelector);
            detailDismiss.setAttribute('data-dismiss-title', dismiss.title);
            detailDismiss.setAttribute('data-dismiss-message', dismiss.message);
            detailDismissLabel.textContent = dismiss.label;
            detailDismiss.removeAttribute('hidden');
        } else {
            detailDismiss.setAttribute('hidden', '');
            detailDismissLabel.textContent = '';
        }

        if (hasLinks || hasCopy || hasDismiss) {
            detailFoot.removeAttribute('hidden');
        } else {
            detailFoot.setAttribute('hidden', '');
        }
    }

    function initDetailModal() {
        detailModal = document.getElementById('orbitaDetailModal');
        if (!detailModal || detailModal.hasAttribute('data-orbita-detail-ready')) return;

        detailTitle = detailModal.querySelector('#orbitaDetailTitle');
        detailSubtitle = detailModal.querySelector('#orbitaDetailSubtitle');
        detailBody = detailModal.querySelector('#orbitaDetailBody');
        detailFoot = detailModal.querySelector('#orbitaDetailFoot');
        detailNav = detailModal.querySelector('#orbitaDetailNav');
        detailCopy = detailModal.querySelector('#orbitaDetailCopy');
        detailCopyLabel = detailModal.querySelector('#orbitaDetailCopyLabel');
        detailDismiss = detailModal.querySelector('#orbitaDetailDismiss');
        detailDismissLabel = detailModal.querySelector('#orbitaDetailDismissLabel');

        detailModal.querySelectorAll('[data-orbita-detail-close]').forEach(function (btn) {
            btn.addEventListener('click', closeDetailModal);
        });

        if (detailCopy) {
            detailCopy.addEventListener('click', function (e) {
                e.stopPropagation();
                var text = detailCopy.dataset.copyText || '';
                if (text) copyText(text);
            });
        }

        if (detailDismiss) {
            detailDismiss.addEventListener('click', function (e) {
                e.stopPropagation();
                var url = detailDismiss.getAttribute('data-dismiss-url');
                var rowSelector = detailDismiss.getAttribute('data-dismiss-row-selector');
                var title = detailDismiss.getAttribute('data-dismiss-title');
                var message = detailDismiss.getAttribute('data-dismiss-message');
                if (!url || !rowSelector) return;
                handleDismissRow(detailDismiss, url, rowSelector, title, message);
            });
        }

        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && detailModal && !detailModal.hasAttribute('hidden')) {
                closeDetailModal();
            }
        });

        detailModal.setAttribute('data-orbita-detail-ready', '1');
    }

    function openDetailModal(options) {
        initDetailModal();
        if (!detailModal || !detailTitle || !detailBody) return;

        detailTitle.textContent = options.title || 'Детали';
        if (detailSubtitle) {
            if (options.subtitle) {
                detailSubtitle.textContent = options.subtitle;
                detailSubtitle.removeAttribute('hidden');
            } else {
                detailSubtitle.textContent = '';
                detailSubtitle.setAttribute('hidden', '');
            }
        }
        detailBody.textContent = '';
        if (options.attachmentUrl) {
            var img = document.createElement('img');
            img.className = 'orbita-detail-screenshot';
            img.alt = 'Скриншот страницы';
            img.src = options.attachmentUrl;
            detailBody.appendChild(img);
        }
        if (options.body) {
            var text = document.createElement('p');
            text.className = 'orbita-detail-text';
            text.textContent = options.body;
            detailBody.appendChild(text);
        }
        setDetailFooter({
            links: options.links || [],
            copyText: options.copyText || '',
            copyLabel: options.copyLabel || 'Копировать',
            dismiss: options.dismiss || null
        });
        detailModal.classList.toggle('orbita-detail-modal--media', !!options.attachmentUrl);
        detailModal.removeAttribute('hidden');
        detailBody.scrollTop = 0;
        closeAllPopovers();
    }

    function closeDetailModal() {
        if (!detailModal) return;
        detailModal.setAttribute('hidden', '');
        detailModal.classList.remove('orbita-detail-modal--media');
        setDetailFooter(null);
    }

    function initDetailOpenButtons() {
        document.querySelectorAll('[data-orbita-detail-open]').forEach(function (btn) {
            if (btn.hasAttribute('data-orbita-detail-bound')) return;
            btn.setAttribute('data-orbita-detail-bound', '1');

            btn.addEventListener('click', function (e) {
                e.stopPropagation();
                var row = btn.closest('tr');
                if (!row) return;
                openDetailFromRow(row);
            });
        });
    }

    initFilterPanels();
    initDetailModal();
    initDetailOpenButtons();
    initLiveRowActions();

    // --- Fast page switching (client-side, no full reload) + loading spinner ---

    function getNavKey(pathname) {
        var p = (pathname || '').split('?')[0].replace(/\/+$/, '').toLowerCase();
        if (!p || p === '/' || p === '/dashboard' || p === '/dashboard/index') return 'dashboard';
        var seg = p.split('/')[1] || '';
        return seg;
    }

    function updateActiveNav(currentPath) {
        var curKey = getNavKey(currentPath);
        document.querySelectorAll('.orbita-nav .nav-item').forEach(function (a) {
            a.classList.remove('active');
            try {
                var linkKey = getNavKey(a.getAttribute('href') || a.href);
                if (linkKey === curKey) {
                    a.classList.add('active');
                }
            } catch (e) { }
        });
    }

    var currentLoadingOverlay = null;

    function showPageLoading() {
        // Remove any previous overlay first
        if (currentLoadingOverlay && currentLoadingOverlay.parentNode) {
            currentLoadingOverlay.parentNode.removeChild(currentLoadingOverlay);
        }

        var overlay = document.createElement('div');
        overlay.className = 'orbita-nav-loading';
        overlay.innerHTML =
            '<div class="orbita-spinner">' +
            '<i class="fa-solid fa-spinner fa-spin" aria-hidden="true"></i>' +
            '<span>Загрузка...</span>' +
            '</div>';

        // Append to .orbita-app so it is not destroyed when we replace .orbita-content innerHTML
        var app = document.querySelector('.orbita-app') || document.body;
        app.appendChild(overlay);

        currentLoadingOverlay = overlay;
    }

    function hidePageLoading() {
        if (currentLoadingOverlay) {
            if (currentLoadingOverlay.parentNode) {
                currentLoadingOverlay.parentNode.removeChild(currentLoadingOverlay);
            }
            currentLoadingOverlay = null;
        }
    }

    function reinitAfterContentSwap() {
        // Re-run inits for swapped .orbita-content only (layout/sidebar handlers are one-time)
        initUpdatedClock();
        initUserMenu();
        initPeriodPicker();
        initConfirmDialog();
        initRowMenus();
        initSubProfilesToggles();
        initSubProfileEnableToggles();
        initSubProfilesRefreshButtons();
        initSubProfileScreenshotLinks();
        initWorkerRestartButtons();
        initFilterPanels();
        initDetailModal();
        initDetailOpenButtons();

        // Re-localize any new time elements
        if (window.OrbitaTime && window.OrbitaTime.localizeAll) {
            window.OrbitaTime.localizeAll();
        }
    }

    var __loadedPageScripts = window.__orbitaLoadedScripts || (window.__orbitaLoadedScripts = new Set());

    function loadScriptOnce(src) {
        if (__loadedPageScripts.has(src)) return Promise.resolve();
        // crude check if similar script tag already present
        var base = src.split('/').pop().split('?')[0];
        if (document.querySelector('script[src*="' + base + '"]')) {
            __loadedPageScripts.add(src);
            return Promise.resolve();
        }
        return new Promise(function (resolve, reject) {
            var s = document.createElement('script');
            s.src = src;
            s.async = false;
            s.onload = function () { __loadedPageScripts.add(src); resolve(); };
            s.onerror = function () { reject(new Error('Failed to load ' + src)); };
            document.head.appendChild(s);
        });
    }

    function getScriptsForPath(fullPath, controllerKey) {
        var p = (fullPath || '').toLowerCase();
        var key = (controllerKey || '').toLowerCase();
        var scripts = [];

        if (key === 'dashboard' || p === '/' || p.startsWith('/dashboard')) {
            scripts = ['/lib/chart.js/dist/chart.umd.js', '/js/orbita-dashboard.js'];
        } else if (key === 'workers') {
            if (p.indexOf('/details') > -1 || /\/workers\/[0-9a-f]{8}/.test(p)) {
                scripts = ['/lib/chart.js/dist/chart.umd.js', '/js/orbita-worker.js'];
            } else {
                scripts = ['/js/orbita-workers.js'];
            }
        } else if (key === 'accounts') {
            scripts = ['/js/orbita-accounts.js'];
        } else if (key === 'events') {
            scripts = ['/js/orbita-events.js'];
        } else if (key === 'errors') {
            scripts = ['/js/orbita-errors.js'];
        } else if (key === 'responses') {
            scripts = ['/js/orbita-responses.js'];
        } else if (key === 'mysettings') {
            scripts = ['/js/orbita-bitrix-settings.js'];
        } else if (key === 'settings') {
            scripts = [
                '/js/orbita-settings.js',
                '/js/orbita-bitrix-settings.js',
                '/js/orbita-worker-releases.js',
                '/js/orbita-leadflow-import.js'
            ];
        }
        return scripts;
    }

    async function ensurePageScripts(fullPath, controllerKey) {
        var scripts = getScriptsForPath(fullPath, controllerKey);
        for (var i = 0; i < scripts.length; i++) {
            try {
                await loadScriptOnce(scripts[i]);
            } catch (e) {
                console.warn('Page script load:', e);
            }
        }
    }

    async function navigateTo(targetPath, push) {
        push = push !== false;
        var content = document.querySelector('.orbita-content');
        if (!content) {
            window.location.href = targetPath;
            return;
        }

        if (window.OrbitaDashboard && typeof window.OrbitaDashboard.destroyCharts === 'function') {
            try { window.OrbitaDashboard.destroyCharts(); } catch (e) { }
        }
        if (window.OrbitaLive && typeof window.OrbitaLive.unregister === 'function') {
            try { window.OrbitaLive.unregister(getActiveLivePage()); } catch (e) { }
        }

        function getActiveLivePage() {
            var root = document.querySelector('[data-orbita-live]');
            return root ? root.getAttribute('data-orbita-live-page') : null;
        }

        // Show spinner immediately
        showPageLoading();

        // Ensure the browser actually paints the spinner before we start fetching
        // (important for very fast demo responses)
        await new Promise(function (resolve) {
            requestAnimationFrame(function () {
                requestAnimationFrame(resolve);
            });
        });

        var fetchStart = Date.now();

        try {
            var res = await fetch(targetPath, {
                method: 'GET',
                credentials: 'same-origin',
                headers: { 'X-Orbita-Content-Only': '1' }
            });

            if (!res.ok) {
                hidePageLoading();
                window.location.href = targetPath;
                return;
            }

            var html = await res.text();

            // Make sure the loading spinner is visible for at least this long
            // even in super-fast demo mode. This is why you asked for a spinner.
            var MIN_LOADING_MS = 300;
            var elapsed = Date.now() - fetchStart;
            var remaining = Math.max(0, MIN_LOADING_MS - elapsed);

            if (remaining > 0) {
                await new Promise(function (r) { setTimeout(r, remaining); });
            }

            hidePageLoading();

            // Swap content first, then run inits — must complete before page scripts / events
            content.style.transition = 'opacity 0.15s ease';
            content.style.opacity = '0.15';

            await new Promise(function (resolve) {
                requestAnimationFrame(function () {
                    content.innerHTML = html;
                    content.style.opacity = '1';
                    setTimeout(function () {
                        content.style.transition = '';
                    }, 200);
                    resolve();
                });
            });

            // meta for title + controller (read from new content)
            var meta = content.querySelector('.orbita-page-meta');
            var pageTitle = null;
            var pageKey = null;
            if (meta) {
                pageTitle = meta.getAttribute('data-orbita-page-title');
                pageKey = meta.getAttribute('data-orbita-controller');
                meta.parentNode.removeChild(meta);
            }
            if (pageTitle) {
                document.title = pageTitle;
            }

            if (push) {
                try { history.pushState({ orbitaNav: true }, '', targetPath); } catch (e) { }
            }

            updateActiveNav(targetPath);
            hidePageLoading();
            reinitAfterContentSwap();

            await ensurePageScripts(targetPath, pageKey);

            // Let page-specific scripts re-init their elements (they listen to this)
            document.dispatchEvent(new CustomEvent('orbita:content-updated', {
                detail: { path: targetPath, key: pageKey }
            }));

            if (pageKey && pageKey.toLowerCase() === 'dashboard'
                && window.OrbitaDashboard
                && typeof window.OrbitaDashboard.reinit === 'function') {
                window.OrbitaDashboard.reinit();
            }
        } catch (err) {
            console.warn('Orbita fast nav failed, falling back', err);
            hidePageLoading();
            window.location.href = targetPath;
        }
    }

    function initClientNavigation() {
        var nav = document.querySelector('.orbita-nav');
        if (!nav) return;

        nav.addEventListener('click', function (e) {
            var link = e.target.closest('a.nav-item');
            if (!link) return;

            var href = link.getAttribute('href');
            if (!href || href[0] === '#' || link.getAttribute('target') === '_blank') return;

            // Only intercept same-origin http(s) links
            try {
                var u = new URL(href, window.location.origin);
                if (u.origin !== window.location.origin) return;

                // Skip auth pages (login etc). Note: /accounts (plural) must NOT be skipped
                var p = u.pathname.toLowerCase();
                if (p === '/account' || p.startsWith('/account/')) return;

                e.preventDefault();
                navigateTo(u.pathname + u.search, true);
            } catch (ex) {
                // let browser handle
            }
        });

        window.addEventListener('popstate', function (ev) {
            // Only react to our history entries or always try
            var path = window.location.pathname + window.location.search;
            navigateTo(path, false);
        });

        // Initial sync (in case)
        updateActiveNav(window.location.pathname + window.location.search);
    }

    function initGeneralInternalNav() {
        // Use document-level delegation so it survives content replacements.
        // Intercept internal links (tabs like active/inactive, breadcrumbs, some cards) for fast nav + spinner.
        document.addEventListener('click', function (e) {
            var link = e.target.closest('a[href]');
            if (!link) return;

            // Already handled by sidebar nav
            if (link.closest('.orbita-nav')) return;

            // Skip special buttons that open modals or have other behavior
            if (link.classList.contains('workers-add-btn') ||
                link.hasAttribute('data-no-fast-nav') ||
                link.getAttribute('target') === '_blank' ||
                (link.getAttribute('href') || '').startsWith('#')) {
                return;
            }

            var href = link.getAttribute('href');
            if (!href) return;

            try {
                var u = new URL(href, window.location.origin);
                if (u.origin !== window.location.origin) return;

                var p = u.pathname.toLowerCase();
                if (p === '/account' || p.startsWith('/account/')) return;

                // Protect downloads and special links
                if (p.includes('/download') || 
                    link.classList.contains('workers-download-btn') ||
                    link.hasAttribute('download')) {
                    return;
                }

                e.preventDefault();
                navigateTo(u.pathname + u.search, true);
            } catch (ex) { }
        });

        // Intercept GET forms (search + filters) → fast nav + spinner instead of full reload
        document.addEventListener('submit', function (e) {
            var form = e.target.closest('form');
            if (!form || !form.closest('.orbita-content')) return;

            var method = (form.getAttribute('method') || 'get').toLowerCase();
            if (method !== 'get') return;

            var action = (form.getAttribute('action') || window.location.pathname).toLowerCase();
            if (action.includes('/account')) return;

            e.preventDefault();

            try {
                var formData = new FormData(form);
                var url = new URL(form.action || window.location.href, window.location.origin);

                // Clear existing and apply form values (so empty values are removed)
                // Keep existing non-form params if needed, but for our filters it's usually clean
                formData.forEach((value, key) => {
                    if (value !== '' && value != null) {
                        url.searchParams.set(key, value);
                    } else {
                        url.searchParams.delete(key);
                    }
                });

                navigateTo(url.pathname + url.search, true);
            } catch (ex) {
                form.submit(); // fallback
            }
        }, true);
    }

    initClientNavigation();
    initGeneralInternalNav();

    // Prefetch on hover for snappier feel (optional, light)
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
    window.Orbita.navigateTo = navigateTo;
    function getAntiForgeryToken() {
        var input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    }

    async function postForm(url, fields) {
        fields = fields || {};
        var body = new URLSearchParams();
        var token = getAntiForgeryToken();
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

    window.Orbita.toast = showToast;
    window.Orbita.copyText = copyText;
    window.Orbita.confirm = showConfirm;
    window.Orbita.postForm = postForm;
    window.Orbita.initWorkerRestartButtons = initWorkerRestartButtons;
    window.Orbita.openDetailModal = openDetailModal;
    window.Orbita.initFilterPanels = initFilterPanels;
    window.Orbita.initDetailOpenButtons = initDetailOpenButtons;
    window.Orbita.initRowMenus = initRowMenus;
    window.Orbita.closeAllRowMenus = closeAllRowMenus;
    window.Orbita.updateNavBadges = updateNavBadges;
    window.Orbita.fetchNavBadges = fetchNavBadges;
    window.Orbita.reinitLiveContent = reinitAfterContentSwap;
})();