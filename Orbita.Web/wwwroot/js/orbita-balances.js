(function () {
    'use strict';
    var page = document.querySelector('[data-balances-page]');
    if (!page) return;

    function initWorkerFilter() {
        var picker = page.querySelector('[data-statistics-multiselect]');
        if (!picker) return;
        var fieldName = picker.dataset.statisticsField;
        var allLabel = picker.dataset.statisticsAllLabel || 'Все воркеры';
        var trigger = picker.querySelector('[data-statistics-multiselect-trigger]');
        var triggerText = picker.querySelector('[data-statistics-multiselect-text]');
        var menu = picker.querySelector('[data-statistics-multiselect-menu]');
        var values = picker.querySelector('[data-statistics-multiselect-values]');
        var all = picker.querySelector('[data-statistics-multiselect-all]');
        var search = picker.querySelector('[data-statistics-multiselect-search]');
        var options = Array.from(picker.querySelectorAll('[data-statistics-multiselect-option]'));
        var explicit = values.querySelectorAll('input').length > 0;

        function sync() {
            var checked = options.filter(function (option) { return option.checked; });
            if (!checked.length || checked.length === options.length) {
                explicit = false;
                options.forEach(function (option) { option.checked = true; });
                checked = options;
            }
            all.checked = !explicit;
            values.replaceChildren();
            if (explicit) {
                checked.forEach(function (option) {
                    var input = document.createElement('input');
                    input.type = 'hidden';
                    input.name = fieldName;
                    input.value = option.value;
                    values.appendChild(input);
                });
            }
            triggerText.textContent = explicit
                ? checked.length === 1
                    ? checked[0].parentElement.textContent.trim()
                    : 'Выбрано: ' + checked.length
                : allLabel;
        }

        trigger.addEventListener('click', function () {
            menu.hidden = !menu.hidden;
            trigger.setAttribute('aria-expanded', String(!menu.hidden));
            if (!menu.hidden && search) search.focus();
        });
        all.addEventListener('change', function () {
            explicit = false;
            options.forEach(function (option) { option.checked = true; });
            sync();
        });
        options.forEach(function (option) {
            option.addEventListener('change', function () {
                explicit = true;
                sync();
            });
        });
        if (search) {
            search.addEventListener('input', function () {
                var query = search.value.trim().toLocaleLowerCase();
                picker.querySelectorAll('[data-statistics-multiselect-option-row]').forEach(function (row) {
                    row.hidden = query && !row.dataset.statisticsSearchText.toLocaleLowerCase().includes(query);
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
    var firstQr = page.querySelector('[data-qr-select]');
    if (firstQr) selectQr(firstQr);
    initWorkerFilter();
    updateBulk();
})();
