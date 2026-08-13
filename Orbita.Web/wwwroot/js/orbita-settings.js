(function () {
    const serviceLogClampClass = 'service-log-entry__message--clamped';

    function openServiceLogDetail(entry) {
        const runtime = window.OrbitaRuntime;
        if (!runtime || typeof runtime.openDetailModal !== 'function') return;

        const message = entry.querySelector('[data-service-log-message]')?.textContent?.trim() || 'Сообщение отсутствует.';
        const time = entry.querySelector('time')?.textContent?.trim() || '—';
        const traceId = entry.querySelector('[data-service-log-copy]')?.getAttribute('data-copy-text')?.trim() || '';
        const level = entry.getAttribute('data-log-level')?.trim() || '—';
        const service = entry.getAttribute('data-log-service')?.trim() || '—';
        const source = entry.getAttribute('data-log-source')?.trim() || '—';
        const isTampered = entry.getAttribute('data-log-tampered') === 'true';

        const sections = [
            { label: 'Время', value: time },
            { label: 'Уровень', value: level },
            { label: 'Сервис', value: service },
            { label: 'Источник', value: source },
            { label: 'Целостность', value: isTampered ? 'Требует проверки' : 'Без замечаний' }
        ];
        if (traceId) {
            sections.splice(4, 0, { label: 'Trace ID', value: traceId });
        }

        runtime.openDetailModal({
            variant: 'log',
            title: 'Запись лога',
            subtitle: [level, service].filter(Boolean).join(' · '),
            sections,
            body: message,
            appendBody: true,
            copyText: message,
            copyLabel: 'Копировать сообщение'
        });
    }

    function initServiceLogDetails() {
        if (document.documentElement.dataset.serviceLogDetailsBound === 'true') return;
        document.documentElement.dataset.serviceLogDetailsBound = 'true';

        document.addEventListener('click', (event) => {
            if (event.target.closest('[data-service-log-copy], [data-service-log-expand]')) return;
            const entry = event.target.closest('[data-service-log-entry]');
            if (entry) openServiceLogDetail(entry);
        });

        document.addEventListener('keydown', (event) => {
            if (event.key !== 'Enter' && event.key !== ' ') return;
            if (event.target.closest('[data-service-log-copy], [data-service-log-expand]')) return;
            const entry = event.target.closest('[data-service-log-entry]');
            if (!entry) return;

            event.preventDefault();
            openServiceLogDetail(entry);
        });
    }

    function initUsersListFilters() {
        document.querySelectorAll('[data-settings-users]').forEach((root) => {
            if (root.__orbitaUsersFilterBound) return;
            root.__orbitaUsersFilterBound = true;

            const searchInput = root.querySelector('[data-settings-users-search]');
            const roleSelect = root.querySelector('[data-settings-users-role]');
            const officeSelect = root.querySelector('[data-settings-users-office]');
            const emptyState = root.querySelector('[data-settings-users-empty]');
            const groups = Array.from(root.querySelectorAll('[data-settings-user-group]'));
            const rows = Array.from(root.querySelectorAll('[data-settings-user-row]'));

            const apply = () => {
                const query = (searchInput?.value || '').trim().toLocaleLowerCase();
                const role = roleSelect?.value || '';
                const office = officeSelect?.value || '';
                let visibleRows = 0;

                rows.forEach((row) => {
                    const rowRole = row.getAttribute('data-user-role') || '';
                    const rowOffice = row.getAttribute('data-user-office-id') || '';
                    const rowKind = row.getAttribute('data-user-group-kind') || '';
                    const searchText = (row.getAttribute('data-user-search') || '').toLocaleLowerCase();

                    const matchesQuery = !query || searchText.includes(query);
                    const matchesRole = !role || rowRole === role;
                    let matchesOffice = true;
                    if (office === 'admins') {
                        matchesOffice = rowKind === 'admins' || rowRole === 'Admin';
                    } else if (office === 'unassigned') {
                        matchesOffice = rowKind === 'unassigned' || (!rowOffice && rowRole !== 'Admin');
                    } else if (office) {
                        matchesOffice = rowOffice === office;
                    }

                    const visible = matchesQuery && matchesRole && matchesOffice;
                    row.hidden = !visible;
                    if (visible) visibleRows += 1;
                });

                groups.forEach((group) => {
                    const groupRows = group.querySelectorAll('[data-settings-user-row]');
                    let groupVisible = 0;
                    groupRows.forEach((row) => {
                        if (!row.hidden) groupVisible += 1;
                    });
                    group.hidden = groupVisible === 0;
                    const countEl = group.querySelector('[data-settings-user-group-count]');
                    if (countEl) countEl.textContent = String(groupVisible);
                });

                if (emptyState) {
                    emptyState.hidden = visibleRows > 0 || rows.length === 0;
                }
            };

            searchInput?.addEventListener('input', apply);
            roleSelect?.addEventListener('change', apply);
            officeSelect?.addEventListener('change', apply);
        });
    }

    function initCreateUserForm() {
        document.querySelectorAll('.settings-create-user-form').forEach((form) => {
            if (form.__orbitaCreateUserBound) return;

            const roleInput = form.querySelector('[data-settings-create-user-role]');
            const officeInput = form.querySelector('[data-settings-create-user-office]');
            const officeField = form.querySelector('[data-settings-create-user-office-field]');
            if (!roleInput || !officeInput || !officeField) return;

            form.__orbitaCreateUserBound = true;

            const updateOfficeState = () => {
                const isAdmin = roleInput.value === 'Admin';
                officeField.hidden = isAdmin;
                officeInput.disabled = isAdmin;
                officeInput.required = !isAdmin;

                if (isAdmin) {
                    officeInput.value = '';
                } else if (!officeInput.value && officeInput.options.length > 0) {
                    officeInput.selectedIndex = 0;
                }
            };

            roleInput.addEventListener('change', updateOfficeState);
            updateOfficeState();
        });
    }

    function runSettingsInits() {
        initUsersListFilters();
        initCreateUserForm();
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
                expandButton.setAttribute('data-service-log-expand', '');
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
            const userInitials = document.getElementById('editUserInitials');
            const fullNameInput = document.getElementById('editUserFullName');
            const originalFullNameInput = document.getElementById('editUserOriginalFullName');
            const roleInput = document.getElementById('editUserRole');
            const originalRoleInput = document.getElementById('editUserOriginalRole');
            const officeInput = document.getElementById('editUserOffice');
            const originalOfficeInput = document.getElementById('editUserOriginalOfficeId');
            const originalUseProfilePermissionsInput = document.getElementById('editUserOriginalUseProfilePermissions');
            const originalPermissionKeysInput = document.getElementById('editUserOriginalPermissionKeys');
            const passwordInput = document.getElementById('editUserPassword');
            const accessFields = document.getElementById('editUserAccessFields');
            const officeField = document.getElementById('editUserOfficeField');
            const passwordField = document.getElementById('editUserPasswordField');
            const useProfilePermissionsInput = document.getElementById('editUserUseProfilePermissions');
            const permissionInputs = document.querySelectorAll('[data-user-permission-checkbox]');

            const updateOfficeState = () => {
                if (!form || !roleInput || !officeInput || !officeField) return;
                const isCurrentUser = form.dataset.currentUser === 'true';
                const isAdmin = roleInput.value === 'Admin';
                officeField.hidden = isCurrentUser || isAdmin;
                officeInput.disabled = isCurrentUser || isAdmin;

                if (useProfilePermissionsInput) {
                    useProfilePermissionsInput.disabled = isCurrentUser;
                    permissionInputs.forEach((input) => {
                        input.disabled = isCurrentUser || useProfilePermissionsInput.checked;
                    });
                }

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
                        || !passwordInput || !accessFields || !passwordField || !useProfilePermissionsInput) return;

                    const isCurrentUser = button.getAttribute('data-user-is-current') === 'true';
                    const fullName = button.getAttribute('data-user-full-name') || '';
                    const email = button.getAttribute('data-user-email') || '';
                    const role = button.getAttribute('data-user-role') || '';
                    const officeId = button.getAttribute('data-user-office-id') || '';
                    const permissions = new Set((button.getAttribute('data-user-permissions') || '')
                        .split(',')
                        .filter(Boolean));
                    const hasPermissionOverride = button.getAttribute('data-user-has-permission-override') === 'true';

                    form.dataset.currentUser = String(isCurrentUser);
                    userIdInput.value = button.getAttribute('data-user-id') || '';
                    userEmailLabel.textContent = email;
                    if (userInitials) {
                        const initials = fullName.trim().split(/\s+/).filter(Boolean).slice(0, 2)
                            .map((part) => part.charAt(0)).join('').toLocaleUpperCase();
                        userInitials.textContent = initials || email.charAt(0).toLocaleUpperCase() || 'П';
                    }
                    fullNameInput.value = fullName;
                    originalFullNameInput.value = fullName;
                    roleInput.value = role;
                    originalRoleInput.value = role;
                    officeInput.value = officeId;
                    originalOfficeInput.value = officeId;
                    originalUseProfilePermissionsInput.value = String(!hasPermissionOverride);
                    originalPermissionKeysInput.value = Array.from(permissions).join(',');
                    passwordInput.value = '';
                    useProfilePermissionsInput.checked = !hasPermissionOverride;
                    permissionInputs.forEach((input) => {
                        input.checked = permissions.has(input.value);
                    });
                    accessFields.hidden = isCurrentUser;
                    passwordField.hidden = isCurrentUser;
                    updateOfficeState();

                    if (typeof editUserDialog.showModal === 'function') {
                        editUserDialog.showModal();
                        window.requestAnimationFrame(() => fullNameInput.focus());
                    }
                });
            });

            if (!editUserDialog.__orbitaDialogBound) {
                editUserDialog.__orbitaDialogBound = true;
                roleInput?.addEventListener('change', updateOfficeState);
                useProfilePermissionsInput?.addEventListener('change', updateOfficeState);

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

    initServiceLogDetails();
    runSettingsInits();
    document.addEventListener('orbita:content-updated', runSettingsInits);
})();
