(function () {
    function initCrmCardPage() {
        var page = document.querySelector('.crm-card-page');
        if (!page || page.dataset.crmCardBound === '1') return;
        page.dataset.crmCardBound = '1';

        var cardEditForm = page.querySelector('[data-crm-card-inline-edit]');
        var cardEditToggle = page.querySelector('[data-crm-card-inline-edit-toggle]');
        var cardEditCancel = page.querySelector('[data-crm-card-inline-edit-cancel]');
        if (cardEditForm && cardEditToggle) {
            function setCardEditing(editing) {
                cardEditForm.classList.toggle('is-editing', editing);
                cardEditToggle.setAttribute('aria-expanded', editing ? 'true' : 'false');
                if (editing) {
                    var nameInput = cardEditForm.querySelector('[name="fullName"]');
                    if (nameInput) {
                        nameInput.focus();
                        nameInput.select();
                    }
                } else {
                    cardEditForm.reset();
                }
            }

            cardEditToggle.addEventListener('click', function () { setCardEditing(true); });
            if (cardEditCancel) {
                cardEditCancel.addEventListener('click', function () { setCardEditing(false); });
            }
        }

        page.querySelectorAll('[data-crm-note-edit-form]').forEach(function (form) {
            if (form.dataset.crmNoteEditorBound === '1') return;
            form.dataset.crmNoteEditorBound = '1';

            var note = form.closest('.crm-feed-item');
            var body = note?.querySelector('[data-crm-note-body]');
            var openButton = note?.querySelector('[data-crm-note-edit-open]');
            var cancelButton = form.querySelector('[data-crm-note-edit-cancel]');
            var textarea = form.querySelector('textarea[name="text"]');
            var error = form.querySelector('[data-crm-note-edit-error]');

            function setNoteEditing(editing, reset) {
                form.hidden = !editing;
                if (body) body.hidden = editing;
                if (openButton) {
                    openButton.hidden = editing;
                    openButton.setAttribute('aria-expanded', editing ? 'true' : 'false');
                }
                if (error) {
                    error.hidden = true;
                    error.textContent = '';
                }
                if (reset) form.reset();
                if (editing && textarea) {
                    textarea.focus();
                    textarea.setSelectionRange(textarea.value.length, textarea.value.length);
                }
            }

            openButton?.addEventListener('click', function () {
                page.querySelectorAll('[data-crm-note-edit-form]:not([hidden])').forEach(function (otherForm) {
                    if (otherForm !== form) {
                        otherForm.querySelector('[data-crm-note-edit-cancel]')?.click();
                    }
                });
                setNoteEditing(true, false);
            });
            cancelButton?.addEventListener('click', function () { setNoteEditing(false, true); });

            form.addEventListener('submit', async function (event) {
                event.preventDefault();
                if (!textarea || !textarea.value.trim()) {
                    textarea?.focus();
                    return;
                }

                var submitButton = form.querySelector('button[type="submit"]');
                if (submitButton) submitButton.disabled = true;
                if (error) error.hidden = true;

                try {
                    var response = await fetch(form.action, {
                        method: 'POST',
                        body: new FormData(form),
                        credentials: 'same-origin',
                        headers: {
                            'Accept': 'application/json',
                            'X-Requested-With': 'XMLHttpRequest'
                        }
                    });
                    var payload = await response.json().catch(function () { return {}; });
                    if (!response.ok) {
                        throw new Error(payload.error || 'Не удалось изменить комментарий.');
                    }

                    var updatedText = (payload.text || textarea.value).trim();
                    if (body) body.textContent = updatedText;
                    textarea.value = updatedText;
                    textarea.defaultValue = updatedText;

                    var edited = note?.querySelector('[data-crm-note-edited]');
                    var editedTime = edited?.querySelector('time');
                    var updatedAtUtc = payload.updatedAtUtc || new Date().toISOString();
                    if (edited && editedTime) {
                        edited.classList.remove('is-hidden');
                        editedTime.setAttribute('data-orbita-utc', updatedAtUtc);
                        editedTime.setAttribute('datetime', updatedAtUtc);
                        if (window.OrbitaTime) window.OrbitaTime.localizeElement(editedTime);
                    }

                    setNoteEditing(false, false);
                } catch (requestError) {
                    if (error) {
                        error.textContent = requestError.message || 'Не удалось изменить комментарий.';
                        error.hidden = false;
                    }
                } finally {
                    if (submitButton) submitButton.disabled = false;
                }
            });
        });

        try {
            var stage = page.querySelector('[data-crm-back-to-board]')?.dataset.crmStage?.trim();
            if (stage) sessionStorage.setItem('orbita.crm.board.focusStage', stage);
        } catch (e) { /* ignore */ }

        var thread = page.querySelector('[data-crm-chat-thread][data-mark-read="1"]');
        var cardId = thread?.getAttribute('data-card-id');
        if (cardId && window.Orbita && typeof window.Orbita.postForm === 'function') {
            window.Orbita.postForm('/Crm/MarkChatRead', { id: cardId }).catch(function () { });
        }
    }

    initCrmCardPage();
    document.addEventListener('orbita:content-updated', initCrmCardPage);
})();
