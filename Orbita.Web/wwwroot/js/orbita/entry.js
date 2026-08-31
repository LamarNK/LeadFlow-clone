(function (runtime) {
    document.querySelector('.orbita-nav')?.addEventListener('mouseover', function (e) {
        var link = e.target.closest('a.nav-item');
        if (!link || !link.href || typeof runtime.prefetch !== 'function') return;
        try {
            var u = new URL(link.href, window.location.origin);
            if (u.origin !== window.location.origin) return;
            runtime.prefetch(u.pathname + u.search);
        } catch (ex) { }
    }, { passive: true });

    // Expose for other scripts (row clicks etc)
    window.Orbita = window.Orbita || {};
    window.Orbita.navigateTo = runtime.navigateTo;
    runtime.getAntiForgeryToken = function getAntiForgeryToken() {
        var input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    }

    runtime.postForm = async function postForm(url, fields) {
        fields = fields || {};
        var body = new URLSearchParams();
        var token = runtime.getAntiForgeryToken();
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

    window.Orbita.toast = runtime.showToast;
    window.Orbita.copyText = runtime.copyText;
    window.Orbita.confirm = runtime.showConfirm;
    window.Orbita.postForm = runtime.postForm;
    runtime.initCrmTaskAttachments = function initCrmTaskAttachments() {
        document.querySelectorAll('[data-crm-task-upload]').forEach(function (form) {
            if (form.hasAttribute('data-crm-task-upload-bound')) return;
            form.setAttribute('data-crm-task-upload-bound', '1');

            var input = form.querySelector('[data-crm-task-file-input]');
            var dropzone = form.querySelector('[data-crm-task-dropzone]');
            var status = form.querySelector('[data-crm-task-file-status]');
            var maxBytes = Number(form.dataset.maxBytes || 0);
            if (!input || !dropzone) return;

            function formatSize(bytes) {
                if (bytes >= 1024 * 1024) return (bytes / 1024 / 1024).toFixed(1).replace('.', ',') + ' МБ';
                if (bytes >= 1024) return Math.round(bytes / 1024) + ' КБ';
                return bytes + ' Б';
            }

            function selectFile(file) {
                if (!file) return false;
                if (maxBytes && file.size > maxBytes) {
                    if (status) status.textContent = 'Файл больше 20 МБ';
                    return false;
                }

                if (status) status.textContent = file.name + ' · ' + formatSize(file.size);
                return true;
            }

            input.addEventListener('change', function () {
                selectFile(input.files && input.files[0]);
            });

            ['dragenter', 'dragover'].forEach(function (eventName) {
                dropzone.addEventListener(eventName, function (event) {
                    event.preventDefault();
                    dropzone.classList.add('is-dragging');
                });
            });
            ['dragleave', 'drop'].forEach(function (eventName) {
                dropzone.addEventListener(eventName, function (event) {
                    event.preventDefault();
                    dropzone.classList.remove('is-dragging');
                });
            });
            dropzone.addEventListener('drop', function (event) {
                var files = event.dataTransfer && event.dataTransfer.files;
                var file = files && files[0];
                if (!selectFile(file)) return;
                input.files = files;
                form.requestSubmit();
            });
            dropzone.addEventListener('keydown', function (event) {
                if (event.key === 'Enter' || event.key === ' ') {
                    event.preventDefault();
                    input.click();
                }
            });
            form.addEventListener('submit', function (event) {
                if (!selectFile(input.files && input.files[0])) {
                    event.preventDefault();
                }
            });
        });
    };
    runtime.initCrmTaskEditButtons = function initCrmTaskEditButtons() {
        document.querySelectorAll('[data-crm-task-edit-open]').forEach(function (button) {
            if (button.hasAttribute('data-crm-task-edit-bound')) return;
            button.setAttribute('data-crm-task-edit-bound', '1');
            button.addEventListener('click', function () {
                var editor = document.getElementById(button.dataset.target || '');
                if (!editor) return;
                editor.open = true;
                editor.scrollIntoView({ behavior: 'smooth', block: 'start' });
                window.setTimeout(function () {
                    editor.querySelector('input[name="title"]')?.focus();
                }, 250);
            });
        });
    };
    runtime.initCrmClientTimes = function initCrmClientTimes() {
        function update() {
            document.querySelectorAll('[data-crm-client-time]').forEach(function (element) {
                var offset = Number(element.getAttribute('data-utc-offset-minutes'));
                if (!Number.isFinite(offset)) return;
                var clientDate = new Date(Date.now() + offset * 60000);
                element.textContent = String(clientDate.getUTCHours()).padStart(2, '0')
                    + ':' + String(clientDate.getUTCMinutes()).padStart(2, '0');
            });
        }

        update();
        if (!runtime.crmClientTimeTimer) {
            runtime.crmClientTimeTimer = window.setInterval(update, 30000);
        }
    };
    runtime.initCrmTaskAttachments();
    runtime.initCrmTaskEditButtons();
    runtime.initCrmClientTimes();
    runtime.initBitrixValidateButtons();
    window.Orbita.initWorkerRestartButtons = runtime.initWorkerRestartButtons;
    window.Orbita.initCrmTaskEditButtons = runtime.initCrmTaskEditButtons;
    window.Orbita.initCrmClientTimes = runtime.initCrmClientTimes;
    window.Orbita.initWorkerAccountEnableToggles = runtime.initWorkerAccountEnableToggles;
    window.Orbita.initProviderConnectionButtons = runtime.initProviderConnectionButtons;
    window.Orbita.initAvitoCredentialsButtons = runtime.initAvitoCredentialsButtons;
    window.Orbita.initLocalProfileSettingsButtons = runtime.initLocalProfileSettingsButtons;
    window.Orbita.initLocalAccountEditButtons = runtime.initLocalAccountEditButtons;
    window.Orbita.initLocalOpenBrowserButtons = runtime.initLocalOpenBrowserButtons;
    window.Orbita.openDetailModal = runtime.openDetailModal;
    window.Orbita.initFilterPanels = runtime.initFilterPanels;
    window.Orbita.initDetailOpenButtons = runtime.initDetailOpenButtons;
    window.Orbita.initRowMenus = runtime.initRowMenus;
    window.Orbita.initBitrixValidateButtons = runtime.initBitrixValidateButtons;
    window.Orbita.closeAllRowMenus = runtime.closeAllRowMenus;
    window.Orbita.updateNavBadges = runtime.updateNavBadges;
    window.Orbita.fetchNavBadges = runtime.fetchNavBadges;
    window.Orbita.reinitLiveContent = runtime.reinitAfterContentSwap;

    runtime.fetchNavBadges();
})(window.OrbitaRuntime = window.OrbitaRuntime || {});
