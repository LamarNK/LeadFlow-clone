(function () {
    function initCrmCardPage() {
        var page = document.querySelector('.crm-card-page');
        if (!page || page.dataset.crmCardBound === '1') return;
        page.dataset.crmCardBound = '1';

        var input = page.querySelector('#stageComment');
        if (input) {
            page.querySelectorAll('[data-stage-comment]').forEach(function (hidden) {
                var form = hidden.closest('form');
                if (!form) return;
                form.addEventListener('submit', function () {
                    hidden.value = input.value || '';
                });
            });
        }

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
