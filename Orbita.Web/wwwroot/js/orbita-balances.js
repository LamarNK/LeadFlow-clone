(function () {
    'use strict';

    function initKpiCounters() {
        var shared = window.OrbitaLiveShared;
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
        }

        trigger.addEventListener('click', function () {
            menu.hidden = !menu.hidden;
            trigger.setAttribute('aria-expanded', String(!menu.hidden));
            if (!menu.hidden && search) search.focus();
        });
        all.addEventListener('change', function () {
            options.forEach(function (option) { option.checked = all.checked; });
            sync();
        });
        options.forEach(function (option) {
            option.addEventListener('change', function () {
                sync();
            });
        });
        apply.addEventListener('click', function () {
            values.replaceChildren();
            options.filter(function (option) { return !option.checked; }).forEach(function (option) {
                var input = document.createElement('input');
                input.type = 'hidden';
                input.name = 'excludedWorkerIds';
                input.value = option.value;
                values.appendChild(input);
            });
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

    function statusLabel(status) {
        return ({
            requested: 'В очереди',
            started: 'В работе',
            payment_claimed: 'В работе',
            qr_ready: 'QR готов',
            awaiting_balance: 'Ожидает баланс',
            completed: 'Подтверждено',
            verification_required: 'Требует проверки',
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
                post(page.dataset.batchUrl, body).then(function () { location.reload(); })
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
            if (cancel && window.confirm('Отменить сессию пополнения?')) {
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
            var row = event.target.closest('[data-balance-row]');
            if (!row || event.target.closest('button, a, input, label')) return;
            var box = row.querySelector('[data-balance-select]');
            if (!box || box.disabled) return;
            box.checked = !box.checked;
            updateBulk();
        });
        var firstQr = page.querySelector('[data-qr-select]');
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
        var hasLive = Array.from(page.querySelectorAll('[data-session-status]')).some(function (row) {
            return isLiveStatus(row.dataset.sessionStatus);
        });
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
                    var byKey = {};
                    rows.forEach(function (row) {
                        byKey[rowKey(row.workerId || row.WorkerId, row.accountId || row.AccountId, row.subProfileId || row.SubProfileId)] = row;
                    });
                    var shouldReload = false;
                    page.querySelectorAll('[data-balance-row]').forEach(function (rowEl) {
                        var next = byKey[rowKey(rowEl.dataset.workerId, rowEl.dataset.accountId, rowEl.dataset.subprofileId)];
                        var previous = rowEl.dataset.sessionStatus || '';
                        var session = next && (next.session || next.Session);
                        var nextStatus = sessionField(session, 'status', 'Status');
                        if (previous && nextStatus && previous !== nextStatus && !belongsToTab(tab, nextStatus)) {
                            shouldReload = true;
                        }
                        var sessionId = sessionField(session, 'id', 'Id');
                        if (nextStatus === 'qr_ready' && previous !== 'qr_ready' && !page.querySelector('[data-qr-select="' + sessionId + '"]')) {
                            shouldReload = true;
                        }
                        applyStatus(rowEl, session);
                    });
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
        initPage();
    });
})();
