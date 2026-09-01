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

    runtime.ensureLocalProfilePanel = function ensureLocalProfilePanel() {
        var existing = document.getElementById('orbita-local-profile-panel');
        if (existing) return existing;

        var wrap = document.createElement('div');
        wrap.id = 'orbita-local-profile-panel';
        wrap.className = 'orbita-local-profile-panel';
        wrap.hidden = true;
        wrap.innerHTML =
            '<div class="orbita-local-profile-panel__backdrop" data-local-profile-close></div>' +
            '<div class="orbita-local-profile-panel__dialog" role="dialog" aria-modal="true" aria-labelledby="orbita-local-profile-title">' +
            '  <div class="orbita-local-profile-panel__header">' +
            '    <h3 id="orbita-local-profile-title" class="orbita-local-profile-panel__title">Настройки профиля</h3>' +
            '    <button type="button" class="orbita-local-profile-panel__close" data-local-profile-close aria-label="Закрыть"><i class="fa-solid fa-xmark" aria-hidden="true"></i></button>' +
            '  </div>' +
            '  <p class="orbita-local-profile-panel__hint" data-local-profile-account></p>' +
            '  <section class="orbita-local-profile-panel__section">' +
            '    <h4 class="orbita-local-profile-panel__section-title">Avito</h4>' +
            '    <label class="orbita-avito-cred-modal__label">Логин / телефон' +
            '      <input type="text" class="orbita-avito-cred-modal__input" data-local-profile-login autocomplete="username" maxlength="256" />' +
            '    </label>' +
            '    <label class="orbita-avito-cred-modal__label">Пароль' +
            '      <input type="password" class="orbita-avito-cred-modal__input" data-local-profile-password autocomplete="new-password" maxlength="256" />' +
            '    </label>' +
            '    <p class="orbita-local-profile-panel__status" data-local-profile-avito-status></p>' +
            '    <button type="button" class="orbita-avito-cred-modal__btn orbita-avito-cred-modal__btn--ghost" data-local-profile-clear>Очистить данные</button>' +
            '  </section>' +
            '  <section class="orbita-local-profile-panel__section">' +
            '    <h4 class="orbita-local-profile-panel__section-title">Прокси</h4>' +
            '    <label class="orbita-local-profile-panel__toggle">' +
            '      <input type="checkbox" data-local-profile-proxy-enabled /> Использовать прокси' +
            '    </label>' +
            '    <div class="orbita-local-profile-panel__fields" data-local-profile-proxy-fields>' +
            '      <label class="orbita-avito-cred-modal__label">Тип' +
            '        <input type="text" class="orbita-avito-cred-modal__input" value="HTTP/HTTPS" disabled />' +
            '      </label>' +
            '      <label class="orbita-avito-cred-modal__label">Адрес host:port' +
            '        <input type="text" class="orbita-avito-cred-modal__input" data-local-profile-proxy-address maxlength="255" autocomplete="off" spellcheck="false" placeholder="203.0.113.10:8080" />' +
            '      </label>' +
            '      <label class="orbita-avito-cred-modal__label">Логин прокси' +
            '        <input type="text" class="orbita-avito-cred-modal__input" data-local-profile-proxy-username maxlength="255" autocomplete="off" />' +
            '      </label>' +
            '      <label class="orbita-avito-cred-modal__label">Пароль прокси' +
            '        <input type="password" class="orbita-avito-cred-modal__input" data-local-profile-proxy-password maxlength="256" autocomplete="new-password" />' +
            '      </label>' +
            '    </div>' +
            '    <p class="orbita-local-profile-panel__status" data-local-profile-proxy-status></p>' +
            '  </section>' +
            '  <section class="orbita-local-profile-panel__section">' +
            '    <h4 class="orbita-local-profile-panel__section-title">Браузер</h4>' +
            '    <p class="orbita-local-profile-panel__status" data-local-profile-browser-status></p>' +
            '    <button type="button" class="orbita-avito-cred-modal__btn" data-local-profile-open>Открыть браузер</button>' +
            '  </section>' +
            '  <div class="orbita-avito-cred-modal__actions">' +
            '    <button type="button" class="orbita-avito-cred-modal__btn" data-local-profile-close>Отмена</button>' +
            '    <button type="button" class="orbita-avito-cred-modal__btn orbita-avito-cred-modal__btn--primary" data-local-profile-save>Сохранить</button>' +
            '  </div>' +
            '</div>';
        document.body.appendChild(wrap);
        return wrap;
    }

    runtime.openLocalProfilePanel = function openLocalProfilePanel(opts) {
        var panel = runtime.ensureLocalProfilePanel();
        var loginInput = panel.querySelector('[data-local-profile-login]');
        var passwordInput = panel.querySelector('[data-local-profile-password]');
        var avitoStatus = panel.querySelector('[data-local-profile-avito-status]');
        var accountEl = panel.querySelector('[data-local-profile-account]');
        var proxyEnabled = panel.querySelector('[data-local-profile-proxy-enabled]');
        var proxyFields = panel.querySelector('[data-local-profile-proxy-fields]');
        var proxyAddress = panel.querySelector('[data-local-profile-proxy-address]');
        var proxyUsername = panel.querySelector('[data-local-profile-proxy-username]');
        var proxyPassword = panel.querySelector('[data-local-profile-proxy-password]');
        var proxyStatus = panel.querySelector('[data-local-profile-proxy-status]');
        var browserStatus = panel.querySelector('[data-local-profile-browser-status]');
        var openBtn = panel.querySelector('[data-local-profile-open]');
        var saveBtn = panel.querySelector('[data-local-profile-save]');
        var clearBtn = panel.querySelector('[data-local-profile-clear]');
        var state = {
            hasPassword: !!opts.hasPassword,
            hasProxyPassword: !!opts.hasProxyPassword
        };

        accountEl.textContent = opts.accountName ? ('Аккаунт: ' + opts.accountName) : '';
        loginInput.value = opts.login || '';
        passwordInput.value = '';
        passwordInput.placeholder = state.hasPassword
            ? 'Пароль сохранён — оставьте пустым, чтобы не менять'
            : 'Введите пароль Avito';
        avitoStatus.textContent = state.hasPassword
            ? 'Учётные данные заданы'
            : 'Учётные данные не заданы';
        proxyEnabled.checked = !!opts.proxyEnabled;
        proxyFields.hidden = !proxyEnabled.checked;
        proxyAddress.value = opts.proxyAddress || '';
        proxyUsername.value = opts.proxyUsername || '';
        proxyPassword.value = '';
        proxyPassword.placeholder = state.hasProxyPassword
            ? 'Пароль сохранён — оставьте пустым, чтобы не менять'
            : 'Необязательно';
        proxyStatus.textContent = opts.proxyStatus || (opts.proxyEnabled ? 'настроен' : 'не настроен');
        browserStatus.textContent = 'Статус: ' + (opts.browserStatus || 'Свободен');
        openBtn.textContent = opts.openLabel || 'Открыть браузер';
        openBtn.disabled = opts.canOpenBrowser === false;
        panel.hidden = false;

        function close() {
            panel.hidden = true;
            saveBtn.onclick = null;
            clearBtn.onclick = null;
            openBtn.onclick = null;
            proxyEnabled.onchange = null;
            panel.querySelectorAll('[data-local-profile-close]').forEach(function (el) {
                el.onclick = null;
            });
        }

        function applyProfile(profile) {
            if (!profile) return;
            state.hasPassword = !!profile.hasPassword;
            state.hasProxyPassword = !!profile.hasProxyPassword;
            loginInput.value = profile.login || '';
            passwordInput.value = '';
            passwordInput.placeholder = state.hasPassword
                ? 'Пароль сохранён — оставьте пустым, чтобы не менять'
                : 'Введите пароль Avito';
            avitoStatus.textContent = profile.hasCredentials
                ? 'Учётные данные заданы'
                : 'Учётные данные не заданы';
            proxyEnabled.checked = !!profile.proxyEnabled;
            proxyFields.hidden = !proxyEnabled.checked;
            proxyAddress.value = profile.proxyAddress || '';
            proxyUsername.value = profile.proxyUsername || '';
            proxyPassword.value = '';
            proxyPassword.placeholder = state.hasProxyPassword
                ? 'Пароль сохранён — оставьте пустым, чтобы не менять'
                : 'Необязательно';
            proxyStatus.textContent = profile.proxyStatus || '';
            browserStatus.textContent = 'Статус: ' + (profile.browserStatus || 'Свободен');
            openBtn.disabled = profile.canOpenBrowser === false;
            if (opts.source) {
                opts.source.setAttribute('data-login', profile.login || '');
                opts.source.setAttribute('data-has-password', profile.hasPassword ? 'true' : 'false');
                opts.source.setAttribute('data-proxy-enabled', profile.proxyEnabled ? 'true' : 'false');
                opts.source.setAttribute('data-proxy-address', profile.proxyAddress || '');
                opts.source.setAttribute('data-proxy-username', profile.proxyUsername || '');
                opts.source.setAttribute('data-has-proxy-password', profile.hasProxyPassword ? 'true' : 'false');
                opts.source.setAttribute('data-proxy-status', profile.proxyStatus || '');
                opts.source.setAttribute('data-browser-status', profile.browserStatus || '');
                opts.source.setAttribute('data-can-open-browser', profile.canOpenBrowser ? 'true' : 'false');
            }
        }

        proxyEnabled.onchange = function () {
            proxyFields.hidden = !proxyEnabled.checked;
        };

        panel.querySelectorAll('[data-local-profile-close]').forEach(function (el) {
            el.onclick = close;
        });

        saveBtn.onclick = async function () {
            var login = (loginInput.value || '').trim();
            var password = passwordInput.value || '';
            if (login && !password && !state.hasPassword) {
                runtime.showToast('Укажите пароль Avito', { variant: 'error' });
                return;
            }
            if (proxyEnabled.checked) {
                var address = (proxyAddress.value || '').trim();
                if (!address) {
                    runtime.showToast('Укажите адрес прокси в формате host:port', { variant: 'error' });
                    return;
                }
                if (address.indexOf('://') >= 0 || address.indexOf('@') >= 0 || /\s/.test(address)) {
                    runtime.showToast('Адрес прокси: только host:port, без схемы и логина', { variant: 'error' });
                    return;
                }
            }

            saveBtn.disabled = true;
            clearBtn.disabled = true;
            var result = await runtime.postForm(opts.saveUrl || '/Workers/UpdateLocalAccountProfile', {
                workerId: opts.workerId,
                accountId: opts.accountId,
                login: login,
                password: password,
                clearCredentials: 'false',
                proxyEnabled: proxyEnabled.checked ? 'true' : 'false',
                proxyAddress: (proxyAddress.value || '').trim(),
                proxyUsername: (proxyUsername.value || '').trim(),
                proxyPassword: proxyPassword.value || '',
                clearProxyPassword: 'false'
            });
            saveBtn.disabled = false;
            clearBtn.disabled = false;

            if (result.ok) {
                runtime.showToast((result.payload && result.payload.message) || 'Сохранено', { variant: 'success' });
                applyProfile(result.payload && result.payload.profile);
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
                    title: 'Очистить данные Avito?',
                    message: 'Логин и пароль Avito будут удалены. Прокси не изменится.',
                    confirmLabel: 'Очистить',
                    variant: 'danger'
                });
            }
            if (!confirmed) return;

            clearBtn.disabled = true;
            saveBtn.disabled = true;
            var result = await runtime.postForm(opts.saveUrl || '/Workers/UpdateLocalAccountProfile', {
                workerId: opts.workerId,
                accountId: opts.accountId,
                login: '',
                password: '',
                clearCredentials: 'true',
                proxyEnabled: proxyEnabled.checked ? 'true' : 'false',
                proxyAddress: (proxyAddress.value || '').trim(),
                proxyUsername: (proxyUsername.value || '').trim(),
                proxyPassword: '',
                clearProxyPassword: 'false'
            });
            clearBtn.disabled = false;
            saveBtn.disabled = false;

            if (result.ok) {
                runtime.showToast((result.payload && result.payload.message) || 'Очищено', { variant: 'success' });
                applyProfile(result.payload && result.payload.profile);
                if (window.OrbitaLive && window.OrbitaLive.scheduleRefresh) {
                    window.OrbitaLive.scheduleRefresh({ kinds: ['Accounts', 'Workers'] });
                }
            } else {
                runtime.showToast((result.payload && result.payload.error) || 'Не удалось очистить', { variant: 'error' });
            }
        };

        openBtn.onclick = async function () {
            if (openBtn.disabled) return;
            openBtn.disabled = true;
            var result = await runtime.postForm(opts.openUrl || '/Workers/OpenLocalBrowser', {
                workerId: opts.workerId,
                accountId: opts.accountId
            });
            openBtn.disabled = opts.canOpenBrowser === false;
            if (result.ok) {
                runtime.showToast(
                    (result.payload && result.payload.message) || 'На машине воркера открывается Chrome.',
                    { variant: 'success' });
                browserStatus.textContent = 'Статус: Открыт вручную';
                if (window.OrbitaLive && window.OrbitaLive.scheduleRefresh) {
                    window.OrbitaLive.scheduleRefresh({ kinds: ['Accounts', 'Workers'] });
                }
            } else {
                runtime.showToast((result.payload && result.payload.error) || 'Не удалось открыть браузер', { variant: 'error' });
            }
        };
    }

    runtime.initLocalProfileSettingsButtons = function initLocalProfileSettingsButtons() {
        document.querySelectorAll('[data-local-profile-settings]').forEach(function (btn) {
            if (btn.hasAttribute('data-local-profile-settings-bound')) return;
            btn.setAttribute('data-local-profile-settings-bound', '1');

            btn.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                runtime.closeAllRowMenus();
                runtime.openLocalProfilePanel({
                    source: btn,
                    workerId: btn.getAttribute('data-worker-id'),
                    accountId: btn.getAttribute('data-account-id'),
                    accountName: btn.getAttribute('data-account-name') || '',
                    login: btn.getAttribute('data-login') || '',
                    hasPassword: btn.getAttribute('data-has-password') === 'true',
                    proxyEnabled: btn.getAttribute('data-proxy-enabled') === 'true',
                    proxyAddress: btn.getAttribute('data-proxy-address') || '',
                    proxyUsername: btn.getAttribute('data-proxy-username') || '',
                    hasProxyPassword: btn.getAttribute('data-has-proxy-password') === 'true',
                    proxyStatus: btn.getAttribute('data-proxy-status') || '',
                    browserStatus: btn.getAttribute('data-browser-status') || '',
                    canOpenBrowser: btn.getAttribute('data-can-open-browser') !== 'false',
                    openLabel: btn.getAttribute('data-open-label') || 'Открыть браузер',
                    saveUrl: btn.getAttribute('data-save-url') || '/Workers/UpdateLocalAccountProfile',
                    openUrl: btn.getAttribute('data-open-url') || '/Workers/OpenLocalBrowser'
                });
            });
        });
    }

    runtime.ensureLocalAccountModal = function ensureLocalAccountModal() {
        var existing = document.getElementById('orbita-local-account-modal');
        if (existing) return existing;

        var wrap = document.createElement('div');
        wrap.id = 'orbita-local-account-modal';
        wrap.className = 'orbita-avito-cred-modal';
        wrap.hidden = true;
        wrap.innerHTML =
            '<div class="orbita-avito-cred-modal__backdrop" data-local-account-close></div>' +
            '<div class="orbita-avito-cred-modal__dialog" role="dialog" aria-modal="true" aria-labelledby="orbita-local-account-title">' +
            '  <h3 id="orbita-local-account-title" class="orbita-avito-cred-modal__title">Папка профиля Chrome</h3>' +
            '  <p class="orbita-avito-cred-modal__hint" data-local-account-account></p>' +
            '  <label class="orbita-avito-cred-modal__label">Имя аккаунта' +
            '    <input type="text" class="orbita-avito-cred-modal__input" data-local-account-name maxlength="200" autocomplete="off" />' +
            '  </label>' +
            '  <label class="orbita-avito-cred-modal__label">Папка профиля (User Data)' +
            '    <input type="text" class="orbita-avito-cred-modal__input" data-local-account-dir maxlength="1024" autocomplete="off" spellcheck="false" />' +
            '  </label>' +
            '  <p class="orbita-avito-cred-modal__status" data-local-account-status>Отдельная папка на машине воркера. Стандартный профиль Chrome использовать нельзя. Папка на диске не удаляется.</p>' +
            '  <div class="orbita-avito-cred-modal__actions">' +
            '    <button type="button" class="orbita-avito-cred-modal__btn" data-local-account-close>Отмена</button>' +
            '    <button type="button" class="orbita-avito-cred-modal__btn orbita-avito-cred-modal__btn--primary" data-local-account-save>Сохранить</button>' +
            '  </div>' +
            '</div>';
        document.body.appendChild(wrap);
        return wrap;
    }

    runtime.openLocalAccountModal = function openLocalAccountModal(opts) {
        var modal = runtime.ensureLocalAccountModal();
        var titleEl = modal.querySelector('#orbita-local-account-title');
        var nameInput = modal.querySelector('[data-local-account-name]');
        var dirInput = modal.querySelector('[data-local-account-dir]');
        var accountEl = modal.querySelector('[data-local-account-account]');
        var statusEl = modal.querySelector('[data-local-account-status]');
        var saveBtn = modal.querySelector('[data-local-account-save]');
        var originalDir = (opts.userDataDir || '').trim();

        if (titleEl) {
            titleEl.textContent = opts.focusName ? 'Переименовать аккаунт' : 'Папка профиля Chrome';
        }
        accountEl.textContent = opts.accountName
            ? ('Аккаунт: ' + opts.accountName)
            : '';
        nameInput.value = opts.accountName || '';
        dirInput.value = opts.userDataDir || '';
        dirInput.placeholder = opts.managed
            ? 'Оставьте пустым, чтобы сохранить автоматический профиль'
            : 'D:\\Orbita\\ChromeProfiles\\account-1';
        if (statusEl) {
            statusEl.textContent = opts.managed
                ? 'Профиль создаётся автоматически на машине воркера. Путь можно задать, только если подключаете уже существующую папку. Папка на диске не удаляется.'
                : 'Отдельная папка на машине воркера. Стандартный профиль Chrome использовать нельзя. Папка на диске не удаляется.';
        }
        modal.hidden = false;
        window.setTimeout(function () {
            var focusInput = opts.focusName ? nameInput : dirInput;
            if (focusInput) {
                focusInput.focus();
                if (typeof focusInput.select === 'function') focusInput.select();
            }
        }, 0);

        function close() {
            modal.hidden = true;
            saveBtn.onclick = null;
            modal.querySelectorAll('[data-local-account-close]').forEach(function (el) {
                el.onclick = null;
            });
        }

        modal.querySelectorAll('[data-local-account-close]').forEach(function (el) {
            el.onclick = close;
        });

        saveBtn.onclick = async function () {
            var displayName = (nameInput.value || '').trim();
            var localUserDataDir = (dirInput.value || '').trim();
            if (!displayName) {
                runtime.showToast('Укажите имя аккаунта', { variant: 'error' });
                return;
            }
            if (!localUserDataDir && !opts.managed) {
                runtime.showToast('Укажите путь к отдельной папке профиля', { variant: 'error' });
                return;
            }

            saveBtn.disabled = true;
            var payload = {
                workerId: opts.workerId,
                accountId: opts.accountId,
                displayName: displayName
            };
            if (localUserDataDir !== originalDir) {
                payload.localUserDataDir = localUserDataDir;
            }
            var result = await runtime.postForm(opts.postUrl, payload);
            saveBtn.disabled = false;

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
    }

    runtime.initLocalAccountEditButtons = function initLocalAccountEditButtons() {
        document.querySelectorAll('[data-local-account-edit]').forEach(function (btn) {
            if (btn.hasAttribute('data-local-account-edit-bound')) return;
            btn.setAttribute('data-local-account-edit-bound', '1');

            btn.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                runtime.closeAllRowMenus();
                runtime.openLocalAccountModal({
                    workerId: btn.getAttribute('data-worker-id'),
                    accountId: btn.getAttribute('data-account-id'),
                    accountName: btn.getAttribute('data-account-name') || '',
                    userDataDir: btn.getAttribute('data-user-data-dir') || '',
                    managed: btn.getAttribute('data-managed') === 'true',
                    focusName: btn.hasAttribute('data-local-account-rename'),
                    postUrl: btn.getAttribute('data-post-url') || '/Workers/UpdateLocalAccount'
                });
            });
        });

        document.querySelectorAll('[data-local-account-delete]').forEach(function (btn) {
            if (btn.hasAttribute('data-local-account-delete-bound')) return;
            btn.setAttribute('data-local-account-delete-bound', '1');

            btn.addEventListener('click', async function (e) {
                e.preventDefault();
                e.stopPropagation();
                runtime.closeAllRowMenus();

                var confirmed = true;
                if (window.Orbita && window.Orbita.confirm) {
                    confirmed = await window.Orbita.confirm({
                        title: 'Удалить аккаунт из панели?',
                        message: 'Папка профиля на диске не удалится.',
                        confirmLabel: 'Удалить',
                        variant: 'danger'
                    });
                }
                if (!confirmed) return;

                var result = await runtime.postForm(btn.getAttribute('data-post-url') || '/Workers/DeleteLocalAccount', {
                    workerId: btn.getAttribute('data-worker-id'),
                    accountId: btn.getAttribute('data-account-id')
                });
                if (result.ok) {
                    runtime.showToast((result.payload && result.payload.message) || 'Аккаунт удалён', { variant: 'success' });
                    if (window.OrbitaLive && window.OrbitaLive.scheduleRefresh) {
                        window.OrbitaLive.scheduleRefresh({ kinds: ['Accounts', 'Workers'] });
                    }
                } else {
                    runtime.showToast((result.payload && result.payload.error) || 'Не удалось удалить', { variant: 'error' });
                }
            });
        });
    }

    runtime.initWorkerRenameModal = function initWorkerRenameModal() {
        var modal = document.getElementById('workerRenameModal');
        if (!modal) return;

        function openModal() {
            if (typeof runtime.closeAllRowMenus === 'function') {
                runtime.closeAllRowMenus();
            }
            modal.removeAttribute('hidden');
            var input = modal.querySelector('[data-worker-rename-input]');
            if (input) {
                input.focus();
                if (typeof input.select === 'function') input.select();
            }
        }

        function closeModal() {
            modal.setAttribute('hidden', '');
        }

        document.querySelectorAll('[data-worker-rename-open]').forEach(function (btn) {
            if (btn.hasAttribute('data-worker-rename-bound')) return;
            btn.setAttribute('data-worker-rename-bound', '1');
            btn.addEventListener('click', function (e) {
                e.preventDefault();
                e.stopPropagation();
                openModal();
            });
        });

        modal.querySelectorAll('[data-worker-rename-close]').forEach(function (el) {
            if (el.hasAttribute('data-worker-rename-close-bound')) return;
            el.setAttribute('data-worker-rename-close-bound', '1');
            el.addEventListener('click', closeModal);
        });

        if (!window.__orbitaWorkerRenameModalKeydown) {
            document.addEventListener('keydown', function (e) {
                if (e.key !== 'Escape') return;
                var openModalEl = document.getElementById('workerRenameModal');
                if (openModalEl && !openModalEl.hasAttribute('hidden')) {
                    openModalEl.setAttribute('hidden', '');
                }
            });
            window.__orbitaWorkerRenameModalKeydown = true;
        }
    }

    runtime.initProviderConnectionButtons = function initProviderConnectionButtons() {
        function bind(selector, path, pendingLabel) {
            document.querySelectorAll(selector).forEach(function (btn) {
                if (btn.hasAttribute('data-provider-bound')) return;
                btn.setAttribute('data-provider-bound', '1');
                btn.addEventListener('click', async function (e) {
                    e.preventDefault();
                    e.stopPropagation();
                    var dirty = document.querySelector('[data-worker-settings-dirty]');
                    if (dirty && !dirty.hidden) {
                        runtime.showToast('Сначала сохраните настройки', { variant: 'error' });
                        return;
                    }
                    if (btn.disabled) return;
                    var workerId = btn.getAttribute('data-worker-id');
                    var provider = btn.getAttribute('data-provider-check') || btn.getAttribute('data-provider-sync');
                    if (!workerId || !provider) return;
                    var isCheck = btn.hasAttribute('data-provider-check');
                    document.querySelectorAll(isCheck ? '[data-provider-check]' : '[data-provider-sync]').forEach(function (other) {
                        other.disabled = true;
                    });
                    var card = btn.closest('[data-provider-card]');
                    if (card && selector.indexOf('provider-check') >= 0) {
                        card.setAttribute('data-provider-status', 'checking');
                        var label = card.querySelector('[data-provider-status-label]');
                        if (label) {
                            label.className = 'worker-provider-status worker-provider-status--checking';
                            label.textContent = 'Проверяется';
                        }
                        var message = card.querySelector('[data-provider-message]');
                        if (message) message.textContent = pendingLabel;
                    }
                    var result = await runtime.postForm(path, { workerId: workerId, provider: provider });
                    if (result.ok) {
                        runtime.showToast((result.payload && result.payload.message) || pendingLabel, { variant: 'success' });
                        if (window.OrbitaLive && window.OrbitaLive.scheduleRefresh) {
                            window.OrbitaLive.scheduleRefresh({ kinds: ['Workers'] });
                        }
                    } else {
                        document.querySelectorAll(isCheck ? '[data-provider-check]' : '[data-provider-sync]').forEach(function (other) {
                            var allowed = other.getAttribute(isCheck ? 'data-can-check' : 'data-can-sync') === 'true';
                            other.disabled = !allowed;
                        });
                        runtime.showToast((result.payload && result.payload.error) || 'Не удалось отправить запрос', { variant: 'error' });
                    }
                });
            });
        }

        bind('[data-provider-check]', '/Workers/CheckProvider', 'Проверка на воркере…');
        bind('[data-provider-sync]', '/Workers/SyncProvider', 'Синхронизация запущена на воркере.');
    };

    runtime.initLocalOpenBrowserButtons = function initLocalOpenBrowserButtons() {
        document.querySelectorAll('[data-local-open-browser]').forEach(function (btn) {
            if (btn.hasAttribute('data-local-open-browser-bound')) return;
            btn.setAttribute('data-local-open-browser-bound', '1');

            btn.addEventListener('click', async function (e) {
                e.preventDefault();
                e.stopPropagation();
                runtime.closeAllRowMenus();
                if (btn.disabled) return;

                var workerId = btn.getAttribute('data-worker-id');
                var accountId = btn.getAttribute('data-account-id');
                if (!workerId || !accountId) return;

                btn.disabled = true;
                var result = await runtime.postForm(btn.getAttribute('data-post-url') || '/Workers/OpenLocalBrowser', {
                    workerId: workerId,
                    accountId: accountId
                });
                btn.disabled = false;

                if (result.ok) {
                    runtime.showToast(
                        (result.payload && result.payload.message) || 'На машине воркера открывается Chrome. Войдите в Avito и закройте браузер.',
                        { variant: 'success' });
                    if (window.OrbitaLive && window.OrbitaLive.scheduleRefresh) {
                        window.OrbitaLive.scheduleRefresh({ kinds: ['Accounts', 'Workers'] });
                    }
                } else {
                    runtime.showToast((result.payload && result.payload.error) || 'Не удалось открыть браузер', { variant: 'error' });
                }
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
    runtime.initProviderConnectionButtons();
    runtime.initAvitoCredentialsButtons();
    runtime.initLocalProfileSettingsButtons();
    runtime.initLocalAccountEditButtons();
    runtime.initWorkerRenameModal();
    runtime.initLocalOpenBrowserButtons();
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
        var crmTasksEl = document.querySelector('[data-nav-badge="crm-tasks"]');
        runtime.setBadge(errorsEl, payload.errorsToday);
        runtime.setBadge(responsesEl, payload.uniqueResponsesToday, ' уникальных откликов за сегодня');
        var crmNotificationStateKnown = typeof payload.crmTaskNotificationsEnabled === 'boolean';
        if (crmNotificationStateKnown) {
            runtime.setBadge(crmTasksEl, payload.crmOpenTasks, ' открытых задач CRM');
            if (window.OrbitaNotifications) {
                if (typeof window.OrbitaNotifications.setEnabled === 'function') {
                    window.OrbitaNotifications.setEnabled(payload.crmTaskNotificationsEnabled);
                }
                if (typeof window.OrbitaNotifications.setUnreadCount === 'function') {
                    window.OrbitaNotifications.setUnreadCount(payload.crmTaskNotificationsUnread);
                }
            }
        }
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
        el.textContent = count > 999 ? '999+' : String(count);
        el.setAttribute('aria-label', count + (suffix || ' новых'));
    }

    runtime.fetchNavBadges = function fetchNavBadges() {
        fetch('/Nav/Badges', { credentials: 'same-origin' })
            .then(function (res) { return res.ok ? res.json() : null; })
            .then(runtime.updateNavBadges)
            .catch(function () { });
    }

})(window.OrbitaRuntime = window.OrbitaRuntime || {});
