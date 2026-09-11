(function () {
    'use strict';
    var page = document.querySelector('[data-balances-page]');
    if (!page) return;

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
    updateBulk();
    window.setInterval(function () {
        if (!document.hidden && selected().length === 0) location.reload();
    }, 5000);
})();
