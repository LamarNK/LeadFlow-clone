(function () {
    const serviceLogClampClass = 'service-log-entry__message--clamped';

    function runSettingsInits() {
        document.querySelectorAll('[data-service-log-message]').forEach((messageEl) => {
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
            if (button.__orbitaCopyBound) return;
            button.__orbitaCopyBound = true;

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

        const editUserDialog = document.getElementById('editUserDialog');
        if (editUserDialog) {
            const form = document.getElementById('editUserForm');
            const userIdInput = document.getElementById('editUserId');
            const userEmailLabel = document.getElementById('editUserEmail');
            const fullNameInput = document.getElementById('editUserFullName');
            const originalFullNameInput = document.getElementById('editUserOriginalFullName');
            const roleInput = document.getElementById('editUserRole');
            const originalRoleInput = document.getElementById('editUserOriginalRole');
            const officeInput = document.getElementById('editUserOffice');
            const originalOfficeInput = document.getElementById('editUserOriginalOfficeId');
            const passwordInput = document.getElementById('editUserPassword');
            const accessFields = document.getElementById('editUserAccessFields');
            const officeField = document.getElementById('editUserOfficeField');
            const passwordField = document.getElementById('editUserPasswordField');

            const updateOfficeState = () => {
                if (!form || !roleInput || !officeInput || !officeField) return;
                const isCurrentUser = form.dataset.currentUser === 'true';
                const isAdmin = roleInput.value === 'Admin';
                officeField.hidden = isCurrentUser || isAdmin;
                officeInput.disabled = isCurrentUser || isAdmin;

                if (!isCurrentUser && !isAdmin && !officeInput.value && officeInput.options.length > 0) {
                    officeInput.selectedIndex = 0;
                }
            };

            document.querySelectorAll('[data-settings-edit-user]').forEach((button) => {
                if (button.__orbitaEditUserBound) return;
                button.__orbitaEditUserBound = true;

                button.addEventListener('click', () => {
                    if (!form || !userIdInput || !userEmailLabel || !fullNameInput || !originalFullNameInput
                        || !roleInput || !originalRoleInput || !officeInput || !originalOfficeInput
                        || !passwordInput || !accessFields || !passwordField) return;

                    const isCurrentUser = button.getAttribute('data-user-is-current') === 'true';
                    const fullName = button.getAttribute('data-user-full-name') || '';
                    const role = button.getAttribute('data-user-role') || '';
                    const officeId = button.getAttribute('data-user-office-id') || '';

                    form.dataset.currentUser = String(isCurrentUser);
                    userIdInput.value = button.getAttribute('data-user-id') || '';
                    userEmailLabel.textContent = button.getAttribute('data-user-email') || '';
                    fullNameInput.value = fullName;
                    originalFullNameInput.value = fullName;
                    roleInput.value = role;
                    originalRoleInput.value = role;
                    officeInput.value = officeId;
                    originalOfficeInput.value = officeId;
                    passwordInput.value = '';
                    accessFields.hidden = isCurrentUser;
                    passwordField.hidden = isCurrentUser;
                    updateOfficeState();

                    if (typeof editUserDialog.showModal === 'function') {
                        editUserDialog.showModal();
                    }
                });
            });

            if (!editUserDialog.__orbitaDialogBound) {
                editUserDialog.__orbitaDialogBound = true;
                roleInput?.addEventListener('change', updateOfficeState);

                document.querySelectorAll('[data-settings-dialog-close]').forEach((button) => {
                    button.addEventListener('click', () => button.closest('dialog')?.close());
                });

                editUserDialog.addEventListener('click', (event) => {
                    if (event.target === editUserDialog) {
                        editUserDialog.close();
                    }
                });
            }
        }

        document.querySelectorAll('[data-settings-copy-api-key]').forEach((button) => {
            if (button.__orbitaApiKeyCopyBound) return;
            button.__orbitaApiKeyCopyBound = true;

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
