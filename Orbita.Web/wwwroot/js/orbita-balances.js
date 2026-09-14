(function () {
    'use strict';

    function initKpiCounters() {
        var shared = window.OrbitaLiveShared;
        if (shared && typeof shared.initializeKpiCounters === 'function') {
            shared.initializeKpiCounters('.balances-kpi-row [data-kpi-count]');
            return;
        }
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

        document.querySelectorAll('.balances-kpi-row [data-kpi-count]').forEach(function (el, index) {
            if (el.getAttribute('data-balances-kpi-initialized') === '1') return;

            var target = parseFloat(el.getAttribute('data-kpi-count'));
            if (isNaN(target)) return;

            el.setAttribute('data-balances-kpi-initialized', '1');
            var suffix = el.getAttribute('data-kpi-suffix') || '';

            if (reduced) {
                el.textContent = Math.round(target) + suffix;
                return;
            }

            if (shared && typeof shared.animateKpiValue === 'function') {
                shared.animateKpiValue(el, 0, target, suffix, 720, 80 + index * 70);
                return;
            }

            el.textContent = Math.round(target) + suffix;
        });
    }

    function initWorkerFilter() {
        var page = document.querySelector('[data-balances-page]');
        if (!page) return;
        var picker = page.querySelector('[data-balances-worker-picker]');
        if (!picker) return;
        if (picker.getAttribute('data-balances-worker-bound') === '1') return;
        picker.setAttribute('data-balances-worker-bound', '1');
        var form = picker.closest('form');
        var trigger = picker.querySelector('[data-balances-worker-trigger]');
        var triggerText = picker.querySelector('[data-balances-worker-trigger-text]');
        var menu = picker.querySelector('[data-balances-worker-menu]');
        var values = picker.querySelector('[data-balances-worker-values]');
        var all = picker.querySelector('[data-balances-worker-all]');
        var search = picker.querySelector('[data-balances-worker-search]');
        var apply = picker.querySelector('[data-balances-worker-apply]');
        var reset = picker.querySelector('[data-balances-worker-reset]');
        var options = Array.from(picker.querySelectorAll('[data-balances-worker-option]'));

        function sync() {
            var checked = options.filter(function (option) { return option.checked; });
            var hiddenCount = options.length - checked.length;
            all.checked = hiddenCount === 0;
            all.indeterminate = hiddenCount > 0 && checked.length > 0;
            triggerText.textContent = hiddenCount === 0 ? 'Все воркеры' : 'Скрыто: ' + hiddenCount;
            values.replaceChildren();
            options.filter(function (option) { return !option.checked; }).forEach(function (option) {
                var input = document.createElement('input');
                input.type = 'hidden';
                input.name = 'excludedWorkerIds';
                input.value = option.value;
                values.appendChild(input);
            });
        }

        function scheduleSubmit() {
            if (picker.__orbitaBalanceFilterTimer) window.clearTimeout(picker.__orbitaBalanceFilterTimer);
            picker.__orbitaBalanceFilterTimer = window.setTimeout(function () {
                picker.__orbitaBalanceFilterTimer = null;
                if (form) form.requestSubmit();
            }, 300);
        }

        trigger.addEventListener('click', function () {
            menu.hidden = !menu.hidden;
            trigger.setAttribute('aria-expanded', String(!menu.hidden));
            if (!menu.hidden && search) search.focus();
        });
        all.addEventListener('change', function () {
            options.forEach(function (option) { option.checked = all.checked; });
            sync();
            scheduleSubmit();
        });
        options.forEach(function (option) {
                option.addEventListener('change', function () {
                    sync();
                    scheduleSubmit();
                });
        });
        if (apply) apply.addEventListener('click', function () {
            menu.hidden = true;
            trigger.setAttribute('aria-expanded', 'false');
            form.requestSubmit();
        });
        reset.addEventListener('click', function () {
            options.forEach(function (option) { option.checked = true; });
            values.replaceChildren();
            sync();
            form.requestSubmit();
        });
        if (search) {
            search.addEventListener('input', function () {
                var query = search.value.trim().toLocaleLowerCase();
                picker.querySelectorAll('[data-balances-worker-option-row]').forEach(function (row) {
                    row.hidden = query && !row.dataset.searchText.toLocaleLowerCase().includes(query);
                });
            });
        }
        document.addEventListener('click', function (event) {
            if (!picker.contains(event.target)) {
                menu.hidden = true;
                trigger.setAttribute('aria-expanded', 'false');
            }
        });
        sync();
    }

    var pollTimer = null;
    var qrCountdownTimer = null;
    var qrListCountdownTimer = null;
    var selectedQrStorageKey = 'orbita-balances-selected-qr';

    function statusLabel(status) {
        return ({
            requested: 'В очереди',
            started: 'В работе',
            payment_claimed: 'В работе',
            qr_ready: 'QR готов',
            awaiting_balance: 'Проверка оплаты',
            completed: 'Подтверждено',
            verification_required: 'Проверка оплаты',
            failed: 'Ошибка',
            expired: 'Истекло',
            cancelled: 'Отменено'
        })[status] || status || 'Низкий баланс';
    }

    function isLiveStatus(status) {
        return status === 'requested'
            || status === 'started'
            || status === 'payment_claimed'
            || status === 'awaiting_balance';
    }

    function belongsToTab(tab, status) {
        if (!status) return tab === 'low' || tab === 'all' || !tab;
        if (tab === 'all') return true;
        if (tab === 'queue') return status === 'requested' || status === 'started';
        if (tab === 'working') return status === 'payment_claimed' || status === 'qr_ready';
        if (tab === 'awaiting') return status === 'awaiting_balance' || status === 'verification_required';
        if (tab === 'history') return true;
        return false;
    }

    function stopStatusPoll() {
        if (pollTimer) {
            window.clearInterval(pollTimer);
            pollTimer = null;
        }
    }

    function stopQrCountdown() {
        if (qrCountdownTimer) {
            window.clearInterval(qrCountdownTimer);
            qrCountdownTimer = null;
        }
    }

    function stopQrListCountdown() {
        if (qrListCountdownTimer) {
            window.clearInterval(qrListCountdownTimer);
            qrListCountdownTimer = null;
        }
    }

    function formatRemainingTime(milliseconds) {
        var totalSeconds = Math.max(0, Math.ceil(milliseconds / 1000));
        var minutes = Math.floor(totalSeconds / 60);
        var seconds = totalSeconds % 60;
        return String(minutes).padStart(2, '0') + ':' + String(seconds).padStart(2, '0');
    }

    function getSelectedQrId() {
        try {
            return window.sessionStorage.getItem(selectedQrStorageKey) || '';
        } catch (_) {
            return '';
        }
    }

    function saveSelectedQrId(sessionId) {
        try {
            window.sessionStorage.setItem(selectedQrStorageKey, sessionId);
        } catch (_) { }
    }

    function token() {
        var meta = document.querySelector('meta[name="orbita-antiforgery-token"]');
        return meta ? meta.content : '';
    }
    function post(url, body) {
        var csrf = token();
        return fetch(url, {
            method: 'POST',
            credentials: 'same-origin',
            headers: {
                'Accept': 'application/json',
                'Content-Type': 'application/x-www-form-urlencoded;charset=UTF-8',
                'RequestVerificationToken': csrf
            },
            body: body
        }).then(function (response) {
            return response.text().then(function (text) {
                var data = {};
                try { data = text ? JSON.parse(text) : {}; } catch (_) { }
                if (!response.ok) throw new Error(data.error || 'Не удалось выполнить действие.');
                return data;
            });
        });
    }
    function initPage() {
        var page = document.querySelector('[data-balances-page]');
        if (!page) return;
        initKpiCounters();
        if (page.getAttribute('data-balances-bound') === '1') {
            startStatusPoll(page);
            return;
        }
        page.setAttribute('data-balances-bound', '1');
        function updateQrListTimers() {
            var hasTimers = false;
            page.querySelectorAll('[data-qr-list-timer]').forEach(function (timer) {
                hasTimers = true;
                var expiresAt = Date.parse(timer.dataset.qrListExpiresAt || '');
                if (Number.isNaN(expiresAt)) {
                    timer.textContent = '';
                    return;
                }

                var remaining = expiresAt - Date.now();
                timer.textContent = remaining > 0
                    ? 'Осталось: ' + formatRemainingTime(remaining)
                    : 'Время оплаты истекло';
                timer.classList.toggle('is-expired', remaining <= 0);
            });
            return hasTimers;
        }

        stopQrListCountdown();
        if (updateQrListTimers()) {
            qrListCountdownTimer = window.setInterval(updateQrListTimers, 1000);
        }

        function selected() {
            return Array.from(page.querySelectorAll('[data-balance-select]:checked'));
        }
        function updateBulk() {
            var count = selected().length;
            var label = page.querySelector('[data-balances-selected]');
            var button = page.querySelector('[data-balances-batch]');
            if (label) label.textContent = count + ' выбрано';
            if (button) button.disabled = count === 0;
            page.querySelectorAll('[data-balance-row]').forEach(function (row) {
                var box = row.querySelector('[data-balance-select]');
                row.classList.toggle('is-selected', !!(box && box.checked));
            });
        }
        function selectQr(button) {
            page.querySelectorAll('[data-qr-select]').forEach(function (item) { item.classList.toggle('is-active', item === button); });
            saveSelectedQrId(button.dataset.qrSelect || '');
            var image = page.querySelector('[data-qr-image]');
            if (image) {
                image.src = button.dataset.qrSrc || '';
                image.hidden = !button.dataset.qrSrc;
            }
            page.querySelector('[data-qr-account-label]').textContent = button.dataset.qrAccount;
            page.querySelector('[data-qr-profile-label]').textContent = button.dataset.qrProfile;
            page.querySelector('[data-qr-amount-label]').textContent = button.dataset.qrAmount;
            var paid = page.querySelector('[data-qr-paid]');
            paid.dataset.sessionId = button.dataset.qrSelect;
            paid.disabled = false;
            var cancel = page.querySelector('[data-qr-cancel]');
            cancel.dataset.sessionId = button.dataset.qrSelect;
            cancel.disabled = false;
            var timer = page.querySelector('[data-qr-timer]');
            var expiresAt = Date.parse(button.dataset.qrExpiresAt || '');

            stopQrCountdown();
            if (!timer || Number.isNaN(expiresAt)) return;

            function updateQrCountdown() {
                var remaining = expiresAt - Date.now();
                if (remaining <= 0) {
                    timer.textContent = 'Время оплаты истекло. Сессия отменяется автоматически.';
                    timer.classList.add('is-expired');
                    timer.hidden = false;
                    paid.disabled = true;
                    cancel.disabled = true;
                    stopQrCountdown();
                    post(page.dataset.cancelUrl + '?sessionId=' + encodeURIComponent(button.dataset.qrSelect), new URLSearchParams())
                        .then(function () { location.reload(); })
                        .catch(function () { window.setTimeout(function () { location.reload(); }, 3000); });
                    return;
                }

                timer.textContent = 'Осталось на оплату: ' + formatRemainingTime(remaining);
                timer.classList.remove('is-expired');
                timer.hidden = false;
            }

            updateQrCountdown();
            qrCountdownTimer = window.setInterval(updateQrCountdown, 1000);
        }
        page.addEventListener('change', function (event) {
            if (event.target.matches('[data-balances-select-all]')) {
                page.querySelectorAll('[data-balance-select]:not(:disabled)').forEach(function (box) {
                    box.checked = event.target.checked;
                });
            }
            if (event.target.matches('[data-balance-select], [data-balances-select-all]')) updateBulk();
        });
        page.addEventListener('click', function (event) {
            var single = event.target.closest('[data-single-topup]');
            if (single) {
                var box = single.closest('[data-balance-row]').querySelector('[data-balance-select]');
                box.checked = true;
                updateBulk();
                page.querySelector('[data-balances-batch]').click();
                return;
            }
            var batch = event.target.closest('[data-balances-batch]');
            if (batch) {
                var boxes = selected();
                if (!boxes.length || !window.confirm('Запросить пополнение для ' + boxes.length + ' субпрофилей?')) return;
                var body = new URLSearchParams();
                boxes.forEach(function (box, index) {
                    var row = box.closest('[data-balance-row]');
                    body.append('selections[' + index + '].WorkerId', row.dataset.workerId);
                    body.append('selections[' + index + '].AccountId', row.dataset.accountId);
                    body.append('selections[' + index + '].SubProfileId', row.dataset.subprofileId);
                });
                batch.disabled = true;
                post(page.dataset.batchUrl, body).then(function (data) {
                    var results = data && Array.isArray(data.results) ? data.results : [];
                    var failed = results.filter(function (result) { return !result.success; });
                    var created = results.length - failed.length;
                    if (!failed.length) {
                        location.reload();
                        return;
                    }

                    var errors = failed.map(function (result) {
                        return result.error || 'Не удалось создать сессию пополнения.';
                    });
                    var message = created
                        ? 'Запрошено: ' + created + '. Не запрошено: ' + failed.length + '.\n\n' + errors.join('\n')
                        : 'Не удалось запросить пополнение.\n\n' + errors.join('\n');
                    alert(message);
                    location.reload();
                })
                    .catch(function (error) { alert(error.message); batch.disabled = false; });
                return;
            }
            var paid = event.target.closest('[data-topup-paid]');
            if (paid) {
                post(page.dataset.markPaidUrl + '?sessionId=' + encodeURIComponent(paid.dataset.topupPaid), new URLSearchParams())
                    .then(function () { location.reload(); }).catch(function (error) { alert(error.message); });
                return;
            }
            var cancel = event.target.closest('[data-topup-cancel]');
            if (cancel && window.confirm('Отменить сессию пополнения? Перед отменой проверим историю операций Avito.')) {
                post(page.dataset.cancelUrl + '?sessionId=' + encodeURIComponent(cancel.dataset.topupCancel), new URLSearchParams())
                    .then(function () { location.reload(); }).catch(function (error) { alert(error.message); });
                return;
            }
            var qr = event.target.closest('[data-qr-select]');
            if (qr) selectQr(qr);
            var qrPaid = event.target.closest('[data-qr-paid]');
            if (qrPaid && qrPaid.dataset.sessionId) {
                post(page.dataset.markPaidUrl + '?sessionId=' + encodeURIComponent(qrPaid.dataset.sessionId), new URLSearchParams())
                    .then(function () { location.reload(); }).catch(function (error) { alert(error.message); });
                return;
            }
            var qrCancel = event.target.closest('[data-qr-cancel]');
            if (qrCancel && qrCancel.dataset.sessionId && window.confirm('Отменить сессию пополнения? Перед отменой проверим историю операций Avito.')) {
                post(page.dataset.cancelUrl + '?sessionId=' + encodeURIComponent(qrCancel.dataset.sessionId), new URLSearchParams())
                    .then(function () { location.reload(); }).catch(function (error) { alert(error.message); });
                return;
            }
            var row = event.target.closest('[data-balance-row]');
            if (!row || event.target.closest('button, a, input, label')) return;
            var box = row.querySelector('[data-balance-select]');
            if (!box || box.disabled) return;
            box.checked = !box.checked;
            updateBulk();
        });
        var selectedQrId = getSelectedQrId();
        var selectedQr = Array.from(page.querySelectorAll('[data-qr-select]')).find(function (item) {
            return item.dataset.qrSelect === selectedQrId;
        });
        var firstQr = selectedQr || page.querySelector('[data-qr-select]');
        if (firstQr) selectQr(firstQr);
        initWorkerFilter();
        updateBulk();
        startStatusPoll(page);
    }

    function rowKey(workerId, accountId, subProfileId) {
        return String(workerId || '').toLowerCase() + '|' + String(accountId || '').toLowerCase() + '|' + String(subProfileId || '');
    }

    function sessionField(session, camel, pascal) {
        if (!session) return '';
        return session[camel] || session[pascal] || '';
    }

    function topUpWorkspaceSignature(sessions) {
        return (sessions || [])
            .filter(function (session) {
                var status = sessionField(session, 'status', 'Status');
                return status === 'requested'
                    || status === 'started'
                    || status === 'payment_claimed'
                    || status === 'qr_ready';
            })
            .map(function (session) {
                return String(sessionField(session, 'id', 'Id')).replace(/-/g, '').toLowerCase()
                    + ':' + String(sessionField(session, 'status', 'Status')).toLowerCase();
            })
            .sort()
            .join('|');
    }

    function applyStatus(rowEl, session) {
        var badge = rowEl.querySelector('[data-balance-status] .balance-status');
        var detail = rowEl.querySelector('[data-balance-status-detail]');
        var status = sessionField(session, 'status', 'Status');
        var text = sessionField(session, 'progressMessage', 'ProgressMessage')
            || sessionField(session, 'failureMessage', 'FailureMessage');
        rowEl.dataset.sessionId = sessionField(session, 'id', 'Id');
        rowEl.dataset.sessionStatus = status;
        if (badge) {
            badge.className = 'balance-status balance-status--' + (status || 'low');
            badge.textContent = status ? statusLabel(status) : 'Низкий баланс';
        }
        if (detail) {
            detail.textContent = text;
            if (text) detail.removeAttribute('hidden');
            else detail.setAttribute('hidden', '');
        }
    }

    function startStatusPoll(page) {
        stopStatusPoll();
        var snapshotUrl = page.dataset.snapshotUrl;
        if (!snapshotUrl) return;
        var workspace = page.querySelector('[data-topup-workspace]');
        var hasLive = Array.from(page.querySelectorAll('[data-session-status]')).some(function (row) {
            return isLiveStatus(row.dataset.sessionStatus);
        }) || !!workspace;
        if (!hasLive) return;

        pollTimer = window.setInterval(function () {
            if (!document.contains(page)) {
                stopStatusPoll();
                return;
            }
            fetch(snapshotUrl, { credentials: 'same-origin', headers: { 'Accept': 'application/json' } })
                .then(function (response) { return response.ok ? response.json() : null; })
                .then(function (data) {
                    var rows = data.rows || data.Rows;
                    if (!data || !Array.isArray(rows)) return;
                    var tab = page.dataset.balancesTab || 'low';
                    var sessions = data.sessions || data.Sessions || [];
                    var byKey = {};
                    rows.forEach(function (row) {
                        byKey[rowKey(row.workerId || row.WorkerId, row.accountId || row.AccountId, row.subProfileId || row.SubProfileId)] = row;
                    });
                    var shouldReload = false;
                    page.querySelectorAll('[data-balance-row]').forEach(function (rowEl) {
                        var next = byKey[rowKey(rowEl.dataset.workerId, rowEl.dataset.accountId, rowEl.dataset.subprofileId)];
                        if (!next) {
                            if (tab !== 'all') shouldReload = true;
                            return;
                        }
                        var previous = rowEl.dataset.sessionStatus || '';
                        var session = next && (next.session || next.Session);
                        var nextStatus = sessionField(session, 'status', 'Status');
                        if (previous && nextStatus && previous !== nextStatus && !belongsToTab(tab, nextStatus)) {
                            shouldReload = true;
                        }
                        var cooldownActive = !!(next.topUpCooldownActive || next.TopUpCooldownActive);
                        if (previous && !nextStatus && cooldownActive) {
                            shouldReload = true;
                        }
                        var sessionId = sessionField(session, 'id', 'Id');
                        if (nextStatus === 'qr_ready' && previous !== 'qr_ready' && !page.querySelector('[data-qr-select="' + sessionId + '"]')) {
                            shouldReload = true;
                        }
                        applyStatus(rowEl, session);
                    });
                    if (workspace
                        && Array.isArray(sessions)
                        && workspace.dataset.topupWorkspaceSignature !== topUpWorkspaceSignature(sessions)) {
                        shouldReload = true;
                    }
                    if (shouldReload) {
                        stopStatusPoll();
                        location.reload();
                    }
                })
                .catch(function () { });
        }, 2000);
    }

    initPage();
    document.addEventListener('orbita:content-updated', function () {
        stopStatusPoll();
        stopQrCountdown();
        stopQrListCountdown();
        initPage();
    });
})();
