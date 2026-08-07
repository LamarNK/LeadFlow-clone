(function (runtime) {
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

    runtime.initUpdatedClock = function initUpdatedClock() {
        if (window.OrbitaTime) {
            window.OrbitaTime.localizeAll();
        }
    }

    runtime.initUserMenu = function initUserMenu() {
        document.querySelectorAll('[data-orbita-user-menu]').forEach(function (menu) {
            if (menu.hasAttribute('data-orbita-user-menu-bound')) return;
            menu.setAttribute('data-orbita-user-menu-bound', '1');

            var trigger = menu.querySelector('.orbita-user-trigger');
            var dropdown = menu.querySelector('.orbita-user-dropdown');
            if (!trigger || !dropdown) return;

            trigger.addEventListener('click', function (e) {
                e.stopPropagation();
                var open = dropdown.hasAttribute('hidden');
                runtime.closeAllPopovers();
                if (open) {
                    dropdown.removeAttribute('hidden');
                    trigger.setAttribute('aria-expanded', 'true');
                }
            });
        });

        if (!window.__orbitaUserMenuDocListeners) {
            document.addEventListener('click', runtime.closeAllPopovers);
            document.addEventListener('keydown', function (e) {
                if (e.key === 'Escape') runtime.closeAllPopovers();
            });
            window.__orbitaUserMenuDocListeners = true;
        }
    }

    runtime.initPeriodPicker = function initPeriodPicker() {
        runtime.ensureLocalPeriodParams();
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
                runtime.closeAllPopovers();
                if (open) {
                    dropdown.removeAttribute('hidden');
                    trigger.setAttribute('aria-expanded', 'true');
                }
            });

            menu.querySelectorAll('[data-period-preset]').forEach(function (btn) {
                btn.addEventListener('click', function () {
                    var preset = btn.getAttribute('data-period-preset');
                    var range = runtime.resolvePresetRange(preset);
                    if (!range) return;
                    runtime.navigateWithPeriod(range.from, range.to);
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
                    runtime.navigateWithPeriod(from, to);
                });
            }
        });
    }

    runtime.resolvePresetRange = function resolvePresetRange(preset) {
        var today = runtime.formatIsoDate(new Date());
        if (preset === 'all') {
            return { from: runtime.formatIsoDate(runtime.addDays(new Date(), -365)), to: today };
        }
        if (preset === 'today') {
            return { from: today, to: today };
        }
        if (preset === 'yesterday') {
            var yesterday = runtime.formatIsoDate(runtime.addDays(new Date(), -1));
            return { from: yesterday, to: yesterday };
        }
        if (preset === '7d') {
            return { from: runtime.formatIsoDate(runtime.addDays(new Date(), -6)), to: today };
        }
        if (preset === '14d') {
            return { from: runtime.formatIsoDate(runtime.addDays(new Date(), -13)), to: today };
        }
        if (preset === '30d') {
            return { from: runtime.formatIsoDate(runtime.addDays(new Date(), -29)), to: today };
        }
        return null;
    }

    runtime.timeZoneOffsetMinutes = function timeZoneOffsetMinutes() {
        if (window.OrbitaTime && typeof window.OrbitaTime.timeZoneOffsetMinutes === 'function') {
            return window.OrbitaTime.timeZoneOffsetMinutes();
        }
        return new Date().getTimezoneOffset();
    }

    runtime.navigateWithPeriod = function navigateWithPeriod(from, to) {
        var url = new URL(window.location.href);
        url.searchParams.set('from', from);
        url.searchParams.set('to', to);
        url.searchParams.set('tz', String(runtime.timeZoneOffsetMinutes()));
        var target = url.pathname + url.search;
        // prefer fast client nav when available (keeps SPA feel)
        if (typeof runtime.navigateTo === 'function') {
            runtime.navigateTo(target, true);
        } else {
            window.location.href = target;
        }
    }

    runtime.addDays = function addDays(date, days) {
        var copy = new Date(date.getTime());
        copy.setDate(copy.getDate() + days);
        return copy;
    }

    runtime.formatIsoDate = function formatIsoDate(date) {
        if (window.OrbitaTime && typeof window.OrbitaTime.formatLocalDateIso === 'function') {
            return window.OrbitaTime.formatLocalDateIso(date);
        }
        var year = date.getFullYear();
        var month = String(date.getMonth() + 1).padStart(2, '0');
        var day = String(date.getDate()).padStart(2, '0');
        return year + '-' + month + '-' + day;
    }

    // Period pages always carry local from/to/tz (server never invents "today" alone).
    runtime.ensureLocalPeriodParams = function ensureLocalPeriodParams() {
        if (!document.querySelector('[data-orbita-period-menu]')) return;
        var url = new URL(window.location.href);
        var from = url.searchParams.get('from');
        var to = url.searchParams.get('to');
        var tz = url.searchParams.get('tz');
        var expectedTz = String(runtime.timeZoneOffsetMinutes());
        if (!from || !to) {
            var today = runtime.formatIsoDate(new Date());
            runtime.navigateWithPeriod(today, today);
            return;
        }
        if (tz !== expectedTz) {
            url.searchParams.set('tz', expectedTz);
            var target = url.pathname + url.search;
            if (typeof runtime.navigateTo === 'function') {
                runtime.navigateTo(target, true);
            } else {
                window.location.replace(target);
            }
        }
    };

    runtime.resetFloatingDropdown = function resetFloatingDropdown(dropdown) {
        if (!dropdown) return;
        dropdown.setAttribute('hidden', '');
        dropdown.classList.remove('row-menu-dropdown--floating');
        dropdown.style.top = '';
        dropdown.style.left = '';
        dropdown.style.right = '';
        dropdown.style.visibility = '';
    }

    runtime.positionFloatingDropdown = function positionFloatingDropdown(trigger, dropdown) {
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

    runtime.closeAllRowMenus = function closeAllRowMenus() {
        document.querySelectorAll('[data-row-menu]').forEach(function (menu) {
            var trigger = menu.querySelector('.row-menu-btn');
            var dropdown = menu.querySelector('.row-menu-dropdown');
            runtime.resetFloatingDropdown(dropdown);
            if (trigger) trigger.setAttribute('aria-expanded', 'false');
        });
    }

    runtime.handleRowMenuDocumentClick = function handleRowMenuDocumentClick(e) {
        var rowTrigger = e.target.closest('[data-row-menu] .row-menu-btn');
        if (rowTrigger) {
            e.stopPropagation();
            var menu = rowTrigger.closest('[data-row-menu]');
            var dropdown = menu && menu.querySelector('.row-menu-dropdown');
            if (!dropdown) return;

            var willOpen = dropdown.hasAttribute('hidden');
            runtime.closeAllRowMenus();
            if (willOpen) {
                runtime.positionFloatingDropdown(rowTrigger, dropdown);
                rowTrigger.setAttribute('aria-expanded', 'true');
            }
            return;
        }

        if (e.target.closest('.row-menu-dropdown')) {
            var sendBitrix = e.target.closest('[data-send-bitrix]');
            if (sendBitrix) {
                e.preventDefault();
                e.stopPropagation();
                e.stopImmediatePropagation();
                runtime.closeAllRowMenus();
                var responseId = sendBitrix.getAttribute('data-response-id');
                if (responseId && window.OrbitaResponses && typeof window.OrbitaResponses.openSendBitrixModal === 'function') {
                    window.OrbitaResponses.openSendBitrixModal(responseId);
                }
            }
            return;
        }

        runtime.closeAllRowMenus();
    }

    runtime.initRowMenus = function initRowMenus() {
        if (window.__orbitaRowMenuDelegationReady) return;
        window.__orbitaRowMenuDelegationReady = true;
        window.__orbitaRowMenuDocListeners = true;

        // Capture phase: run before other document click handlers (e.g. user menu close).
        document.addEventListener('click', runtime.handleRowMenuDocumentClick, true);
        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape') runtime.closeAllPopovers();
        });
        window.addEventListener('resize', runtime.closeAllRowMenus);
    }

    runtime.initLiveRowActions = function initLiveRowActions() {
        if (window.__orbitaLiveRowActionsReady) return;
        window.__orbitaLiveRowActionsReady = true;

        document.addEventListener('click', function (e) {
            var detailRow = e.target.closest('.events-row, .errors-row, .dash-event-row--detail');
            if (detailRow && !e.target.closest('a, button, .row-menu, input, select, label')) {
                runtime.openDetailFromRow(detailRow);
                return;
            }

            var copyResponseCard = e.target.closest('[data-copy-response-card]');
            if (copyResponseCard) {
                e.stopPropagation();
                var cardRow = copyResponseCard.closest('.responses-row');
                var cardText = '';
                if (cardRow) {
                    cardText = cardRow.getAttribute('data-response-card') || '';
                    if (!cardText && window.OrbitaResponses && typeof window.OrbitaResponses.getRowCardCopy === 'function') {
                        cardText = window.OrbitaResponses.getRowCardCopy(cardRow);
                    }
                }
                if (cardText) runtime.copyText(cardText, 'Карточка скопирована');
                runtime.closeAllRowMenus();
                return;
            }

            var copyPhone = e.target.closest('[data-copy-phone]');
            if (copyPhone) {
                e.stopPropagation();
                var row = copyPhone.closest('.responses-row');
                var phone = row ? row.getAttribute('data-phone') : '';
                if (phone) runtime.copyText(phone);
                runtime.closeAllRowMenus();
                return;
            }

            var copyEvent = e.target.closest('[data-copy-event]');
            if (copyEvent) {
                e.stopPropagation();
                var eventRow = copyEvent.closest('.events-row');
                var eventText = eventRow ? eventRow.getAttribute('data-copy') : '';
                if (eventText) runtime.copyText(eventText);
                runtime.closeAllRowMenus();
                return;
            }

            var copyError = e.target.closest('[data-copy-error]');
            if (copyError) {
                e.stopPropagation();
                var errorRow = copyError.closest('.errors-row');
                var errorText = errorRow ? errorRow.getAttribute('data-copy') : '';
                if (errorText) runtime.copyText(errorText);
                runtime.closeAllRowMenus();
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

        var result = await runtime.postForm(url, { eventId: eventId });
        if (result.ok) {
            var row = btn.closest(rowSelector);
            if (!row) {
                row = document.querySelector(rowSelector + '[data-event-id="' + eventId + '"]');
            }
            if (row && row.parentNode) row.parentNode.removeChild(row);
            runtime.showToast((result.payload && result.payload.message) || 'Готово', { variant: 'success' });
            runtime.closeDetailModal();
        } else {
            runtime.showToast((result.payload && result.payload.error) || 'Не удалось выполнить', { variant: 'error' });
        }
        runtime.closeAllRowMenus();
    }

    runtime.closeAllPopovers = function closeAllPopovers() {
        runtime.closeAllRowMenus();
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

})(window.OrbitaRuntime = window.OrbitaRuntime || {});
