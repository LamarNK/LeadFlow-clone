(function () {
    const versionPattern = /^Orbita\.Worker\.Setup-(\d+\.\d+\.\d+\.\d+)\.msi$/i;
    const maxUploadBytes = 536870912;

    const form = document.querySelector('[data-worker-release-upload]');
    if (!form) return;

    const fileInput = form.querySelector('.settings-release-file-input');
    const dropzone = form.querySelector('[data-worker-release-dropzone]');
    const versionInput = form.querySelector('[data-worker-release-version]');
    const fileNameEl = form.querySelector('[data-worker-release-file-name]');
    const fileErrorEl = form.querySelector('[data-worker-release-file-error]');
    const submitButton = form.querySelector('[data-worker-release-submit]');

    if (!fileInput || !dropzone || !versionInput || !fileNameEl || !fileErrorEl || !submitButton) {
        return;
    }

    function formatFileSize(bytes) {
        if (bytes < 1024) return bytes + ' B';
        const kb = bytes / 1024;
        if (kb < 1024) return kb.toFixed(1) + ' KB';
        return (kb / 1024).toFixed(1) + ' MB';
    }

    function parseVersion(fileName) {
        const match = versionPattern.exec(fileName.trim());
        return match ? match[1] : null;
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

    function clearSelection() {
        fileInput.value = '';
        versionInput.value = '';
        fileNameEl.textContent = '';
        fileNameEl.hidden = true;
        fileErrorEl.textContent = '';
        fileErrorEl.hidden = true;
        submitButton.disabled = true;
        setDropzoneState(null);
    }

    function applyFile(file) {
        if (!file) {
            clearSelection();
            return;
        }

        if (file.size > maxUploadBytes) {
            clearSelection();
            setDropzoneState('error');
            fileErrorEl.textContent = 'Файл больше 512 МБ.';
            fileErrorEl.hidden = false;
            return;
        }

        const isMsi = file.name.toLowerCase().endsWith('.msi');
        if (!isMsi) {
            clearSelection();
            setDropzoneState('error');
            fileErrorEl.textContent = 'Нужен файл с расширением .msi';
            fileErrorEl.hidden = false;
            return;
        }

        const version = parseVersion(file.name);
        const dataTransfer = new DataTransfer();
        dataTransfer.items.add(file);
        fileInput.files = dataTransfer.files;

        fileNameEl.textContent = file.name + ' · ' + formatFileSize(file.size);
        fileNameEl.hidden = false;

        if (!version) {
            versionInput.value = '';
            fileErrorEl.textContent = 'Имя файла должно быть Orbita.Worker.Setup-1.0.0.1.msi';
            fileErrorEl.hidden = false;
            submitButton.disabled = true;
            setDropzoneState('error');
            return;
        }

        versionInput.value = version;
        fileErrorEl.textContent = '';
        fileErrorEl.hidden = true;
        submitButton.disabled = false;
        setDropzoneState('ready');
    }

    dropzone.addEventListener('click', () => fileInput.click());

    dropzone.addEventListener('keydown', (event) => {
        if (event.key === 'Enter' || event.key === ' ') {
            event.preventDefault();
            fileInput.click();
        }
    });

    fileInput.addEventListener('change', () => {
        applyFile(fileInput.files?.[0] ?? null);
    });

    ['dragenter', 'dragover'].forEach((eventName) => {
        dropzone.addEventListener(eventName, (event) => {
            event.preventDefault();
            event.stopPropagation();
            setDropzoneState('dragover');
        });
    });

    ['dragleave', 'dragend'].forEach((eventName) => {
        dropzone.addEventListener(eventName, (event) => {
            event.preventDefault();
            event.stopPropagation();
            if (!dropzone.classList.contains('settings-release-dropzone--ready')
                && !dropzone.classList.contains('settings-release-dropzone--error')) {
                setDropzoneState(null);
            }
        });
    });

    dropzone.addEventListener('drop', (event) => {
        event.preventDefault();
        event.stopPropagation();
        const file = event.dataTransfer?.files?.[0] ?? null;
        applyFile(file);
    });

    form.addEventListener('submit', (event) => {
        if (!fileInput.files?.length || !versionInput.value.trim()) {
            event.preventDefault();
            fileErrorEl.textContent = 'Выберите корректный MSI-файл.';
            fileErrorEl.hidden = false;
            setDropzoneState('error');
        }
    });
})();