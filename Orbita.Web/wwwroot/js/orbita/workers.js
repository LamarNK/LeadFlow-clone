(function (runtime) {
    runtime.initSubProfilesRefreshButtons = function initSubProfilesRefreshButtons() {
        document.querySelectorAll('[data-refresh-subprofiles]').forEach(function (btn) {
            if (btn.hasAttribute('data-refresh-bound')) return;
            btn.setAttribute('data-refresh-bound', '1');

            btn.addEventListener('click', async function (e) {
                e.preventDefault();
                e.stopPropagation();
                if (btn.disabled || btn.classList.contains('is-loading')) return;

                var workerId = btn.getAttribute('data-worker-id');
                var accountId = btn.getAttribute('data-account-id');
                if (!workerId || !accountId) return;

                var path = window.location.pathname.toLowerCase().indexOf('/accounts') >= 0
                    ? '/Accounts/RefreshSubProfiles'
                    : '/Workers/RefreshSubProfiles';

                btn.disabled = true;
                btn.classList.add('is-loading');

                var result = await runtime.postForm(path, { workerId: workerId, accountId: accountId });
                btn.classList.remove('is-loading');
                btn.disabled = false;

                if (result.ok) {
                    btn.classList.add('is-pending');
                    btn.title = 'Обновление запрошено — ждём воркер';
                    runtime.showToast((result.payload && result.payload.message) || 'Запрос отправлен', { variant: 'success' });
                } else {
                    runtime.showToast((result.payload && result.payload.error) || 'Не удалось отправить запрос', { variant: 'error' });
                }
            });
        });
    }

    runtime.ensureAvitoCredentialsModal = function ensureAvitoCredentialsModal() {
        var existing = document.getElementById('orbita-avito-credentials-modal');
        if (existing) return existing;

        var wrap = document.createElement('div');
        wrap.id = 'orbita-avito-credentials-modal';
        wrap.className = 'orbita-avito-cred-modal';
        wrap.hidden = true;
        wrap.innerHTML =
            '<div class="orbita-avito-cred-modal__backdrop" data-avito-cred-close></div>' +
            '<div class="orbita-avito-cred-modal__dialog" role="dialog" aria-modal="true" aria-labelledby="orbita-avito-cred-title">' +
            '  <h3 id="orbita-avito-cred-title" class="orbita-avito-cred-modal__title">Логин и пароль Avito</h3>' +
            '  <p class="orbita-avito-cred-modal__hint" data-avito-cred-account></p>' +
            '  <label class="orbita-avito-cred-modal__label">Логин / телефон' +
            '    <input type="text" class="orbita-avito-cred-modal__input" data-avito-cred-login autocomplete="username" />' +
            '  </label>' +
            '  <label class="orbita-avito-cred-modal__label">Пароль' +
            '    <input type="password" class="orbita-avito-cred-modal__input" data-avito-cred-password autocomplete="new-password" placeholder="Оставьте пустым, чтобы не менять" />' +
            '  </label>' +
            '  <p class="orbita-avito-cred-modal__status" data-avito-cred-status></p>' +
            '  <div class="orbita-avito-cred-modal__actions">' +
            '    <button type="button" class="orbita-avito-cred-modal__btn orbita-avito-cred-modal__btn--ghost" data-avito-cred-clear>Удалить</button>' +
            '    <button type="button" class="orbita-avito-cred-modal__btn" data-avito-cred-close>Отмена</button>' +
            '    <button type="button" class="orbita-avito-cred-modal__btn orbita-avito-cred-modal__btn--primary" data-avito-cred-save>Сохранить</button>' +
            '  </div>' +
            '</div>';
        document.body.appendChild(wrap);
        return wrap;
    }

    runtime.openAvitoCredentialsModal = function openAvitoCredentialsModal(opts) {
        var modal = runtime.ensureAvitoCredentialsModal();
        var loginInput = modal.querySelector('[data-avito-cred-login]');
        var passwordInput = modal.querySelector('[data-avito-cred-password]');
        var statusEl = modal.querySelector('[data-avito-cred-status]');
        var accountEl = modal.querySelector('[data-avito-cred-account]');
        var saveBtn = modal.querySelector('[data-avito-cred-save]');
        var clearBtn = modal.querySelector('[data-avito-cred-clear]');

        accountEl.textContent = opts.accountName
            ? ('Аккаунт: ' + opts.accountName)
            : '';
        loginInput.value = opts.login || '';
        passwordInput.value = '';
        passwordInput.placeholder = opts.hasPassword
            ? 'Пароль сохранён — оставьте пустым, чтобы не менять'
            : 'Введите пароль Avito';
        statusEl.textContent = opts.hasPassword
            ? 'Пароль уже сохранён в Орбите (шифруется).'
            : 'Пароль ещё не задан — воркер не сможет войти автоматически.';
        modal.hidden = false;

        function close() {
            modal.hidden = true;
            saveBtn.onclick = null;
            clearBtn.onclick = null;
            modal.querySelectorAll('[data-avito-cred-close]').forEach(function (el) {
                el.onclick = null;
            });
        }

        modal.querySelectorAll('[data-avito-cred-close]').forEach(function (el) {
            el.onclick = close;
        });

        saveBtn.onclick = async function () {
            var login = (loginInput.value || '').trim();
            var password = passwordInput.value || '';
            if (!login) {
                runtime.showToast('Укажите логин Avito', { variant: 'error' });
                return;
            }
            if (!password && !opts.hasPassword) {
                runtime.showToast('Укажите пароль Avito', { variant: 'error' });
                return;
            }

            saveBtn.disabled = true;
            clearBtn.disabled = true;
            var result = await runtime.postForm(opts.postUrl, {
                workerId: opts.workerId,
                accountId: opts.accountId,
                login: login,
                password: password,
                clear: 'false'
            });
            saveBtn.disabled = false;
            clearBtn.disabled = false;

            if (result.ok) {
                runtime.showToast((result.payload && result.payload.message) || 'Сохранено', { variant: 'success' });
                close();
                if (window.OrbitaLive && window.OrbitaLive.scheduleRefresh) {
                    window.OrbitaLive.scheduleRefresh({ kinds: ['Accounts', 'Workers'] });
                }
            } else {
                runtime.showToast((result.payload && result.payload.error) || 'Не удалось сохранить', { variant: 'error' });
            }
        };

        clearBtn.onclick = async function () {
            var confirmed = true;
            if (window.Orbita && window.Orbita.confirm) {
                confirmed = await window.Orbita.confirm({
                    title: 'Удалить логин и пароль?',
                    message: 'Воркер больше не сможет входить в этот аккаунт по credentials из Орбиты.',
                    confirmLabel: 'Удалить',
                    variant: 'danger'
                });
            }
            if (!confirmed) return;

            clearBtn.disabled = true;
            saveBtn.disabled = true;
            var result = await runtime.postForm(opts.postUrl, {
                workerId: opts.workerId,
                accountId: opts.accountId,
                login: '',
                password: '',
                clear: 'true'
            });
            clearBtn.disabled = false;
            saveBtn.disabled = false;

            if (result.ok) {
                runtime.showToast((result.payload && result.payload.message) || 'Удалено', { variant: 'success' });
                close();
                if (window.OrbitaLive && window.OrbitaLive.scheduleRefresh) {
                    window.OrbitaLive.scheduleRefresh({ kinds: ['Accounts', 'Workers'] });
                }
            } else {
                runtime.showToast((result.payload && result.payload.error) || 'Не удалось удалить', { variant: 'error' });
            }
        };
    }

    runtime.initAvitoCredentialsButtons = function initAvitoCredentialsButtons() {
        document.querySelectorAll('[data-avito-credentials]').forEach(function (btn) {
            if (btn.hasAttribute('data-avito-credentials-bound')) return;
            btn.setAttribute('data-avito-credentials-bound', '1');

            btn.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                runtime.closeAllRowMenus();
                runtime.openAvitoCredentialsModal({
                    workerId: btn.getAttribute('data-worker-id'),
                    accountId: btn.getAttribute('data-account-id'),
                    accountName: btn.getAttribute('data-account-name') || '',
                    login: btn.getAttribute('data-login') || '',
                    hasPassword: btn.getAttribute('data-has-password') === 'true',
                    postUrl: btn.getAttribute('data-post-url') || '/Workers/UpdateAccountCredentials'
                });
            });
        });
    }

    runtime.initWorkerAccountEnableToggles = function initWorkerAccountEnableToggles() {
        document.querySelectorAll('[data-account-enable-toggle]').forEach(function (input) {
            if (input.hasAttribute('data-account-enable-bound')) return;
            input.setAttribute('data-account-enable-bound', '1');

            input.addEventListener('change', async function (e) {
                e.stopPropagation();
                if (input.disabled) return;

                var workerId = input.getAttribute('data-worker-id');
                var accountId = input.getAttribute('data-account-id');
                if (!workerId || !accountId) return;

                var enabled = input.checked;
                var toggleUrl = input.getAttribute('data-toggle-url') || '/Workers/Toggle';
                var refreshKind = input.getAttribute('data-toggle-refresh');
                input.disabled = true;
                var result = await runtime.postForm(toggleUrl, {
                    workerId: workerId,
                    accountId: accountId,
                    enabled: enabled ? 'true' : 'false'
                });
                input.disabled = false;

                if (result.ok) {
                    var label = input.closest('label');
                    if (label) {
                        label.title = enabled ? 'Отключить аккаунт в панели' : 'Включить аккаунт в панели';
                    }
                    runtime.showToast((result.payload && result.payload.message) || 'Сохранено', { variant: 'success' });
                    if (refreshKind && window.OrbitaLive && window.OrbitaLive.scheduleRefresh) {
                        window.OrbitaLive.scheduleRefresh({ kinds: [refreshKind] });
                    }
                } else {
                    input.checked = !enabled;
                    runtime.showToast((result.payload && result.payload.error) || 'Не удалось сохранить', { variant: 'error' });
                }
            });
        });
    }

    runtime.initSubProfileEnableToggles = function initSubProfileEnableToggles() {
        document.querySelectorAll('[data-subprofile-toggle]').forEach(function (input) {
            if (input.hasAttribute('data-subprofile-enable-bound')) return;
            input.setAttribute('data-subprofile-enable-bound', '1');

            input.addEventListener('change', async function (e) {
                e.stopPropagation();
                if (input.disabled) return;

                var workerId = input.getAttribute('data-worker-id');
                var accountId = input.getAttribute('data-account-id');
                var subProfileId = input.getAttribute('data-subprofile-id');
                if (!workerId || !accountId || !subProfileId) return;

                var enabled = input.checked;
                var path = window.location.pathname.toLowerCase().indexOf('/accounts') >= 0
                    ? '/Accounts/UpdateSubProfile'
                    : '/Workers/UpdateSubProfile';

                input.disabled = true;
                var result = await runtime.postForm(path, {
                    workerId: workerId,
                    accountId: accountId,
                    subProfileId: subProfileId,
                    isEnabledInPanel: enabled ? 'true' : 'false'
                });
                input.disabled = false;

                var row = input.closest('.subprofiles-item');
                if (result.ok) {
                    if (row) {
                        row.classList.toggle('subprofiles-item--disabled', !enabled);
                    }
                    runtime.showToast((result.payload && result.payload.message) || 'Сохранено', { variant: 'success' });
                } else {
                    input.checked = !enabled;
                    runtime.showToast((result.payload && result.payload.error) || 'Не удалось сохранить', { variant: 'error' });
                }
            });
        });
    }

    runtime.initSubProfileScreenshotLinks = function initSubProfileScreenshotLinks() {
        document.querySelectorAll('[data-subprofile-screenshot]').forEach(function (btn) {
            if (btn.hasAttribute('data-subprofile-screenshot-bound')) return;
            btn.setAttribute('data-subprofile-screenshot-bound', '1');

            btn.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                var url = btn.getAttribute('data-screenshot-url');
                if (!url) return;

                var item = btn.closest('.subprofiles-item');
                var issueEl = btn.closest('.subprofiles-item-alert')?.querySelector('.subprofiles-issue');
                var name = item?.querySelector('.subprofiles-name')?.textContent?.trim() || '';
                var issue = issueEl?.textContent?.trim() || '';

                runtime.openDetailModal({
                    title: name ? 'Скриншот · ' + name : 'Скриншот ошибки',
                    body: issue,
                    attachmentUrl: url
                });
            });
        });
    }

    runtime.initSubProfilesToggles = function initSubProfilesToggles() {
        document.querySelectorAll('[data-subprofiles-toggle]').forEach(function (btn) {
            if (btn.hasAttribute('data-subprofiles-bound')) return;
            btn.setAttribute('data-subprofiles-bound', '1');
            btn.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                var shared = window.OrbitaLiveShared;
                if (shared && typeof shared.toggleSubprofiles === 'function') {
                    shared.toggleSubprofiles(btn);
                    return;
                }
                var panelId = btn.getAttribute('aria-controls');
                if (!panelId) return;
                var expanded = btn.getAttribute('aria-expanded') === 'true';
                var willExpand = !expanded;
                btn.setAttribute('aria-expanded', willExpand ? 'true' : 'false');
                if (shared && typeof shared.setSubprofilePanelExpanded === 'function') {
                    shared.setSubprofilePanelExpanded(panelId, willExpand);
                }
                var icon = btn.querySelector('.subprofiles-toggle-icon');
                if (icon) icon.classList.toggle('subprofiles-toggle-icon--open', willExpand);
            });
        });
    }

    var bitrixValidationStatusLabels = {
        ok: 'Готово',
        warning: 'Внимание',
        error: 'Ошибка',
        skipped: 'Пропущено'
    };

    runtime.escapeBitrixHtml = function escapeBitrixHtml(value) {
        return String(value == null ? '' : value)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    runtime.findBitrixValidationResults = function findBitrixValidationResults(form) {
        if (!form) return null;
        return form.querySelector('[data-bitrix-validation-results]')
            || (form.nextElementSibling && form.nextElementSibling.matches
                && form.nextElementSibling.matches('[data-bitrix-validation-results]') ? form.nextElementSibling : null)
            || (form.parentElement ? form.parentElement.querySelector('[data-bitrix-validation-results]') : null);
    }

    runtime.renderBitrixValidation = function renderBitrixValidation(container, validation) {
        if (!validation) {
            container.innerHTML =
                '<div class="settings-bitrix-validation-summary settings-bitrix-validation-summary--error">' +
                'Не удалось прочитать результат проверки. Обновите страницу и попробуйте снова.' +
                '</div>';
            return;
        }

        var steps = Array.isArray(validation.steps) ? validation.steps : [];
        var summaryTone = validation && validation.status === 'ok'
            ? 'success'
            : validation && validation.status === 'warning'
                ? 'warning'
                : 'error';
        var stepsHtml = steps.map(function (step) {
            var tone = step.status === 'ok' ? 'success' : step.status === 'warning' ? 'warning' : 'error';
            var label = bitrixValidationStatusLabels[step.status] || step.status;
            var title = step.title || step.name || step.id || 'Проверка';
            var hintHtml = step.hint
                ? '<p class="settings-bitrix-step-hint"><strong>Что сделать:</strong> ' + runtime.escapeBitrixHtml(step.hint) + '</p>'
                : '';
            return '<li class="settings-bitrix-step settings-bitrix-step--' + tone + '">' +
                '<div class="settings-bitrix-step-head">' +
                '<span class="settings-bitrix-step-name">' + runtime.escapeBitrixHtml(title) + '</span>' +
                '<span class="settings-bitrix-step-status">' + runtime.escapeBitrixHtml(label) + '</span>' +
                '</div>' +
                '<p class="settings-bitrix-step-message">' + runtime.escapeBitrixHtml(step.message) + '</p>' +
                hintHtml +
                '</li>';
        }).join('');

        container.innerHTML =
            '<div class="settings-bitrix-validation-summary settings-bitrix-validation-summary--' + summaryTone + '">' +
            runtime.escapeBitrixHtml((validation && validation.message) || '') +
            '</div>' +
            '<p class="settings-bitrix-validation-caption">Подробности по шагам:</p>' +
            '<ul class="settings-bitrix-step-list">' + stepsHtml + '</ul>';
    }

    async function validateBitrixWebhookFromButton(btn) {
        var post = window.Orbita && window.Orbita.postForm;
        if (!post) {
            runtime.showToast('Проверка Bitrix24 недоступна. Обновите страницу.', { variant: 'error' });
            return;
        }

        var form = btn.closest('[data-bitrix-settings-form]');
        if (!form) {
            runtime.showToast('Форма Bitrix24 не найдена. Обновите страницу.', { variant: 'error' });
            return;
        }

        var validateUrl = form.getAttribute('data-bitrix-validate-url');
        var input = form.querySelector('[data-bitrix-webhook-input]');
        var resultsEl = runtime.findBitrixValidationResults(form);
        if (!validateUrl || !input || !resultsEl) {
            runtime.showToast('Не удалось инициализировать проверку. Обновите страницу.', { variant: 'error' });
            return;
        }

        var webhookUrl = (input.value || '').trim();
        var checkingSaved = !webhookUrl;

        btn.disabled = true;
        resultsEl.hidden = false;
        resultsEl.innerHTML = '<p class="settings-bitrix-validation-loading">' +
            (checkingSaved
                ? 'Проверяем сохранённый вебхук офиса: связь с Bitrix24 и права CRM…'
                : 'Проверяем ссылку: формат, связь с Bitrix24 и права CRM…') +
            '</p>';

        try {
            var fields = { webhookUrl: webhookUrl };
            var idInput = form.querySelector('input[name="Id"]');
            if (idInput && idInput.value) {
                fields.id = idInput.value;
            }
            var officeIdInput = form.querySelector('input[name="officeId"]');
            if (officeIdInput && officeIdInput.value) {
                fields.officeId = officeIdInput.value;
            }
            var result = await post(validateUrl, fields);
            if (!result.ok) {
                var message = (result.payload && result.payload.error)
                    || (result.status === 403
                        ? 'Недостаточно прав для проверки вебхука.'
                        : 'Не удалось выполнить проверку. Обновите страницу и попробуйте снова.');
                resultsEl.innerHTML = '<div class="settings-bitrix-validation-summary settings-bitrix-validation-summary--error">' +
                    runtime.escapeBitrixHtml(message) + '</div>';
                return;
            }

            runtime.renderBitrixValidation(resultsEl, result.payload);
        } catch (err) {
            resultsEl.innerHTML =
                '<div class="settings-bitrix-validation-summary settings-bitrix-validation-summary--error">' +
                'Не удалось связаться с панелью. Проверьте интернет и попробуйте ещё раз.' +
                '</div>';
        } finally {
            btn.disabled = false;
        }
    }

    runtime.initBitrixValidateButtons = function initBitrixValidateButtons() {
        if (document.documentElement.dataset.orbitaBitrixValidateBound === '1') {
            return;
        }
        document.documentElement.dataset.orbitaBitrixValidateBound = '1';
        document.addEventListener('click', function (e) {
            var btn = e.target.closest('[data-bitrix-validate-btn]');
            if (!btn) return;
            e.preventDefault();
            e.stopPropagation();
            validateBitrixWebhookFromButton(btn);
        }, true);
    }

    runtime.initUpdatedClock();
    runtime.initRowMenus();
    runtime.initSubProfilesToggles();
    runtime.initSubProfileEnableToggles();
    runtime.initWorkerAccountEnableToggles();
    runtime.initAvitoCredentialsButtons();
    runtime.initSubProfilesRefreshButtons();
    runtime.initSubProfileScreenshotLinks();
    runtime.initUserMenu();
    runtime.initPeriodPicker();
    runtime.initSidebarToggle();
    runtime.initMobileSidebar();
    runtime.initConfirmDialog();

    runtime.initOfficeSwitcher = function initOfficeSwitcher() {
        document.querySelectorAll('[data-orbita-office-select]').forEach(function (select) {
            if (select.hasAttribute('data-orbita-office-bound')) return;
            select.setAttribute('data-orbita-office-bound', '1');
            select.addEventListener('change', function () {
                var form = select.closest('form');
                if (form) form.submit();
            });
        });
    }

    runtime.initWorkerRestartButtons = function initWorkerRestartButtons() {
        document.querySelectorAll('[data-worker-restart]').forEach(function (btn) {
            if (btn.hasAttribute('data-worker-restart-bound')) return;
            btn.setAttribute('data-worker-restart-bound', '1');

            btn.addEventListener('click', async function (e) {
                e.stopPropagation();
                var workerId = btn.getAttribute('data-worker-id');
                if (!workerId) return;

                if (window.Orbita && window.Orbita.confirm) {
                    var confirmed = await window.Orbita.confirm({
                        title: 'Перезапустить воркер?',
                        message: 'Воркер получит команду и перезапустит процесс на VDS.',
                        confirmLabel: 'Перезапустить',
                        variant: 'danger'
                    });
                    if (!confirmed) return;
                }

                var result = await runtime.postForm('/Workers/Restart', { workerId: workerId });
                if (result.ok) {
                    runtime.showToast((result.payload && result.payload.message) || 'Команда отправлена', { variant: 'success' });
                } else {
                    runtime.showToast((result.payload && result.payload.error) || 'Не удалось отправить команду', { variant: 'error' });
                }
            });
        });
    }

    runtime.initOfficeSwitcher();
    runtime.initWorkerRestartButtons();

    runtime.updateNavBadges = function updateNavBadges(payload) {
        if (!payload) return;
        var errorsEl = document.querySelector('[data-nav-badge="errors"]');
        var responsesEl = document.querySelector('[data-nav-badge="responses"]');
        runtime.setBadge(errorsEl, payload.errorsToday);
        runtime.setBadge(responsesEl, payload.sentToCrm, ' в Битрикс24 за сегодня');
    }

    runtime.setBadge = function setBadge(el, value, suffix) {
        if (!el) return;
        var count = parseInt(value, 10) || 0;
        if (count <= 0) {
            el.setAttribute('hidden', '');
            el.textContent = '';
            return;
        }
        el.removeAttribute('hidden');
        el.textContent = count > 99 ? '99+' : String(count);
        el.setAttribute('aria-label', count + (suffix || ' новых'));
    }

    runtime.fetchNavBadges = function fetchNavBadges() {
        fetch('/Nav/Badges', { credentials: 'same-origin' })
            .then(function (res) { return res.ok ? res.json() : null; })
            .then(runtime.updateNavBadges)
            .catch(function () { });
    }

})(window.OrbitaRuntime = window.OrbitaRuntime || {});
