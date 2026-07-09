(function () {
    function initKpiCounters() {
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

        document.querySelectorAll('.accounts-kpi-row [data-kpi-count]').forEach(function (el, index) {
            var target = parseFloat(el.getAttribute('data-kpi-count'));
            if (isNaN(target)) return;

            if (reduced) {
                el.textContent = Math.round(target).toString();
                return;
            }

            var duration = 720;
            var delay = 80 + index * 70;
            var startAt = 0;

            function easeOutCubic(t) {
                return 1 - Math.pow(1 - t, 3);
            }

            function frame(now) {
                if (!startAt) startAt = now;
                var elapsed = now - startAt;
                if (elapsed < delay) {
                    requestAnimationFrame(frame);
                    return;
                }

                var t = Math.min(1, (elapsed - delay) / duration);
                el.textContent = Math.round(target * easeOutCubic(t)).toString();
                if (t < 1) requestAnimationFrame(frame);
            }

            requestAnimationFrame(frame);
        });
    }

    function initRowNavigation() {
        document.querySelectorAll('.accounts-row[data-href]').forEach(function (row) {
            if (row.hasAttribute('data-accounts-row-bound')) return;
            row.setAttribute('data-accounts-row-bound', '1');
            row.addEventListener('click', function (e) {
                if (e.target.closest('[data-row-menu]') || e.target.closest('a') || e.target.closest('button')
                    || e.target.closest('.worker-toggle') || e.target.closest('.subprofiles-section')) return;
                var href = row.getAttribute('data-href');
                if (href) {
                    if (window.Orbita && typeof window.Orbita.navigateTo === 'function') {
                        window.Orbita.navigateTo(href, true);
                    } else {
                        window.location.href = href;
                    }
                }
            });
        });
    }

    function initAccountToggleButtons() {
        document.querySelectorAll('[data-account-toggle]').forEach(function (btn) {
            if (btn.hasAttribute('data-account-toggle-bound')) return;
            btn.setAttribute('data-account-toggle-bound', '1');

            btn.addEventListener('click', async function (e) {
                e.stopPropagation();
                var workerId = btn.getAttribute('data-worker-id');
                var accountId = btn.getAttribute('data-account-id');
                var enabled = btn.getAttribute('data-enabled') === 'true';
                if (!workerId || !accountId || !window.Orbita || !window.Orbita.postForm) return;

                var label = enabled ? 'Включить аккаунт в панели?' : 'Отключить аккаунт в панели?';
                if (window.Orbita.confirm) {
                    var confirmed = await window.Orbita.confirm({
                        title: label,
                        message: 'Воркер получит обновлённую конфигурацию при следующем опросе.',
                        confirmLabel: enabled ? 'Включить' : 'Отключить',
                        variant: enabled ? 'primary' : 'danger'
                    });
                    if (!confirmed) return;
                }

                var result = await window.Orbita.postForm('/Accounts/Toggle', {
                    workerId: workerId,
                    accountId: accountId,
                    enabled: enabled ? 'true' : 'false'
                });

                if (result.ok) {
                    window.Orbita.toast((result.payload && result.payload.message) || 'Сохранено', { variant: 'success' });
                    if (window.OrbitaLive && window.OrbitaLive.scheduleRefresh) {
                        window.OrbitaLive.scheduleRefresh({ kinds: ['Accounts'] });
                    }
                } else {
                    window.Orbita.toast((result.payload && result.payload.error) || 'Не удалось сохранить', { variant: 'error' });
                }
            });
        });
    }

    var shared = window.OrbitaLiveShared;
    var liveState = null;

    function workerDetailsUrl(id) {
        return shared.urlFromTemplate(shared.getLiveAttr('data-worker-details-url'), '__id__', id);
    }

    function accountSearchUrl(name) {
        return shared.urlFromTemplate(shared.getLiveAttr('data-account-search-url'), '__q__', name);
    }

    function responsesFilterUrl(accountId) {
        return shared.urlFromTemplate(shared.getLiveAttr('data-responses-filter-url'), '__id__', accountId);
    }

    function renderAccountMenu(account, accountUrl, workerUrl) {
        var toggle = account.isEnabledInPanel
            ? '<button type="button" class="row-menu-item row-menu-item--danger" data-account-toggle data-worker-id="' + shared.escapeHtml(account.workerId) + '" data-account-id="' + shared.escapeHtml(account.id) + '" data-enabled="false"><i class="fa-solid fa-ban" aria-hidden="true"></i>Отключить в панели</button>'
            : '<button type="button" class="row-menu-item" data-account-toggle data-worker-id="' + shared.escapeHtml(account.workerId) + '" data-account-id="' + shared.escapeHtml(account.id) + '" data-enabled="true"><i class="fa-solid fa-circle-check" aria-hidden="true"></i>Включить в панели</button>';
        return shared.rowMenuShell('row-menu-dropdown--accounts',
            '<a class="row-menu-item" href="' + shared.escapeHtml(accountUrl) + '"><i class="fa-regular fa-eye" aria-hidden="true"></i>Просмотр</a>' +
            '<a class="row-menu-item" href="' + shared.escapeHtml(workerUrl + '#worker-accounts') + '"><i class="fa-regular fa-pen-to-square" aria-hidden="true"></i>Настройки на воркере</a>' +
            '<a class="row-menu-item" href="' + shared.escapeHtml(responsesFilterUrl(account.id)) + '"><i class="fa-regular fa-clock" aria-hidden="true"></i>История откликов</a>' +
            toggle);
    }

    function subProfileHasIdentity(sub) {
        if (!sub) return false;
        return !!String(sub.name || sub.Name || sub.id || sub.Id || '').trim();
    }

    function enrichAccountsFromSsr(accounts) {
        var ssr = window.__ORBITA_SSR_SUBPROFILES__;
        if (!ssr || !ssr.length) return accounts || [];
        var byId = {};
        ssr.forEach(function (entry) {
            if (entry && entry.accountId) byId[entry.accountId] = entry.items || [];
        });
        return (accounts || []).map(function (acc) {
            if (!acc || !acc.hasSubProfiles) return acc;
            var items = acc.subProfiles || [];
            if (items.some(subProfileHasIdentity)) return acc;
            var fallback = byId[acc.id];
            if (!fallback || !fallback.length || !fallback.some(function (it) {
                return !!String(it.name || it.Name || it.id || it.Id || '').trim();
            })) {
                return acc;
            }
            return Object.assign({}, acc, {
                subProfiles: fallback.map(function (it) {
                    return {
                        id: it.id || it.Id || '',
                        name: it.name || it.Name || it.id || it.Id || '',
                        category: it.category || it.Category || '',
                        balanceText: it.balanceText || it.BalanceText || '—',
                        isEnabledInPanel: true
                    };
                })
            });
        });
    }

    function renderAccountRowHtml(account) {
        var workerUrl = workerDetailsUrl(account.workerId);
        var accountUrl = accountSearchUrl(account.accountName);
        var statusHtml = '<span class="account-status account-status--' + shared.escapeHtml(account.statusTone || 'active') + '"><i class="fa-solid fa-circle account-status-dot" aria-hidden="true"></i>' + shared.escapeHtml(account.statusLabel || '') + '</span>';
        if (account.lastErrorMessage) {
            statusHtml += '<span class="account-error-hint" title="' + shared.escapeHtml(account.lastErrorMessage) + '">' + shared.escapeHtml(account.lastErrorMessage) + '</span>';
        } else if (account.errorHint) {
            statusHtml += '<span class="account-error-hint" title="' + shared.escapeHtml(account.errorHint) + '">' + shared.escapeHtml(account.errorHint) + '</span>';
        }
        var activityHtml = account.lastActivityUtc
            ? '<time data-orbita-utc="' + shared.escapeHtml(account.lastActivityUtc) + '" data-orbita-format="activity"></time>'
            : '—';
        var subProfiles = shared.renderSubProfilesToolbar(account.workerId, account, 'subprofiles-acc');
        var processingHtml = account.processingLabel
            ? shared.renderActivityPill(account.processingLabel, account.processingTone, account.isProcessingNow)
            : '<span class="worker-activity-pill worker-activity-pill--muted">—</span>';
        var rowClass = 'accounts-row' + (account.isProcessingNow ? ' accounts-row--processing' : '');
        var showOffice = shared.getLiveAttr('data-show-office-column') === 'true';
        var officeCell = showOffice
            ? '<td data-label="Офис">' + shared.escapeHtml(account.officeName || '—') + '</td>'
            : '';
        var toggleCell = shared.renderAccountEnableToggle(account.workerId, account.id, account.isEnabledInPanel, {
            toggleUrl: '/Accounts/Toggle',
            refreshKind: 'Accounts'
        });
        var subProfilesJson = shared.escapeHtml(JSON.stringify(account.subProfiles || []));
        var processingSubProfileId = account.isProcessingNow && account.processingSubProfileId
            ? shared.escapeHtml(account.processingSubProfileId)
            : '';

        return '<tr class="' + rowClass + '" data-href="' + shared.escapeHtml(accountUrl) + '"' +
            ' data-account-id="' + shared.escapeHtml(account.id) + '"' +
            ' data-subprofiles-layout="accounts"' +
            ' data-subprofiles-show-office="' + (showOffice ? 'true' : 'false') + '"' +
            (processingSubProfileId ? ' data-processing-subprofile-id="' + processingSubProfileId + '"' : '') +
            ' data-subprofiles-json="' + subProfilesJson + '">' +
            toggleCell +
            '<td class="cell-account" data-label="Аккаунт"><a href="' + shared.escapeHtml(accountUrl) + '">' + shared.escapeHtml(account.accountName) + '</a>' + subProfiles + '</td>' +
            '<td class="cell-worker" data-label="Воркер"><a href="' + shared.escapeHtml(workerUrl) + '">' + shared.escapeHtml(account.workerName) + '</a></td>' +
            officeCell +
            '<td data-label="Статус">' + statusHtml + '</td>' +
            '<td data-label="Сейчас">' + processingHtml + '</td>' +
            '<td class="cell-num cell-balance" data-label="Баланс">' + shared.renderAccountBalance(account) + '</td>' +
            (function () {
                var metrics = shared.resolveAccountMetricLinks(account);
                return '<td class="cell-num" data-label="Откликов">' + shared.renderMetricLink(account.responses, metrics.responses, 'Отклики за сегодня') + '</td>' +
                    '<td class="cell-num" data-label="Уникальных">' + shared.renderMetricLink(account.uniqueResponses, metrics.unique, 'Уникальные отклики за сегодня') + '</td>' +
                    '<td class="cell-num" data-label="Ошибок">' + shared.renderMetricLink(account.errors, metrics.errors, 'Проблемы за сегодня') + '</td>';
            })() +
            '<td data-label="Последняя активность">' + activityHtml + '</td>' +
            '<td class="data-table-menu" data-label="">' + renderAccountMenu(account, accountUrl, workerUrl) + '</td></tr>';
    }

    function insertSubProfileRowsAfter(accountRow, account, panelId, wasExpanded) {
        if (!account.hasSubProfiles || !shared.accountSubProfilesRenderable(account)) {
            shared.removeSubProfileTableRows(panelId);
            return;
        }
        shared.removeSubProfileTableRows(panelId);
        var showOffice = shared.getLiveAttr('data-show-office-column') === 'true';
        var subRowsHtml = shared.renderSubProfileTableRows(
            account.workerId,
            account,
            panelId,
            'accounts',
            account.isProcessingNow ? account.processingSubProfileId : null,
            showOffice,
            wasExpanded);
        if (!subRowsHtml) return;
        var temp = document.createElement('tbody');
        temp.innerHTML = subRowsHtml;
        var insertAfter = accountRow;
        while (temp.firstChild) {
            insertAfter.insertAdjacentElement('afterend', temp.firstChild);
            insertAfter = insertAfter.nextElementSibling;
        }
    }

    function renderAccounts(rows) {
        var tbody = document.querySelector('[data-orbita-live-body="accounts"]');
        if (!tbody || !shared) return;
        rows = enrichAccountsFromSsr(rows);
        var expandedPanels = shared.captureExpandedSubprofilePanels(tbody);
        var nextIds = {};
        (rows || []).forEach(function (account) {
            nextIds[account.id] = true;
            var panelId = 'subprofiles-acc-' + account.id;
            var existingRow = tbody.querySelector('tr.accounts-row[data-account-id="' + account.id + '"]');
            var temp = document.createElement('tbody');
            temp.innerHTML = renderAccountRowHtml(account);
            var newRow = temp.firstElementChild;
            if (!newRow) return;

            var wasExpanded = !!expandedPanels[panelId];
            if (existingRow) {
                shared.removeSubProfileTableRows(panelId);
                existingRow.replaceWith(newRow);
            } else {
                tbody.appendChild(newRow);
            }

            if (account.hasSubProfiles) {
                insertSubProfileRowsAfter(newRow, account, panelId, wasExpanded);
            }

            if (wasExpanded) {
                var btn = newRow.querySelector('[aria-controls="' + panelId + '"]');
                if (btn) {
                    btn.setAttribute('aria-expanded', 'true');
                    var icon = btn.querySelector('.subprofiles-toggle-icon');
                    if (icon) icon.classList.add('subprofiles-toggle-icon--open');
                }
            }
        });

        tbody.querySelectorAll('tr.accounts-row[data-account-id]').forEach(function (row) {
            var accountId = row.getAttribute('data-account-id');
            if (!accountId || nextIds[accountId]) return;
            var panelId = 'subprofiles-acc-' + accountId;
            shared.removeSubProfileTableRows(panelId);
            row.remove();
        });

        if (window.OrbitaTime) {
            window.OrbitaTime.localizeAll(tbody);
        }
        shared.reinitLiveContent();
    }

    function applySnapshot(snapshot, highlightChanged) {
        if (!snapshot || !shared) return;
        var prev = liveState ? shared.stableJson(liveState.accounts) : null;
        var next = shared.stableJson(snapshot.accounts || []);
        shared.updateKpiCards(snapshot.kpiCards || [], highlightChanged);
        shared.updatePaginationInfo(snapshot.pagination);
        if (prev !== next) {
            renderAccounts(snapshot.accounts || []);
            if (highlightChanged) {
                shared.highlightCard(document.querySelector('.card--accounts-table'));
            }
            initRowNavigation();
            initAccountToggleButtons();
        }
        liveState = snapshot;
        shared.updateUpdatedClock(snapshot.updatedAtUtc);
    }

    function fetchSnapshot() {
        var root = shared && shared.getLiveRoot();
        if (!root) return Promise.resolve();
        var url = root.getAttribute('data-orbita-snapshot');
        if (!url) return Promise.resolve();
        return fetch(url, { credentials: 'same-origin', headers: { Accept: 'application/json' } })
            .then(function (res) {
                if (!res.ok) throw new Error('Accounts snapshot failed: ' + res.status);
                return res.json();
            })
            .then(function (snapshot) { applySnapshot(snapshot, true); });
    }

    function initAccountsPage() {
        initKpiCounters();
        initRowNavigation();
        initAccountToggleButtons();
        if (window.Orbita && typeof window.Orbita.initWorkerAccountEnableToggles === 'function') {
            window.Orbita.initWorkerAccountEnableToggles();
        }
        if (window.OrbitaLive && shared && shared.getLiveRoot()) {
            window.OrbitaLive.register('accounts', { fetchSnapshot: fetchSnapshot });
        }
    }

    initAccountsPage();
    document.addEventListener('orbita:content-updated', initAccountsPage);
})();