(function (runtime) {
    runtime.showToast = function showToast(message, options) {
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

    runtime.copyText = function copyText(text, successMessage) {
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
            if (ok) runtime.showToast(successMessage, { variant: 'success' });
            else runtime.showToast('Не удалось скопировать', { variant: 'error' });
            return ok;
        });
    }

    var confirmDialog = null;
    var confirmTitle = null;
    var confirmMessage = null;
    var confirmOkBtn = null;
    var confirmPending = null;
    var confirmPreviousFocus = null;

    runtime.initConfirmDialog = function initConfirmDialog() {
        confirmDialog = document.getElementById('orbitaConfirmDialog');
        if (!confirmDialog || confirmDialog.hasAttribute('data-orbita-confirm-ready')) return;

        confirmTitle = confirmDialog.querySelector('#orbitaConfirmTitle');
        confirmMessage = confirmDialog.querySelector('#orbitaConfirmMessage');
        confirmOkBtn = confirmDialog.querySelector('[data-orbita-confirm-ok]');

        confirmDialog.querySelectorAll('[data-orbita-confirm-cancel]').forEach(function (btn) {
            btn.addEventListener('click', function () {
                runtime.closeConfirm(false);
            });
        });

        if (confirmOkBtn) {
            confirmOkBtn.addEventListener('click', function () {
                runtime.closeConfirm(true);
            });
        }

        document.addEventListener('keydown', function (e) {
            if (e.key === 'Tab' && confirmDialog && !confirmDialog.hasAttribute('hidden')) {
                var buttons = confirmDialog.querySelectorAll('button:not(:disabled)');
                var first = buttons[0];
                var last = buttons[buttons.length - 1];
                if (e.shiftKey && document.activeElement === first) {
                    e.preventDefault();
                    last.focus();
                } else if (!e.shiftKey && document.activeElement === last) {
                    e.preventDefault();
                    first.focus();
                }
            }
            if (e.key === 'Escape' && confirmDialog && !confirmDialog.hasAttribute('hidden')) {
                runtime.closeConfirm(false);
            }
        });

        confirmDialog.setAttribute('data-orbita-confirm-ready', '1');
    }

    runtime.closeConfirm = function closeConfirm(confirmed) {
        if (!confirmDialog) return;
        confirmDialog.setAttribute('hidden', '');
        if (confirmPreviousFocus && confirmPreviousFocus.isConnected) confirmPreviousFocus.focus();
        confirmPreviousFocus = null;
        var resolver = confirmPending;
        confirmPending = null;
        if (resolver) {
            resolver.resolve(!!confirmed);
        }
    }

    runtime.showConfirm = function showConfirm(options) {
        options = options || {};
        runtime.initConfirmDialog();
        if (!confirmDialog || !confirmTitle || !confirmMessage || !confirmOkBtn) {
            return Promise.resolve(false);
        }

        confirmTitle.textContent = options.title || 'Подтвердите действие';
        confirmMessage.textContent = options.message || '';
        confirmOkBtn.textContent = options.confirmLabel || 'Подтвердить';
        confirmOkBtn.classList.toggle('orbita-confirm__btn--danger', options.variant === 'danger');

        confirmPreviousFocus = document.activeElement;
        confirmDialog.removeAttribute('hidden');
        confirmDialog.querySelector('button[data-orbita-confirm-cancel]').focus();

        return new Promise(function (resolve) {
            confirmPending = { resolve: resolve };
        });
    }

})(window.OrbitaRuntime = window.OrbitaRuntime || {});
