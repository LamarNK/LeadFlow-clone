(() => {
    const pageSelector = '[data-crm-staff-manager-page]';

    const initStaffManagerPage = () => {
        const page = document.querySelector(pageSelector);
        if (!page || page.dataset.crmStaffReady === 'true') return;
        page.dataset.crmStaffReady = 'true';

        const createModal = document.querySelector('[data-crm-staff-create-modal]');
        const editModal = document.querySelector('[data-crm-staff-edit-modal]');
        const search = page.querySelector('[data-crm-staff-search]');
        const filterButtons = Array.from(page.querySelectorAll('[data-crm-staff-filter]'));
        const rows = Array.from(page.querySelectorAll('[data-crm-staff-row]'));
        const groups = Array.from(page.querySelectorAll('[data-crm-staff-role-group]'));
        const resultCount = page.querySelector('[data-crm-staff-result-count]');
        const empty = page.querySelector('[data-crm-staff-empty]');
        let activeRole = 'all';
        let returnFocus = null;

        const closeModal = (modal) => {
            if (!modal || modal.hidden) return;
            modal.hidden = true;
            document.body.classList.remove('orbita-modal-open');
            returnFocus?.focus();
            returnFocus = null;
        };

        const openModal = (modal, trigger, focusSelector) => {
            if (!modal) return;
            returnFocus = trigger || document.activeElement;
            modal.hidden = false;
            document.body.classList.add('orbita-modal-open');
            window.setTimeout(() => modal.querySelector(focusSelector)?.focus(), 0);
        };

        document.querySelectorAll('[data-crm-staff-modal-close]').forEach((button) => {
            button.addEventListener('click', () => closeModal(button.closest('.crm-staff-modal')));
        });

        document.querySelectorAll('[data-crm-staff-add-open]').forEach((button) => {
            button.addEventListener('click', () => openModal(createModal, button, '[name="fullName"]'));
        });

        [createModal, editModal].filter(Boolean).forEach((modal) => {
            modal.addEventListener('keydown', (event) => {
                if (event.key === 'Escape') closeModal(modal);
            });
        });

        const updateResultCount = (count) => {
            if (!resultCount) return;
            const label = count === 1 ? 'сотрудник' : count > 1 && count < 5 ? 'сотрудника' : 'сотрудников';
            resultCount.textContent = `${count} ${label}`;
        };

        const applyFilters = () => {
            const query = (search?.value || '').trim().toLowerCase();
            let visibleCount = 0;

            rows.forEach((row) => {
                const matchesRole = activeRole === 'all' || row.dataset.staffRole === activeRole;
                const matchesQuery = !query || (row.dataset.staffSearch || '').toLowerCase().includes(query);
                const visible = matchesRole && matchesQuery;
                row.hidden = !visible;
                if (visible) visibleCount += 1;
            });

            groups.forEach((group) => {
                group.hidden = !Array.from(group.querySelectorAll('[data-crm-staff-row]')).some((row) => !row.hidden);
            });

            if (empty) empty.hidden = visibleCount !== 0;
            updateResultCount(visibleCount);
        };

        search?.addEventListener('input', applyFilters);
        filterButtons.forEach((button) => {
            button.addEventListener('click', () => {
                activeRole = button.dataset.crmStaffFilter || 'all';
                filterButtons.forEach((item) => item.classList.toggle('is-active', item === button));
                applyFilters();
            });
        });

        const editForm = editModal?.querySelector('[data-crm-staff-edit-form]');
        const editId = editModal?.querySelector('[data-crm-staff-edit-id]');
        const editName = editModal?.querySelector('[data-crm-staff-edit-name]');
        const editRole = editModal?.querySelector('[data-crm-staff-edit-role]');
        const editPassword = editModal?.querySelector('[data-crm-staff-edit-password]');
        const editEmail = editModal?.querySelector('[data-crm-staff-edit-email]');
        const capacityField = editModal?.querySelector('[data-crm-staff-capacity-field]');
        const capacity = editModal?.querySelector('[data-crm-staff-edit-capacity]');
        const accountState = editModal?.querySelector('[data-crm-staff-account-state]');
        const lockForm = editModal?.querySelector('[data-crm-staff-lock-form]');
        const lockId = editModal?.querySelector('[data-crm-staff-lock-id]');
        const lockButton = editModal?.querySelector('[data-crm-staff-lock-button]');
        const deleteId = editModal?.querySelector('[data-crm-staff-delete-id]');

        page.querySelectorAll('[data-crm-staff-edit]').forEach((button) => {
            button.addEventListener('click', () => {
                const data = button.dataset;
                if (!editModal || !editForm || !editId || !editName || !editRole) return;

                editId.value = data.staffId || '';
                editName.value = data.staffName || '';
                editRole.value = data.staffRole || '';
                if (editPassword) editPassword.value = '';
                if (editEmail) editEmail.textContent = data.staffEmail || '';
                if (capacityField && capacity) {
                    const hasCapacity = data.staffHasCapacity === 'true';
                    capacityField.hidden = !hasCapacity;
                    capacity.disabled = !hasCapacity;
                    capacity.value = hasCapacity ? data.staffCapacity || '' : '';
                }

                const isLocked = data.staffLocked === 'true';
                if (accountState) {
                    accountState.textContent = isLocked ? 'Доступ в панель сейчас заблокирован.' : 'Аккаунт активен и может входить в панель.';
                }
                if (lockForm && lockId && lockButton) {
                    lockId.value = data.staffId || '';
                    lockForm.action = isLocked ? '/Crm/UnlockOfficeStaff' : '/Crm/LockOfficeStaff';
                    lockButton.innerHTML = isLocked
                        ? '<i class="fa-solid fa-lock-open" aria-hidden="true"></i>Разблокировать'
                        : '<i class="fa-solid fa-lock" aria-hidden="true"></i>Заблокировать';
                }
                if (deleteId) deleteId.value = data.staffId || '';
                openModal(editModal, button, '[data-crm-staff-edit-name]');
            });
        });

        editModal?.querySelector('[data-crm-staff-delete-form]')?.addEventListener('submit', async (event) => {
            const name = editName?.value || 'сотрудника';
            const message = `Удалить ${name}? Это действие нельзя отменить.`;
            if (window.Orbita?.confirm) {
                event.preventDefault();
                const confirmed = await window.Orbita.confirm({
                    title: 'Удалить сотрудника?',
                    message,
                    confirmLabel: 'Удалить',
                    variant: 'danger'
                });
                if (confirmed) event.target.submit();
                return;
            }
            if (!window.confirm(message)) event.preventDefault();
        });

        applyFilters();
    };

    document.addEventListener('DOMContentLoaded', initStaffManagerPage);
    document.addEventListener('orbita:content-updated', initStaffManagerPage);
    initStaffManagerPage();
})();
