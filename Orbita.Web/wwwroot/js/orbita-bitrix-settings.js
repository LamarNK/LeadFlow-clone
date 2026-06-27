(function () {
    const statusLabels = {
        ok: 'Готово',
        warning: 'Внимание',
        error: 'Ошибка',
        skipped: 'Пропущено'
    };

    document.querySelectorAll('[data-bitrix-settings-form]').forEach((form) => {
        const validateUrl = form.getAttribute('data-bitrix-validate-url');
        const input = form.querySelector('[data-bitrix-webhook-input]');
        const validateBtn = form.querySelector('[data-bitrix-validate-btn]');
        const resultsEl = form.parentElement?.querySelector('[data-bitrix-validation-results]');
        const tokenInput = form.querySelector('input[name="__RequestVerificationToken"]');

        if (!validateUrl || !input || !validateBtn || !resultsEl || !tokenInput) {
            return;
        }

        validateBtn.addEventListener('click', async () => {
            validateBtn.disabled = true;
            resultsEl.hidden = false;
            resultsEl.innerHTML = '<p class="settings-bitrix-validation-loading">Проверяем ссылку: формат, связь с Bitrix24 и права CRM…</p>';

            try {
                const formData = new FormData();
                formData.append('webhookUrl', input.value.trim());
                formData.append('__RequestVerificationToken', tokenInput.value);

                const response = await fetch(validateUrl, {
                    method: 'POST',
                    body: formData
                });

                if (!response.ok) {
                    let message = 'Не удалось выполнить проверку. Обновите страницу и попробуйте снова.';
                    try {
                        const payload = await response.json();
                        if (payload?.error) {
                            message = payload.error;
                        }
                    } catch {
                        // ignore
                    }
                    resultsEl.innerHTML = `<div class="settings-bitrix-validation-summary settings-bitrix-validation-summary--error">${escapeHtml(message)}</div>`;
                    return;
                }

                const validation = await response.json();
                renderValidation(resultsEl, validation);
            } catch {
                resultsEl.innerHTML = `
                    <div class="settings-bitrix-validation-summary settings-bitrix-validation-summary--error">
                        Не удалось связаться с панелью. Проверьте интернет и попробуйте ещё раз.
                    </div>`;
            } finally {
                validateBtn.disabled = false;
            }
        });
    });

    function renderValidation(container, validation) {
        const steps = Array.isArray(validation?.steps) ? validation.steps : [];
        const summaryTone = validation?.status === 'ok'
            ? 'success'
            : validation?.status === 'warning'
                ? 'warning'
                : 'error';

        const stepsHtml = steps.map((step) => {
            const tone = step.status === 'ok' ? 'success' : step.status === 'warning' ? 'warning' : 'error';
            const label = statusLabels[step.status] || step.status;
            const title = step.title || step.name || step.id || 'Проверка';
            const hintHtml = step.hint
                ? `<p class="settings-bitrix-step-hint"><strong>Что сделать:</strong> ${escapeHtml(step.hint)}</p>`
                : '';
            return `<li class="settings-bitrix-step settings-bitrix-step--${tone}">
                <div class="settings-bitrix-step-head">
                    <span class="settings-bitrix-step-name">${escapeHtml(title)}</span>
                    <span class="settings-bitrix-step-status">${escapeHtml(label)}</span>
                </div>
                <p class="settings-bitrix-step-message">${escapeHtml(step.message)}</p>
                ${hintHtml}
            </li>`;
        }).join('');

        container.innerHTML = `
            <div class="settings-bitrix-validation-summary settings-bitrix-validation-summary--${summaryTone}">
                ${escapeHtml(validation?.message || '')}
            </div>
            <p class="settings-bitrix-validation-caption">Подробности по шагам:</p>
            <ul class="settings-bitrix-step-list">${stepsHtml}</ul>`;
    }

    function escapeHtml(value) {
        return String(value ?? '')
            .replaceAll('&', '&amp;')
            .replaceAll('<', '&lt;')
            .replaceAll('>', '&gt;')
            .replaceAll('"', '&quot;');
    }
})();