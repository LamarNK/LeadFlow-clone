(function () {
    var highlightMs = 1800;

    function stableJson(value) {
        return JSON.stringify(value);
    }

    function escapeHtml(text) {
        return String(text)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function highlightCard(el) {
        if (!el) return;
        el.classList.remove('orbita-live-updated');
        void el.offsetWidth;
        el.classList.add('orbita-live-updated');
        window.setTimeout(function () {
            el.classList.remove('orbita-live-updated');
        }, highlightMs);
    }

    function updateUpdatedClock(isoUtc) {
        if (!isoUtc) return;
        document.querySelectorAll('.orbita-updated-time').forEach(function (el) {
            el.setAttribute('data-orbita-utc', isoUtc);
            if (window.OrbitaTime) {
                window.OrbitaTime.localizeElement(el);
            }
        });
    }

    function setRefreshBusy(busy) {
        document.querySelectorAll('[data-orbita-refresh]').forEach(function (btn) {
            btn.classList.toggle('is-refreshing', busy);
            if (busy) {
                btn.setAttribute('aria-busy', 'true');
            } else {
                btn.removeAttribute('aria-busy');
            }
        });
    }

    function animateKpiValue(el, from, to, suffix, duration, delay) {
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        suffix = suffix || '';
        if (reduced) {
            el.textContent = Math.round(to) + suffix;
            el.setAttribute('data-kpi-count', String(to));
            el.setAttribute('data-kpi-suffix', suffix);
            return;
        }

        var startAt = 0;
        duration = duration || 720;
        delay = delay || 0;

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
            var value = Math.round(from + (to - from) * easeOutCubic(t));
            el.textContent = value + suffix;
            if (t < 1) {
                requestAnimationFrame(frame);
            } else {
                el.setAttribute('data-kpi-count', String(to));
                el.setAttribute('data-kpi-suffix', suffix);
            }
        }

        requestAnimationFrame(frame);
    }

    function updateKpiCards(kpiCards, highlightChanged) {
        (kpiCards || []).forEach(function (card, index) {
            var el = document.querySelector('[data-kpi-key="' + card.key + '"]');
            if (!el) return;

            var valueEl = el.querySelector('[data-kpi-count]');
            if (!valueEl) return;

            var suffix = card.valueSuffix || '';
            var prev = parseFloat(valueEl.getAttribute('data-kpi-count'));
            var next = card.countValue;
            if (isNaN(prev)) prev = 0;

            if (prev !== next || valueEl.getAttribute('data-kpi-suffix') !== suffix) {
                animateKpiValue(valueEl, prev, next, suffix, 720, 80 + index * 70);
            }

            var deltaEl = el.querySelector('.kpi-delta-pill');
            if (deltaEl && card.delta) {
                deltaEl.textContent = card.delta;
                deltaEl.className = 'kpi-delta-pill kpi-delta-' + (card.deltaTone || 'neutral');
            }

            if (highlightChanged && prev !== next) {
                highlightCard(el);
            }
        });
    }

    function getLiveRoot() {
        return document.querySelector('[data-orbita-live]');
    }

    function getLiveAttr(name) {
        var root = getLiveRoot();
        return root ? (root.getAttribute(name) || '') : '';
    }

    function urlFromTemplate(template, token, value) {
        if (!template) return '#';
        return template.split(token).join(encodeURIComponent(String(value)));
    }

    function getRequestVerificationToken() {
        var input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    }

    function rowMenuShell(dropdownClass, itemsHtml) {
        return '<div class="row-menu" data-row-menu>' +
            '<button type="button" class="row-menu-btn" aria-label="Действия" aria-expanded="false" aria-haspopup="true">' +
            '<i class="fa-solid fa-ellipsis-vertical" aria-hidden="true"></i></button>' +
            '<div class="row-menu-dropdown ' + dropdownClass + '" hidden>' + itemsHtml + '</div></div>';
    }

    function formatPhone(phoneRaw, phoneNormalized) {
        var source = phoneNormalized || phoneRaw || '';
        var digits = String(source).replace(/\D/g, '');
        if (digits.length === 11 && digits.charAt(0) === '7') {
            return '+7 ' + digits.slice(1, 4) + ' ' + digits.slice(4, 7) + '-' + digits.slice(7, 9) + '-' + digits.slice(9, 11);
        }
        if (digits.length === 10) {
            return '+7 ' + digits.slice(0, 3) + ' ' + digits.slice(3, 6) + '-' + digits.slice(6, 8) + '-' + digits.slice(8, 10);
        }
        return String(phoneRaw || '').trim();
    }

    function isPhoneHidden(phoneRaw, phoneNormalized) {
        return !String(phoneRaw || '').trim() && !String(phoneNormalized || '').trim();
    }

    function displayAdId(sourceResponseId, vacancyUrl) {
        if (sourceResponseId && String(sourceResponseId).trim()) {
            return String(sourceResponseId).trim();
        }
        var match = String(vacancyUrl || '').match(/\/(\d{6,})(?:\?|$)/);
        return match ? match[1] : '—';
    }

    function displayAuthor(fullName) {
        return fullName && String(fullName).trim() ? String(fullName).trim() : 'Неизвестный пользователь';
    }

    function renderResponseAccountCell(accountName, subProfileName, accountUrl) {
        var account = accountName && String(accountName).trim() ? String(accountName).trim() : '—';
        var sub = subProfileName && String(subProfileName).trim() ? String(subProfileName).trim() : '';
        var html = '<a href="' + escapeHtml(accountUrl) + '">' + escapeHtml(account) + '</a>';
        if (sub) {
            html += '<span class="responses-account-sub" title="Субпрофиль Avito">' + escapeHtml(sub) + '</span>';
        }
        return html;
    }

    function shouldShowMachineName(displayName, machineName) {
        if (!machineName || !String(machineName).trim()) return false;
        return String(displayName || '').trim().toLowerCase() !== String(machineName).trim().toLowerCase();
    }

    function formatBalance(value) {
        var n = Number(value);
        if (isNaN(n)) return '0 ₽';
        return Math.round(n).toLocaleString('ru-RU') + ' ₽';
    }

    function renderAccountBalance(account) {
        var total = formatBalance(account.balance);
        var title = account.hasMultipleSubProfiles ? ' title="Сумма авансов по субпрофилям"' : ' title="Аванс"';
        var html = '<div class="account-balance"><span class="account-balance-total"' + title + '>' + total + '</span>';
        if (account.balanceSubtitle) {
            html += '<span class="account-balance-breakdown">' + escapeHtml(account.balanceSubtitle) + '</span>';
        }
        html += '</div>';
        return html;
    }

    function updatePaginationInfo(pagination) {
        if (!pagination) return;
        document.querySelectorAll('[data-orbita-live-pagination] .orbita-pagination__info').forEach(function (el) {
            if (!pagination.totalItems) {
                el.textContent = 'Показано 0 из 0';
            } else {
                var start = pagination.rangeStart;
                var end = pagination.rangeEnd;
                el.textContent = 'Показано ' + start + '–' + end + ' из ' + pagination.totalItems;
            }
        });
    }

    function renderActivityPill(label, tone, isLive) {
        if (!label) return '<span class="worker-activity-pill worker-activity-pill--muted">—</span>';
        var liveClass = isLive ? ' worker-activity-pill--live' : '';
        var dot = isLive ? '<span class="worker-activity-pill-dot" aria-hidden="true"></span>' : '';
        return '<span class="worker-activity-pill worker-activity-pill--' + escapeHtml(tone || 'muted') + liveClass + '" title="' + escapeHtml(label) + '">' +
            dot + '<span class="worker-activity-pill-text">' + escapeHtml(label) + '</span></span>';
    }

    function renderSubProfileStatusBadges(sub, isProcessing) {
        var badges = '';
        if (isProcessing) {
            badges += '<span class="subprofiles-badge subprofiles-badge--processing"><span class="subprofiles-badge-dot" aria-hidden="true"></span>сейчас</span>';
        } else if (sub.isCurrent) {
            badges += '<span class="subprofiles-badge subprofiles-badge--current">текущий</span>';
        }
        if (!sub.isEnabledInPanel) {
            badges += '<span class="subprofiles-badge subprofiles-badge--off">выкл</span>';
        }
        return badges ? '<span class="subprofiles-status-badges">' + badges + '</span>' : '';
    }

    function getSubprofilePanelRows(panelId) {
        if (!panelId) return [];
        return Array.prototype.slice.call(document.querySelectorAll('[data-subprofiles-panel="' + panelId + '"]'));
    }

    function setSubprofilePanelExpanded(panelId, expanded) {
        getSubprofilePanelRows(panelId).forEach(function (row) {
            row.classList.toggle('subprofiles-data-row--collapsed', !expanded);
        });
    }

    function normalizeSubProfile(sub) {
        if (!sub) return {};
        var id = sub.id || sub.Id || '';
        var name = sub.name || sub.Name || '';
        if (!name) name = id;
        if (!id && name) id = name;
        return {
            id: id,
            name: name,
            category: sub.category || sub.Category || '',
            isCurrent: !!(sub.isCurrent || sub.IsCurrent),
            isEnabledInPanel: sub.isEnabledInPanel !== false && sub.IsEnabledInPanel !== false,
            statusLabel: sub.statusLabel || sub.StatusLabel || '',
            statusTone: sub.statusTone || sub.StatusTone || 'success',
            balanceText: sub.balanceText || sub.BalanceText || '—',
            ratingText: sub.ratingText || sub.RatingText || '',
            responses: sub.responses || sub.Responses || 0,
            uniqueResponses: sub.uniqueResponses || sub.UniqueResponses || 0,
            errors: sub.errors || sub.Errors || 0,
            lastActivityUtc: sub.lastActivityUtc || sub.LastActivityUtc || null,
            isProcessingNow: !!(sub.isProcessingNow || sub.IsProcessingNow),
            processingLabel: sub.processingLabel || sub.ProcessingLabel || '',
            processingTone: sub.processingTone || sub.ProcessingTone || 'live',
            hasIssue: !!(sub.hasIssue || sub.HasIssue),
            issueSummary: sub.issueSummary || sub.IssueSummary || '',
            diagnosticAttachmentId: sub.diagnosticAttachmentId || sub.DiagnosticAttachmentId || null
        };
    }

    function accountSubProfilesRenderable(account) {
        var items = (account && account.subProfiles) || [];
        if (!items.length) return false;
        return items.some(function (sub) {
            var normalized = normalizeSubProfile(sub);
            return !!(normalized.id || normalized.name);
        });
    }

    function renderSubProfileRowCells(rawSub, workerId, accountId, layout, showOfficeColumn) {
        var sub = normalizeSubProfile(rawSub);
        var category = sub.category
            ? '<span class="subprofiles-tag">' + escapeHtml(sub.category) + '</span>'
            : '';
        var rating = sub.ratingText
            ? '<span class="subprofiles-rating">' + escapeHtml(sub.ratingText) + '</span>'
            : '';
        var alert = '';
        if (sub.hasIssue && sub.issueSummary) {
            var screenshot = sub.diagnosticAttachmentId
                ? '<button type="button" class="subprofiles-screenshot-btn" data-subprofile-screenshot data-screenshot-url="/Diagnostics/Image/' + escapeHtml(sub.diagnosticAttachmentId) + '" title="Посмотреть скриншот страницы при ошибке">скрин</button>'
                : '';
            alert = '<div class="subprofiles-item-alert">' +
                '<span class="subprofiles-issue" title="' + escapeHtml(sub.issueSummary) + '">' + escapeHtml(sub.issueSummary) + '</span>' +
                screenshot +
                '</div>';
        }
        var statusHtml = '<span class="account-status account-status--' + escapeHtml(sub.statusTone || 'success') + '">' +
            '<i class="fa-solid fa-circle account-status-dot" aria-hidden="true"></i>' +
            escapeHtml(sub.statusLabel || '') + '</span>';
        var processingHtml = sub.processingLabel
            ? renderActivityPill(sub.processingLabel, sub.processingTone, sub.isProcessingNow)
            : '<span class="worker-activity-pill worker-activity-pill--muted">—</span>';
        var activityHtml = sub.lastActivityUtc
            ? '<time data-orbita-utc="' + escapeHtml(sub.lastActivityUtc) + '" data-orbita-format="activity"></time>'
            : '—';
        var tail = layout === 'accounts'
            ? '<td class="cell-num" data-label="Уникальных">' + (sub.uniqueResponses || 0) + '</td>' +
            '<td class="cell-num" data-label="Ошибок">' + (sub.errors || 0) + '</td>' +
            '<td data-label="Последняя активность">' + activityHtml + '</td>' +
            '<td class="data-table-menu subprofiles-data-empty" data-label=""></td>'
            : '<td data-label="Последняя активность">' + activityHtml + '</td>' +
            '<td class="cell-num" data-label="Ошибок">' + (sub.errors || 0) + '</td>' +
            '<td class="data-table-menu subprofiles-data-empty" data-label=""></td>';
        var extraCols = layout === 'accounts'
            ? '<td class="cell-worker subprofiles-data-empty" data-label="Воркер"></td>' +
            (showOfficeColumn ? '<td class="subprofiles-data-empty" data-label="Офис"></td>' : '')
            : '';
        return '<td class="cell-toggle" data-label="Вкл">' +
            '<label class="subprofiles-toggle-sm" title="' + (sub.isEnabledInPanel ? 'Отключить субпрофиль' : 'Включить субпрофиль') + '">' +
            '<input type="checkbox" data-subprofile-toggle data-worker-id="' + escapeHtml(workerId) + '" data-account-id="' + escapeHtml(accountId) + '" data-subprofile-id="' + escapeHtml(sub.id) + '"' + (sub.isEnabledInPanel ? ' checked' : '') + ' />' +
            '<span class="subprofiles-toggle-sm-slider"></span></label></td>' +
            '<td class="cell-name subprofiles-data-name" data-label="Аккаунт">' +
            '<div class="subprofiles-data-indent"><span class="subprofiles-name">' + escapeHtml(sub.name) + '</span>' + category + rating + '</div>' +
            alert + '</td>' +
            extraCols +
            '<td data-label="Статус">' + statusHtml + '</td>' +
            '<td data-label="Сейчас">' + processingHtml + '</td>' +
            '<td class="cell-num cell-balance" data-label="Баланс"><span class="subprofiles-balance">' + escapeHtml(sub.balanceText || '—') + '</span></td>' +
            '<td class="cell-num" data-label="Откликов">' + (sub.responses || 0) + '</td>' +
            tail;
    }

    function renderSubProfileTableRows(workerId, account, panelId, layout, activeSubProfileId, showOfficeColumn, startExpanded) {
        var items = (account && account.subProfiles) || [];
        if (!items.length) return '';
        return items.map(function (rawSub) {
            var sub = normalizeSubProfile(rawSub);
            var isProcessing = !!sub.isProcessingNow || !!(activeSubProfileId && sub.id === activeSubProfileId);
            var classes = 'subprofiles-data-row';
            if (sub.isCurrent) classes += ' subprofiles-data-row--current';
            if (isProcessing) classes += ' subprofiles-data-row--processing';
            if (sub.hasIssue) classes += ' subprofiles-data-row--issue';
            if (!sub.isEnabledInPanel) classes += ' subprofiles-data-row--disabled';
            if (!startExpanded) classes += ' subprofiles-data-row--collapsed';
            return '<tr class="' + classes + '" data-subprofiles-panel="' + escapeHtml(panelId) + '" data-subprofile-id="' + escapeHtml(sub.id) + '">' +
                renderSubProfileRowCells(rawSub, workerId, account.id, layout, showOfficeColumn) +
                '</tr>';
        }).join('');
    }

    function removeSubProfileTableRows(panelId) {
        getSubprofilePanelRows(panelId).forEach(function (row) {
            row.remove();
        });
    }

    function toggleSubprofiles(btn) {
        if (!btn) return;
        var panelId = btn.getAttribute('aria-controls');
        if (!panelId) return;

        var expanded = btn.getAttribute('aria-expanded') === 'true';
        var willExpand = !expanded;
        btn.setAttribute('aria-expanded', willExpand ? 'true' : 'false');
        var icon = btn.querySelector('.subprofiles-toggle-icon');
        if (icon) icon.classList.toggle('subprofiles-toggle-icon--open', willExpand);

        var rows = getSubprofilePanelRows(panelId);
        if (rows.length) {
            setSubprofilePanelExpanded(panelId, willExpand);
            return;
        }

        if (willExpand) {
            var accountRow = btn.closest('tr');
            if (!accountRow) return;
            var layout = accountRow.getAttribute('data-subprofiles-layout') || 'accounts';
            var showOffice = accountRow.getAttribute('data-subprofiles-show-office') === 'true';
            var accountId = accountRow.getAttribute('data-account-id');
            var activeSubId = accountRow.getAttribute('data-processing-subprofile-id') || null;
            var workerToggle = accountRow.querySelector('[data-account-enable-toggle]');
            var workerId = workerToggle ? workerToggle.getAttribute('data-worker-id') : '';
            var raw = accountRow.getAttribute('data-subprofiles-json');
            var items = [];
            if (raw) {
                try { items = JSON.parse(raw); } catch (e) { items = []; }
            }
            if (!items.length) return;
            var fakeAccount = { id: accountId, subProfiles: items };
            var html = renderSubProfileTableRows(workerId, fakeAccount, panelId, layout, activeSubId, showOffice, true);
            if (!html) return;
            var temp = document.createElement('tbody');
            temp.innerHTML = html;
            var insertAfter = accountRow;
            while (temp.firstChild) {
                insertAfter.insertAdjacentElement('afterend', temp.firstChild);
                insertAfter = insertAfter.nextElementSibling;
            }
            if (window.OrbitaTime) {
                window.OrbitaTime.localizeAll(accountRow.parentElement);
            }
            reinitLiveContent();
        }
    }

    function renderSubProfilesList(workerId, accountId, items, activeSubProfileId) {
        if (!items || !items.length) return '';
        return '<ul class="subprofiles-list">' + items.map(function (rawSub) {
            var sub = normalizeSubProfile(rawSub);
            var isProcessing = !!(activeSubProfileId && sub.id === activeSubProfileId);
            var classes = 'subprofiles-item';
            if (sub.isCurrent) classes += ' subprofiles-item--current';
            if (isProcessing) classes += ' subprofiles-item--processing';
            if (sub.hasIssue) classes += ' subprofiles-item--issue';
            if (!sub.isEnabledInPanel) classes += ' subprofiles-item--disabled';
            var category = sub.category
                ? '<span class="subprofiles-tag">' + escapeHtml(sub.category) + '</span>'
                : '';
            var alert = '';
            if (sub.hasIssue && sub.issueSummary) {
                var screenshot = sub.diagnosticAttachmentId
                    ? '<button type="button" class="subprofiles-screenshot-btn" data-subprofile-screenshot data-screenshot-url="/Diagnostics/Image/' + escapeHtml(sub.diagnosticAttachmentId) + '" title="Посмотреть скриншот страницы при ошибке">скрин</button>'
                    : '';
                alert = '<div class="subprofiles-item-alert">' +
                    '<span class="subprofiles-issue" title="' + escapeHtml(sub.issueSummary) + '">' + escapeHtml(sub.issueSummary) + '</span>' +
                    screenshot +
                    '</div>';
            }
            var rating = sub.ratingText
                ? '<span class="subprofiles-rating">' + escapeHtml(sub.ratingText) + '</span>'
                : '';
            return '<li class="' + classes + '" data-subprofile-id="' + escapeHtml(sub.id) + '">' +
                '<label class="subprofiles-toggle-sm" title="' + (sub.isEnabledInPanel ? 'Отключить субпрофиль' : 'Включить субпрофиль') + '">' +
                '<input type="checkbox" data-subprofile-toggle data-worker-id="' + escapeHtml(workerId) + '" data-account-id="' + escapeHtml(accountId) + '" data-subprofile-id="' + escapeHtml(sub.id) + '"' + (sub.isEnabledInPanel ? ' checked' : '') + ' />' +
                '<span class="subprofiles-toggle-sm-slider"></span></label>' +
                '<div class="subprofiles-item-main">' +
                '<div class="subprofiles-item-head">' +
                '<span class="subprofiles-name">' + escapeHtml(sub.name) + '</span>' + category + renderSubProfileStatusBadges(sub, isProcessing) +
                '</div>' + alert +
                '</div>' +
                '<div class="subprofiles-meta">' + rating +
                '<span class="subprofiles-balance">' + escapeHtml(sub.balanceText || '—') + '</span></div></li>';
        }).join('') + '</ul>';
    }

    function renderAccountEnableToggle(workerId, accountId, isEnabled, options) {
        options = options || {};
        var checked = isEnabled ? ' checked' : '';
        var title = isEnabled ? 'Отключить аккаунт в панели' : 'Включить аккаунт в панели';
        var urlAttr = options.toggleUrl ? ' data-toggle-url="' + escapeHtml(options.toggleUrl) + '"' : '';
        var refreshAttr = options.refreshKind ? ' data-toggle-refresh="' + escapeHtml(options.refreshKind) + '"' : '';
        return '<td class="cell-toggle" data-label="Вкл"><label class="worker-toggle" title="' + escapeHtml(title) + '">' +
            '<input type="checkbox" data-account-enable-toggle data-worker-id="' + escapeHtml(workerId) + '" data-account-id="' + escapeHtml(accountId) + '"' +
            urlAttr + refreshAttr + checked + ' />' +
            '<span class="worker-toggle-slider"></span></label></td>';
    }

    function renderSubProfilesToolbar(workerId, account, panelIdPrefix) {
        if (!account.canRefreshSubProfiles && !account.hasSubProfiles) return '';
        var refreshBtn = account.canRefreshSubProfiles
            ? '<button type="button" class="subprofiles-refresh-btn' + (account.isSubProfilesRefreshPending ? ' is-pending' : '') + '" data-refresh-subprofiles data-worker-id="' + escapeHtml(workerId) + '" data-account-id="' + escapeHtml(account.id) + '" title="' + (account.isSubProfilesRefreshPending ? 'Обновление запрошено — ждём воркер' : 'Обновить список субпрофилей') + '" aria-label="Обновить субпрофили"><i class="fa-solid fa-arrows-rotate subprofiles-refresh-icon" aria-hidden="true"></i></button>'
            : '';
        var panelId = panelIdPrefix + '-' + account.id;
        var toggle = '';
        var panel = '';
        if (account.hasSubProfiles) {
            var items = account.subProfiles || [];
            var hasIssues = items.some(function (sub) { return sub.hasIssue || sub.HasIssue; });
            var summaryClass = hasIssues ? ' subprofiles-summary--issue' : '';
            toggle = '<button type="button" class="subprofiles-toggle" data-subprofiles-toggle aria-expanded="false" aria-controls="' + escapeHtml(panelId) + '" title="' + escapeHtml(account.subProfilesSummary || '') + '">' +
                '<i class="fa-solid fa-chevron-right subprofiles-toggle-icon" aria-hidden="true"></i>' +
                '<span class="subprofiles-summary' + summaryClass + '">' + escapeHtml(account.subProfilesSummary || '') + '</span>' +
                '</button>';
        } else if (account.canRefreshSubProfiles) {
            toggle = '<span class="subprofiles-empty-hint">Субпрофили не обнаружены</span>';
        }
        return '<div class="subprofiles-section"><div class="subprofiles-toolbar">' + toggle + refreshBtn + '</div></div>';
    }

    function reinitLiveContent() {
        if (window.Orbita && typeof window.Orbita.reinitLiveContent === 'function') {
            window.Orbita.reinitLiveContent();
        }
    }

    function captureExpandedSubprofilePanels(container) {
        var expandedPanels = {};
        if (!container) return expandedPanels;
        container.querySelectorAll('.subprofiles-toggle[aria-expanded="true"]').forEach(function (btn) {
            var panelId = btn.getAttribute('aria-controls');
            if (panelId) expandedPanels[panelId] = true;
        });
        return expandedPanels;
    }

    function restoreExpandedSubprofilePanels(container, expandedPanels) {
        if (!container || !expandedPanels) return;
        Object.keys(expandedPanels).forEach(function (panelId) {
            var btn = container.querySelector('[aria-controls="' + panelId + '"]');
            if (!btn) return;
            btn.setAttribute('aria-expanded', 'true');
            var icon = btn.querySelector('.subprofiles-toggle-icon');
            if (icon) icon.classList.add('subprofiles-toggle-icon--open');
            setSubprofilePanelExpanded(panelId, true);
        });
    }

    window.OrbitaLiveShared = {
        stableJson: stableJson,
        escapeHtml: escapeHtml,
        highlightCard: highlightCard,
        updateUpdatedClock: updateUpdatedClock,
        setRefreshBusy: setRefreshBusy,
        updateKpiCards: updateKpiCards,
        getLiveRoot: getLiveRoot,
        getLiveAttr: getLiveAttr,
        urlFromTemplate: urlFromTemplate,
        getRequestVerificationToken: getRequestVerificationToken,
        rowMenuShell: rowMenuShell,
        formatPhone: formatPhone,
        isPhoneHidden: isPhoneHidden,
        displayAdId: displayAdId,
        displayAuthor: displayAuthor,
        renderResponseAccountCell: renderResponseAccountCell,
        shouldShowMachineName: shouldShowMachineName,
        formatBalance: formatBalance,
        renderAccountBalance: renderAccountBalance,
        updatePaginationInfo: updatePaginationInfo,
        renderActivityPill: renderActivityPill,
        normalizeSubProfile: normalizeSubProfile,
        accountSubProfilesRenderable: accountSubProfilesRenderable,
        renderSubProfilesList: renderSubProfilesList,
        renderSubProfileTableRows: renderSubProfileTableRows,
        removeSubProfileTableRows: removeSubProfileTableRows,
        toggleSubprofiles: toggleSubprofiles,
        setSubprofilePanelExpanded: setSubprofilePanelExpanded,
        renderSubProfilesToolbar: renderSubProfilesToolbar,
        renderAccountEnableToggle: renderAccountEnableToggle,
        captureExpandedSubprofilePanels: captureExpandedSubprofilePanels,
        restoreExpandedSubprofilePanels: restoreExpandedSubprofilePanels,
        reinitLiveContent: reinitLiveContent
    };
})();