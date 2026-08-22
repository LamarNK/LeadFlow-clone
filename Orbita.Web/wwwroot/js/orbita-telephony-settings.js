(() => {
    const elementFromEvent = event => event.target instanceof Element ? event.target : null;

    const closeModal = modal => {
        if (!modal) return;
        modal.hidden = true;
        if (!document.querySelector('.telephony-modal:not([hidden])')) {
            document.body.classList.remove('telephony-modal-open');
        }
    };

    const openUserModal = button => {
        const modal = document.querySelector('[data-telephony-user-modal]');
        const userSelect = modal?.querySelector('[data-telephony-phone-user-select]');
        const extension = modal?.querySelector('[data-telephony-phone-extension]');
        const outboundProvider = modal?.querySelector('[data-telephony-outbound-provider]');
        if (!modal || !userSelect || !extension || !outboundProvider) return;

        userSelect.value = button.dataset.userId || '';
        extension.value = button.dataset.providerKey || '';
        outboundProvider.value = button.dataset.outboundProvider || 'default';
        modal.hidden = false;
        document.body.classList.add('telephony-modal-open');
        window.setTimeout(() => (button.dataset.userId ? extension : userSelect).focus(), 0);
    };

    const openProviderBindingModal = button => {
        const modal = document.querySelector('[data-telephony-binding-modal]');
        const userSelect = modal?.querySelector('[data-telephony-user-select]');
        const providerKey = modal?.querySelector('[data-telephony-provider-key]');
        if (!modal || !userSelect || !providerKey) return;

        userSelect.value = button.dataset.userId || '';
        providerKey.value = button.dataset.providerKey || '';
        modal.hidden = false;
        document.body.classList.add('telephony-modal-open');
        window.setTimeout(() => (button.dataset.userId ? providerKey : userSelect).focus(), 0);
    };

    document.addEventListener('click', async event => {
        const target = elementFromEvent(event);
        if (!target) return;

        const copyButton = target.closest('[data-copy-telephony-value]');
        if (copyButton) {
            const value = document.getElementById(copyButton.dataset.copyTelephonyValue)?.textContent?.trim();
            if (!value) return;
            await navigator.clipboard.writeText(value);
            const previous = copyButton.textContent;
            copyButton.textContent = 'Скопировано';
            window.setTimeout(() => copyButton.textContent = previous, 1500);
            return;
        }

        const userButton = target.closest('[data-telephony-open-user]');
        if (userButton) {
            openUserModal(userButton);
            return;
        }

        const bindingButton = target.closest('[data-telephony-open-binding]');
        if (bindingButton) {
            openProviderBindingModal(bindingButton);
            return;
        }

        const closeUserButton = target.closest('[data-telephony-close-user]');
        if (closeUserButton) {
            closeModal(closeUserButton.closest('[data-telephony-user-modal]'));
            return;
        }

        const closeBindingButton = target.closest('[data-telephony-close-binding]');
        if (closeBindingButton) {
            closeModal(closeBindingButton.closest('[data-telephony-binding-modal]'));
        }
    });

    document.addEventListener('keydown', event => {
        if (event.key !== 'Escape') return;
        closeModal(document.querySelector('.telephony-modal:not([hidden])'));
    });
})();
