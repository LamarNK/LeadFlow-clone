(function () {
    const serviceLogClampClass = 'service-log-entry__message--clamped';

    document.querySelectorAll('[data-service-log-message]').forEach((messageEl) => {
        messageEl.classList.add(serviceLogClampClass);

        requestAnimationFrame(() => {
            if (messageEl.scrollHeight <= messageEl.clientHeight + 2) {
                messageEl.classList.remove(serviceLogClampClass);
                return;
            }

            const expandButton = document.createElement('button');
            expandButton.type = 'button';
            expandButton.className = 'service-log-entry__expand';
            expandButton.textContent = 'Показать полностью';
            expandButton.addEventListener('click', () => {
                const isClamped = messageEl.classList.toggle(serviceLogClampClass);
                expandButton.textContent = isClamped ? 'Показать полностью' : 'Свернуть';
            });
            messageEl.insertAdjacentElement('afterend', expandButton);
        });
    });

    document.querySelectorAll('[data-service-log-copy]').forEach((button) => {
        button.addEventListener('click', async () => {
            const text = button.getAttribute('data-copy-text')?.trim() || '';
            if (!text) return;

            try {
                await navigator.clipboard.writeText(text);
                const label = button.querySelector('span');
                if (!label) return;
                const original = label.textContent;
                label.textContent = 'Скопировано';
                window.setTimeout(() => {
                    label.textContent = original;
                }, 1500);
            } catch {
                window.prompt('Скопируйте trace id:', text);
            }
        });
    });

    const resetDialog = document.getElementById('resetPasswordDialog');
    if (resetDialog) {
        const userIdInput = document.getElementById('resetPasswordUserId');
        const userEmailLabel = document.getElementById('resetPasswordUserEmail');

        document.querySelectorAll('[data-settings-reset-password]').forEach((button) => {
            button.addEventListener('click', () => {
                if (!userIdInput || !userEmailLabel) return;
                userIdInput.value = button.getAttribute('data-user-id') || '';
                userEmailLabel.textContent = button.getAttribute('data-user-email') || '';
                if (typeof resetDialog.showModal === 'function') {
                    resetDialog.showModal();
                }
            });
        });

        document.querySelectorAll('[data-settings-dialog-close]').forEach((button) => {
            button.addEventListener('click', () => resetDialog.close());
        });

        resetDialog.addEventListener('click', (event) => {
            if (event.target === resetDialog) {
                resetDialog.close();
            }
        });
    }

    function runSettingsInits() {
        const serviceLogClampClass = 'service-log-entry__message--clamped';

        document.querySelectorAll('[data-service-log-message]').forEach((messageEl) => {
            // avoid double clamp button on reinit
            if (messageEl.__orbitaClamped) return;
            messageEl.__orbitaClamped = true;
            messageEl.classList.add(serviceLogClampClass);

            requestAnimationFrame(() => {
                if (messageEl.scrollHeight <= messageEl.clientHeight + 2) {
                    messageEl.classList.remove(serviceLogClampClass);
                    return;
                }

                const expandButton = document.createElement('button');
                expandButton.type = 'button';
                expandButton.className = 'service-log-entry__expand';
                expandButton.textContent = 'Показать полностью';
                expandButton.addEventListener('click', () => {
                    const isClamped = messageEl.classList.toggle(serviceLogClampClass);
                    expandButton.textContent = isClamped ? 'Показать полностью' : 'Свернуть';
                });
                messageEl.insertAdjacentElement('afterend', expandButton);
            });
        });

        document.querySelectorAll('[data-service-log-copy]').forEach((button) => {
            button.addEventListener('click', async () => {
                const text = button.getAttribute('data-copy-text')?.trim() || '';
                if (!text) return;

                try {
                    await navigator.clipboard.writeText(text);
                    const label = button.querySelector('span');
                    if (!label) return;
                    const original = label.textContent;
                    label.textContent = 'Скопировано';
                    window.setTimeout(() => {
                        label.textContent = original;
                    }, 1500);
                } catch {
                    window.prompt('Скопируйте trace id:', text);
                }
            });
        });

        const resetDialog = document.getElementById('resetPasswordDialog');
        if (resetDialog) {
            const userIdInput = document.getElementById('resetPasswordUserId');
            const userEmailLabel = document.getElementById('resetPasswordUserEmail');

            document.querySelectorAll('[data-settings-reset-password]').forEach((button) => {
                button.addEventListener('click', () => {
                    if (!userIdInput || !userEmailLabel) return;
                    userIdInput.value = button.getAttribute('data-user-id') || '';
                    userEmailLabel.textContent = button.getAttribute('data-user-email') || '';
                    if (typeof resetDialog.showModal === 'function') {
                        resetDialog.showModal();
                    }
                });
            });

            document.querySelectorAll('[data-settings-dialog-close]').forEach((button) => {
                button.addEventListener('click', () => resetDialog.close());
            });

            resetDialog.addEventListener('click', (event) => {
                if (event.target === resetDialog) {
                    resetDialog.close();
                }
            });
        }

        document.querySelectorAll('[data-settings-copy-api-key]').forEach((button) => {
            button.addEventListener('click', async () => {
                const targetId = button.getAttribute('data-copy-target');
                const target = targetId ? document.getElementById(targetId) : null;
                if (!target) return;

                const text = target.textContent?.trim() || '';
                if (!text) return;

                if (window.Orbita && window.Orbita.copyText) {
                    window.Orbita.copyText(text);
                }
            });
        });
    }

    runSettingsInits();
    document.addEventListener('orbita:content-updated', runSettingsInits);
})();