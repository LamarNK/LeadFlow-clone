(function () {
    const maxUploadBytes = 104857600;
    const form = document.querySelector('[data-leadflow-import-form]');
    if (!form) return;

    const fileInput = form.querySelector('[data-leadflow-import-file]');
    const dropzone = form.querySelector('[data-leadflow-import-dropzone]');
    const officeSelect = form.querySelector('[data-leadflow-import-office]');
    const encryptionKeyInput = form.querySelector('[data-leadflow-import-key]');
    const fileNameEl = form.querySelector('[data-leadflow-import-file-name]');
    const fileErrorEl = form.querySelector('[data-leadflow-import-file-error]');
    const previewButton = form.querySelector('[data-leadflow-import-preview]');
    const executeAllButton = document.querySelector('[data-leadflow-import-execute-all]');
    const executeSampleButton = document.querySelector('[data-leadflow-import-execute-sample]');
    const statusEl = form.querySelector('[data-leadflow-import-status]');
    const previewCard = document.querySelector('[data-leadflow-import-preview-card]');
    const previewBody = document.querySelector('[data-leadflow-import-preview-body]');
    const previewSummary = document.querySelector('[data-leadflow-import-summary]');
    const previewNote = document.querySelector('[data-leadflow-import-preview-note]');
    const selectAllWrap = document.querySelector('[data-leadflow-import-select-all-wrap]');
    const selectAllCheckbox = document.querySelector('[data-leadflow-import-select-all]');
    const antiForgeryToken = form.querySelector('input[name="__RequestVerificationToken"]')?.value || '';

    let currentPreview = null;

    function setStatus(message, tone) {
        if (!statusEl) return;
        statusEl.textContent = message || '';
        statusEl.hidden = !message;
        statusEl.classList.remove('settings-alert--success', 'settings-alert--error');
        if (tone === 'success') statusEl.classList.add('settings-alert--success');
        if (tone === 'error') statusEl.classList.add('settings-alert--error');
    }

    function setDropzoneState(state) {
        dropzone.classList.remove(
            'settings-release-dropzone--dragover',
            'settings-release-dropzone--ready',
            'settings-release-dropzone--error'
        );
        if (state) {
            dropzone.classList.add('settings-release-dropzone--' + state);
        }
    }

    function formatFileSize(bytes) {
        if (bytes < 1024) return bytes + ' B';
        const kb = bytes / 1024;
        if (kb < 1024) return kb.toFixed(1) + ' KB';
        return (kb / 1024).toFixed(1) + ' MB';
    }

    function updatePreviewButtonState() {
        const hasFile = fileInput.files && fileInput.files.length > 0;
        const hasOffice = !!officeSelect.value;
        previewButton.disabled = !(hasFile && hasOffice);
    }

    function clearSelection() {
        fileInput.value = '';
        fileNameEl.textContent = '';
        fileNameEl.hidden = true;
        fileErrorEl.textContent = '';
        fileErrorEl.hidden = true;
        setDropzoneState(null);
        updatePreviewButtonState();
    }

    function applyFile(file) {
        if (!file) {
            clearSelection();
            return;
        }

        if (file.size > maxUploadBytes) {
            clearSelection();
            setDropzoneState('error');
            fileErrorEl.textContent = 'Файл больше 100 МБ.';
            fileErrorEl.hidden = false;
            return;
        }

        if (!file.name.toLowerCase().endsWith('.db')) {
            clearSelection();
            setDropzoneState('error');
            fileErrorEl.textContent = 'Нужен файл leadflow.db';
            fileErrorEl.hidden = false;
            return;
        }

        const dataTransfer = new DataTransfer();
        dataTransfer.items.add(file);
        fileInput.files = dataTransfer.files;
        fileNameEl.textContent = file.name + ' · ' + formatFileSize(file.size);
        fileNameEl.hidden = false;
        fileErrorEl.hidden = true;
        setDropzoneState('ready');
        updatePreviewButtonState();
    }

    function importStateLabel(state) {
        switch (state) {
            case 'new': return 'Новый';
            case 'exists': return 'Уже в Орбите';
            case 'invalid': return 'Некорректный';
            default: return state;
        }
    }

    function statusLabel(status) {
        switch (status) {
            case 'Sent': return 'Отправлен';
            case 'Duplicate': return 'Дубль';
            case 'Error': return 'Ошибка';
            case 'InProgress': return 'В обработке';
            case 'ActionRequired': return 'Требует действия';
            case 'New': return 'Новый';
            default: return status || '—';
        }
    }

    function formatDate(value) {
        if (!value) return '—';
        const date = new Date(value);
        return Number.isNaN(date.getTime()) ? value : date.toLocaleString('ru-RU');
    }

    function renderPreview(preview) {
        currentPreview = preview;
        previewCard.hidden = false;
        executeAllButton.hidden = false;
        executeSampleButton.hidden = false;
        selectAllWrap.hidden = false;
        previewNote.hidden = false;

        const encryptedHint = preview.isEncrypted ? ' · база зашифрована' : '';
        previewSummary.textContent =
            'Офис: ' + preview.officeName +
            ' · всего ' + preview.totalInFile +
            ' · новых ' + preview.importableCount +
            ' · уже есть ' + preview.alreadyExistsCount +
            ' · пропуск ' + preview.invalidCount +
            encryptedHint;

        if (preview.importableCount > preview.previewSampleCount) {
            previewNote.textContent =
                'В таблице ниже — первые ' + preview.previewSampleCount +
                ' из ' + preview.importableCount +
                ' новых откликов для проверки. Кнопка «Импортировать все новые» загрузит всю базу целиком.';
        } else if (preview.importableCount > 0) {
            previewNote.textContent = 'Ниже полный список новых откликов для проверки перед импортом.';
        } else {
            previewNote.textContent = 'Новых откликов для импорта не найдено.';
        }

        previewBody.innerHTML = '';
        preview.items.forEach((item) => {
            const row = document.createElement('tr');
            const canSelect = item.canImport;
            row.innerHTML =
                '<td class="data-table-check-col">' +
                    (canSelect
                        ? '<input type="checkbox" data-leadflow-import-row-id="' + item.id + '" checked />'
                        : '') +
                '</td>' +
                '<td class="settings-log-time">' + formatDate(item.createdAt) + '</td>' +
                '<td>' + escapeHtml(item.fullName || '—') + '</td>' +
                '<td>' + escapeHtml(item.phoneRaw || '—') + '</td>' +
                '<td>' + escapeHtml(item.accountName || '—') + '</td>' +
                '<td>' + escapeHtml(item.vacancy || '—') + '</td>' +
                '<td>' + escapeHtml(statusLabel(item.status)) + '</td>' +
                '<td><span class="settings-status-pill settings-status-pill--' + (canSelect ? 'online' : 'offline') + '">' +
                    escapeHtml(importStateLabel(item.importState)) + '</span></td>';
            previewBody.appendChild(row);
        });

        updateExecuteButtons();
        if (selectAllCheckbox) {
            selectAllCheckbox.checked = true;
        }
    }

    function escapeHtml(value) {
        return String(value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function getSelectedIds() {
        return Array.from(previewBody.querySelectorAll('input[type="checkbox"][data-leadflow-import-row-id]:checked'))
            .map((el) => el.getAttribute('data-leadflow-import-row-id'));
    }

    function updateExecuteButtons() {
        if (!currentPreview) return;

        const selectedCount = getSelectedIds().length;
        const importable = currentPreview.importableCount || 0;

        executeAllButton.disabled = importable === 0;
        executeAllButton.textContent = 'Импортировать все новые (' + importable + ')';

        executeSampleButton.disabled = selectedCount === 0;
        executeSampleButton.textContent = selectedCount > 0
            ? 'Импортировать выбранные в таблице (' + selectedCount + ')'
            : 'Импортировать выбранные в таблице';
    }

    function hidePreviewActions() {
        previewCard.hidden = true;
        executeAllButton.hidden = true;
        executeSampleButton.hidden = true;
        selectAllWrap.hidden = true;
        previewNote.hidden = true;
        currentPreview = null;
    }

    async function previewImport() {
        if (!fileInput.files || fileInput.files.length === 0 || !officeSelect.value) {
            return;
        }

        previewButton.disabled = true;
        setStatus('Читаем базу…', null);

        const formData = new FormData();
        formData.append('databaseFile', fileInput.files[0]);
        formData.append('officeId', officeSelect.value);
        formData.append('__RequestVerificationToken', antiForgeryToken);
        if (encryptionKeyInput.value.trim()) {
            formData.append('encryptionKey', encryptionKeyInput.value.trim());
        }

        try {
            const response = await fetch('/Settings/PreviewLeadFlowImport', {
                method: 'POST',
                body: formData
            });
            const payload = await response.json();
            if (!response.ok) {
                throw new Error(payload.error || 'Не удалось выполнить предпросмотр.');
            }

            renderPreview(payload);
            setStatus(
                payload.importableCount > 0
                    ? 'Предпросмотр готов. Можно импортировать все ' + payload.importableCount + ' новых откликов или только выбранные строки в таблице.'
                    : 'Предпросмотр готов. Новых откликов для импорта нет.',
                'success'
            );
        } catch (error) {
            hidePreviewActions();
            setStatus(error.message || 'Ошибка предпросмотра.', 'error');
        } finally {
            updatePreviewButtonState();
        }
    }

    async function executeImport(importAll) {
        if (!currentPreview) return;

        const selectedIds = importAll ? [] : getSelectedIds();
        const count = importAll ? currentPreview.importableCount : selectedIds.length;
        if (count === 0) return;

        const message = importAll
            ? 'Импортировать все ' + count + ' новых откликов в офис «' + currentPreview.officeName + '»?'
            : 'Импортировать ' + count + ' откликов из таблицы предпросмотра в офис «' + currentPreview.officeName + '»?';

        if (!window.confirm(message)) {
            return;
        }

        executeAllButton.disabled = true;
        executeSampleButton.disabled = true;
        setStatus('Импортируем ' + count + ' откликов…', null);

        try {
            const formData = new FormData();
            formData.append('sessionId', currentPreview.sessionId);
            formData.append('selectedIdsJson', JSON.stringify(selectedIds));
            formData.append('__RequestVerificationToken', antiForgeryToken);

            const response = await fetch('/Settings/ExecuteLeadFlowImport', {
                method: 'POST',
                body: formData
            });
            const payload = await response.json();
            if (!response.ok) {
                throw new Error(payload.error || 'Не удалось выполнить импорт.');
            }

            setStatus(
                'Импорт завершён: добавлено ' + payload.imported +
                (payload.skipped ? ', пропущено ' + payload.skipped : '') +
                (payload.failed ? ', ошибок ' + payload.failed : '') + '.',
                'success'
            );
            hidePreviewActions();
        } catch (error) {
            setStatus(error.message || 'Ошибка импорта.', 'error');
            updateExecuteButtons();
        }
    }

    dropzone.addEventListener('click', () => fileInput.click());
    dropzone.addEventListener('keydown', (event) => {
        if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            fileInput.click();
        }
    });
    fileInput.addEventListener('change', () => applyFile(fileInput.files[0]));
    officeSelect.addEventListener('change', updatePreviewButtonState);
    previewButton.addEventListener('click', previewImport);
    executeAllButton?.addEventListener('click', () => executeImport(true));
    executeSampleButton?.addEventListener('click', () => executeImport(false));

    previewBody?.addEventListener('change', (event) => {
        if (event.target.matches('input[type="checkbox"][data-leadflow-import-row-id]')) {
            updateExecuteButtons();
        }
    });

    selectAllCheckbox?.addEventListener('change', () => {
        const checked = selectAllCheckbox.checked;
        previewBody.querySelectorAll('input[type="checkbox"][data-leadflow-import-row-id]').forEach((el) => {
            el.checked = checked;
        });
        updateExecuteButtons();
    });

    ['dragenter', 'dragover'].forEach((eventName) => {
        dropzone.addEventListener(eventName, (event) => {
            event.preventDefault();
            setDropzoneState('dragover');
        });
    });

    dropzone.addEventListener('dragleave', (event) => {
        if (!dropzone.contains(event.relatedTarget)) {
            setDropzoneState(fileInput.files.length > 0 ? 'ready' : null);
        }
    });

    dropzone.addEventListener('drop', (event) => {
        event.preventDefault();
        const file = event.dataTransfer?.files?.[0];
        applyFile(file || null);
    });
})();