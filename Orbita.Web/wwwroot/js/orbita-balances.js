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
        if (page.getAttribute('data-balances-bound') === '1') return;
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
            }
        });
        var firstQr = page.querySelector('[data-qr-select]');
        if (firstQr) selectQr(firstQr);
        initWorkerFilter();
        updateBulk();
    }

    initPage();
    document.addEventListener('orbita:content-updated', initPage);
})();
