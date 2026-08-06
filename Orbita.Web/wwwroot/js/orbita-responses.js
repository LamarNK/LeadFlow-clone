(function () {
    function initKpiCounters() {
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        document.querySelectorAll('.responses-kpi-row [data-kpi-count]').forEach(function (el, index) {
            var target = parseFloat(el.getAttribute('data-kpi-count'));
            if (isNaN(target)) return;
            if (reduced) {
                el.textContent = Math.round(target).toString();
                return;
            }
            var duration = 720;
            var delay = 80 + index * 70;
            var startAt = 0;
            function easeOutCubic(t) { return 1 - Math.pow(1 - t, 3); }
            function frame(now) {
                if (!startAt) startAt = now;
                var elapsed = now - startAt;
                if (elapsed < delay) { requestAnimationFrame(frame); return; }
                var t = Math.min(1, (elapsed - delay) / duration);
                el.textContent = Math.round(target * easeOutCubic(t)).toString();
                if (t < 1) requestAnimationFrame(frame);
            }
            requestAnimationFrame(frame);
        });
    }

    var liveState = null;

    function getShared() {
        return window.OrbitaLiveShared || null;
    }

    function readRowValue(row, camelKey) {
        if (!row) return undefined;
        if (row[camelKey] !== undefined && row[camelKey] !== null) return row[camelKey];
        var pascalKey = camelKey.charAt(0).toUpperCase() + camelKey.slice(1);
        return row[pascalKey];
    }

    function readRowBool(row, camelKey) {
        return !!readRowValue(row, camelKey);
    }

    function readHighlightLabels(row) {
        var rawLabels = readRowValue(row, 'highlightLabels');
        var labels = Array.isArray(rawLabels)
            ? rawLabels
            : [];
        if (!labels.length) {
            var fallbackLabel = readRowValue(row, 'highlightLabel');
            if (fallbackLabel) labels = [fallbackLabel];
        }

        return labels
            .filter(function (label) { return label && String(label).trim(); })
            .map(function (label) { return String(label).trim(); })
            .filter(function (label, index, all) { return all.indexOf(label) === index; });
    }

    /** Matches CandidateGenders.FormatLabel on the server. */
    function formatGenderLabel(gender) {
        if (!gender) return '—';
        var g = String(gender).trim().toLowerCase();
        if (g === 'male') return 'Мужчина';
        if (g === 'female') return 'Женщина';
        return '—';
    }

    function candidateInitials(name) {
        return String(name || '').trim().split(/\s+/).filter(Boolean).slice(0, 2)
            .map(function (part) { return part.charAt(0).toLocaleUpperCase(); }).join('') || '—';
    }

    function formatRelativeResponseTime(value) {
        var date = new Date(value);
        var elapsed = Math.max(0, Date.now() - date.getTime());
        if (!isFinite(elapsed)) return '—';
        var minutes = Math.floor(elapsed / 60000);
        if (minutes < 1) return 'только что';
        if (minutes < 60) return minutes + ' мин назад';
        var hours = Math.floor(minutes / 60);
        if (hours < 24) return hours + ' ч назад';
        var days = Math.floor(hours / 24);
        return days + ' дн назад';
    }

    function localizeRelativeResponseTimes(root) {
        (root || document).querySelectorAll('[data-response-relative-time]').forEach(function (element) {
            element.textContent = formatRelativeResponseTime(element.getAttribute('data-orbita-utc'));
        });
    }

    function formatCollectionDuration(createdAtUtc, collectedAtUtc) {
        var createdAt = new Date(createdAtUtc);
        var collectedAt = new Date(collectedAtUtc);
        var elapsedMinutes = Math.round((collectedAt.getTime() - createdAt.getTime()) / 60000);
        if (!isFinite(elapsedMinutes) || elapsedMinutes <= 0) return '—';

        var hours = Math.floor(elapsedMinutes / 60);
        var minutes = elapsedMinutes % 60;
        if (hours === 0) return elapsedMinutes + ' мин';
        return minutes === 0 ? hours + ' ч' : hours + ' ч ' + minutes + ' мин';
    }

    function renderResponseTimingCell(createdAtUtc, collectedAtUtc, shared) {
        var duration = formatCollectionDuration(createdAtUtc, collectedAtUtc);
        var gapText = duration === '—' ? 'Разница: —' : 'Через ' + duration;
        return '<td class="responses-last-response" data-label="Отклик и сбор"><div class="responses-last-response__timeline">' +
            '<div class="responses-last-response__event responses-last-response__event--response"><span class="responses-last-response__dot"><i class="fa-solid fa-paper-plane" aria-hidden="true"></i></span><div><span>Отклик</span><time data-orbita-utc="' +
            shared.escapeHtml(createdAtUtc) + '" data-orbita-format="activity"></time></div></div>' +
            '<div class="responses-last-response__gap"><span aria-hidden="true"></span><strong>' + shared.escapeHtml(gapText) + '</strong></div>' +
            '<div class="responses-last-response__event responses-last-response__event--collection"><span class="responses-last-response__dot"><i class="fa-solid fa-box-archive" aria-hidden="true"></i></span><div><span>Сбор</span><time data-orbita-utc="' +
            shared.escapeHtml(collectedAtUtc) + '" data-orbita-format="activity"></time></div></div>' +
            '</div></td>';
    }

    function workerDetailsUrl(id) {
        var shared = getShared();
        if (!shared) return '#';
        return shared.urlFromTemplate(shared.getLiveAttr('data-worker-details-url'), '__id__', id);
    }

    function accountSearchUrl(name) {
        var shared = getShared();
        if (!shared) return '#';
        return shared.urlFromTemplate(shared.getLiveAttr('data-account-search-url'), '__q__', name);
    }

    function detailJsonUrl(id) {
        var shared = getShared();
        if (!shared) return '#';
        return shared.urlFromTemplate(shared.getLiveAttr('data-response-detail-json-url'), '__id__', id);
    }

    function getLiveRoot() {
        var shared = getShared();
        return shared ? shared.getLiveRoot() : null;
    }

    function getBitrixInstances() {
        var root = getLiveRoot();
        if (!root) return [];
        try {
            return JSON.parse(root.getAttribute('data-bitrix-instances-json') || '[]');
        } catch (e) {
            return [];
        }
    }

    function getOfficeOptions() {
        var root = getLiveRoot();
        if (!root) return [];
        try {
            return JSON.parse(root.getAttribute('data-office-options-json') || '[]');
        } catch (e) {
            return [];
        }
    }

    function getDeliverOptionsUrl() {
        var root = getLiveRoot();
        return root ? root.getAttribute('data-deliver-options-url') : null;
    }

    function writeDeliverOptionsToDom(offices, instances) {
        var root = getLiveRoot();
        if (!root) return;
        if (Array.isArray(offices)) {
            root.setAttribute('data-office-options-json', JSON.stringify(offices));
        }
        if (Array.isArray(instances)) {
            root.setAttribute('data-bitrix-instances-json', JSON.stringify(instances));
        }
    }

    function normalizeDeliverOptionsPayload(payload) {
        if (!payload) return { offices: [], instances: [] };
        var offices = payload.deliveryOffices || payload.DeliveryOffices || [];
        var instances = payload.sendBitrixInstances || payload.SendBitrixInstances || [];
        return { offices: offices, instances: instances };
    }

    /** Always load fresh office/Bitrix settings when opening the modal. */
    function fetchDeliverOptions() {
        var url = getDeliverOptionsUrl();
        if (!url) {
            return Promise.resolve({
                offices: getOfficeOptions(),
                instances: getBitrixInstances()
            });
        }

        return fetch(url, {
            method: 'GET',
            credentials: 'same-origin',
            headers: { Accept: 'application/json' },
            cache: 'no-store'
        }).then(function (res) {
            if (!res.ok) throw new Error('Deliver options failed: ' + res.status);
            return res.json();
        }).then(function (payload) {
            var normalized = normalizeDeliverOptionsPayload(payload);
            writeDeliverOptionsToDom(normalized.offices, normalized.instances);
            return normalized;
        }).catch(function () {
            return {
                offices: getOfficeOptions(),
                instances: getBitrixInstances()
            };
        });
    }

    function getSendBitrixUrl() {
        var shared = getShared();
        var root = shared && shared.getLiveRoot();
        return root ? root.getAttribute('data-send-bitrix-url') : null;
    }

    function getDeliverUrl() {
        var shared = getShared();
        var root = shared && shared.getLiveRoot();
        return root ? (root.getAttribute('data-deliver-url') || root.getAttribute('data-send-bitrix-url')) : null;
    }

    function getBulkSendUrl() {
        var shared = getShared();
        var root = shared && shared.getLiveRoot();
        return root ? root.getAttribute('data-bulk-send-bitrix-url') : null;
    }

    function getBulkDeliverUrl() {
        var shared = getShared();
        var root = shared && shared.getLiveRoot();
        return root ? (root.getAttribute('data-bulk-deliver-url') || root.getAttribute('data-bulk-send-bitrix-url')) : null;
    }

    function closeRowMenus() {
        if (window.Orbita && typeof window.Orbita.closeAllRowMenus === 'function') {
            window.Orbita.closeAllRowMenus();
        }
    }

    function showDialog(modal) {
        if (!modal || typeof modal.showModal !== 'function') return false;
        try {
            if (modal.open) modal.close();
            modal.showModal();
            return true;
        } catch (e) {
            console.warn('Responses dialog open failed:', e);
            return false;
        }
    }

    var SELECTION_STORAGE_KEY = 'orbita-responses-selected';
    var DELIVER_PREFS_KEY = 'orbita-responses-deliver-prefs';
    var BULK_SEND_MAX = 200;

    function loadDeliverPrefs() {
        try {
            var parsed = JSON.parse(localStorage.getItem(DELIVER_PREFS_KEY) || '{}');
            return parsed && typeof parsed === 'object' ? parsed : {};
        } catch (e) {
            return {};
        }
    }

    function saveDeliverPrefs(prefs) {
        try {
            localStorage.setItem(DELIVER_PREFS_KEY, JSON.stringify(prefs || {}));
        } catch (e) { /* ignore quota / private mode */ }
    }

    function readDeliverPrefsFromForm(form) {
        if (!form) return {};
        var crm = form.querySelector('[data-deliver-to-crm]');
        var bitrix = form.querySelector('[data-deliver-to-bitrix]');
        return {
            toCrm: !!(crm && crm.checked),
            toBitrix: !!(bitrix && bitrix.checked),
            officeIds: getCheckedValues(form, '[data-deliver-office-option]'),
            bitrixInstanceIds: getCheckedValues(form, '[data-deliver-bitrix-option]')
        };
    }

    function getCheckedValues(root, selector) {
        return Array.prototype.slice.call(root.querySelectorAll(selector + ':checked'))
            .map(function (el) { return String(el.value || ''); })
            .filter(Boolean);
    }

    function applyDeliverPrefsToForm(form, prefs) {
        prefs = prefs || {};
        var crm = form.querySelector('[data-deliver-to-crm]');
        var bitrix = form.querySelector('[data-deliver-to-bitrix]');
        // Default: CRM on if nothing stored yet.
        var toCrm = prefs.toCrm !== undefined ? !!prefs.toCrm : true;
        var toBitrix = prefs.toBitrix !== undefined ? !!prefs.toBitrix : false;
        // At least one channel — prefer stored, else CRM.
        if (!toCrm && !toBitrix) toCrm = true;
        if (crm) crm.checked = toCrm;
        if (bitrix) bitrix.checked = toBitrix;
    }

    function normalizePrefIdList(prefs) {
        prefs = prefs || {};
        var officeIds = Array.isArray(prefs.officeIds) ? prefs.officeIds.map(String) : [];
        var bitrixIds = Array.isArray(prefs.bitrixInstanceIds) ? prefs.bitrixInstanceIds.map(String) : [];
        // Migrate legacy single-id prefs.
        if (!officeIds.length && prefs.officeId) officeIds = [String(prefs.officeId)];
        if (!bitrixIds.length && prefs.bitrixInstanceId) bitrixIds = [String(prefs.bitrixInstanceId)];
        return { officeIds: officeIds, bitrixInstanceIds: bitrixIds };
    }

    function setCheckedByValues(root, selector, values) {
        var set = {};
        (values || []).forEach(function (v) { set[String(v)] = true; });
        root.querySelectorAll(selector).forEach(function (el) {
            el.checked = !!set[String(el.value || '')];
        });
    }

    function loadSelectionState() {
        try {
            var raw = sessionStorage.getItem(SELECTION_STORAGE_KEY);
            if (!raw) return { ids: new Set(), meta: {} };
            var parsed = JSON.parse(raw);
            if (Array.isArray(parsed)) {
                return { ids: new Set(parsed.map(String)), meta: {} };
            }
            return {
                ids: new Set((parsed.ids || []).map(String)),
                meta: parsed.meta || {}
            };
        } catch (e) {
            return { ids: new Set(), meta: {} };
        }
    }

    function saveSelectionState() {
        try {
            sessionStorage.setItem(SELECTION_STORAGE_KEY, JSON.stringify({
                ids: Array.from(selectedIds),
                meta: selectionMeta
            }));
        } catch (e) { }
    }

    var selectionState = loadSelectionState();
    var selectedIds = selectionState.ids;
    var selectionMeta = selectionState.meta;

    function captureRowMeta(row) {
        if (!row) return { card: '', canSend: false };
        return {
            card: row.getAttribute('data-response-card') || '',
            canSend: row.getAttribute('data-can-send') === 'true'
        };
    }

    function toast(message, variant) {
        if (window.Orbita && typeof window.Orbita.toast === 'function') {
            window.Orbita.toast(message, { variant: variant || 'info' });
        }
    }

    function updateBulkBar() {
        var bar = document.querySelector('[data-responses-bulk-bar]');
        var countEl = document.querySelector('[data-responses-bulk-count]');
        if (!bar || !countEl) return;
        countEl.textContent = String(selectedIds.size);
        bar.hidden = selectedIds.size === 0;
    }

    function syncSelectAllCheckbox() {
        var selectAll = document.querySelector('[data-responses-select-all]');
        var boxes = Array.prototype.slice.call(document.querySelectorAll('[data-response-select]'));
        if (!selectAll) return;
        if (!boxes.length) {
            selectAll.checked = false;
            selectAll.indeterminate = false;
            return;
        }
        var checkedCount = boxes.filter(function (cb) { return cb.checked; }).length;
        selectAll.checked = checkedCount === boxes.length;
        selectAll.indeterminate = checkedCount > 0 && checkedCount < boxes.length;
    }

    function syncRowCheckboxes() {
        document.querySelectorAll('[data-response-select]').forEach(function (cb) {
            cb.checked = selectedIds.has(cb.value);
        });
        syncSelectAllCheckbox();
    }

    function setSelection(id, checked, row) {
        if (checked) {
            selectedIds.add(id);
            if (row) selectionMeta[id] = captureRowMeta(row);
        } else {
            selectedIds.delete(id);
            delete selectionMeta[id];
        }
        saveSelectionState();
        updateBulkBar();
        syncSelectAllCheckbox();
    }

    function toggleSelectAll(checked) {
        document.querySelectorAll('[data-response-select]').forEach(function (cb) {
            var row = cb.closest('.responses-row');
            if (checked) {
                selectedIds.add(cb.value);
                if (row) selectionMeta[cb.value] = captureRowMeta(row);
            } else {
                selectedIds.delete(cb.value);
                delete selectionMeta[cb.value];
            }
            cb.checked = checked;
        });
        saveSelectionState();
        updateBulkBar();
        syncSelectAllCheckbox();
    }

    function clearSelection() {
        selectedIds.clear();
        selectionMeta = {};
        saveSelectionState();
        syncRowCheckboxes();
        updateBulkBar();
    }

    function getSelectedIds() {
        return Array.from(selectedIds);
    }

    function getRowCardCopy(row) {
        if (!row) return '';
        var card = row.getAttribute('data-response-card');
        if (card) return card;
        return buildCardCopyFromRow(readRowValue(row, 'id') ? row : null, row);
    }

    function buildCardCopyFromRow(snapshotRow, domRow) {
        var shared = getShared();
        if (!shared) return '';

        var fullName = domRow ? domRow.querySelector('.responses-author')?.textContent?.trim() : '';
        if (snapshotRow) {
            fullName = readRowValue(snapshotRow, 'fullName') || fullName;
        }

        var phoneHidden = snapshotRow ? readRowBool(snapshotRow, 'isPhoneHidden') : domRow?.getAttribute('data-phone-hidden') === 'true';
        var phoneDisplay = phoneHidden
            ? 'Скрыт'
            : (domRow ? domRow.getAttribute('data-phone') : '') || shared.formatPhone(
                readRowValue(snapshotRow, 'phoneRaw'),
                readRowValue(snapshotRow, 'phoneNormalized'));

        var city = snapshotRow ? (readRowValue(snapshotRow, 'city') || '—') : (domRow?.querySelector('.responses-city')?.textContent?.trim() || '—');
        var ageVal = snapshotRow ? readRowValue(snapshotRow, 'age') : parseInt(domRow?.querySelector('.responses-age')?.textContent || '0', 10);
        var ageText = ageVal > 0 ? ageVal + ' лет' : '—';
        var vacancy = snapshotRow ? (readRowValue(snapshotRow, 'vacancy') || '—') : (domRow?.querySelector('.responses-ad-link')?.textContent?.trim() || '—');
        var account = domRow ? domRow.querySelector('.responses-account')?.textContent?.trim() : '';
        if (snapshotRow) {
            account = shared.renderResponseAccountCell
                ? readRowValue(snapshotRow, 'accountName')
                : readRowValue(snapshotRow, 'accountName');
            var sub = readRowValue(snapshotRow, 'avitoSubProfileName');
            if (sub) account = account + ' · ' + sub;
        }
        var statusLabel = snapshotRow ? (readRowValue(snapshotRow, 'statusLabel') || '') : (domRow?.querySelector('.response-status-badge')?.textContent?.trim() || '');
        var collectedUtc = snapshotRow ? (readRowValue(snapshotRow, 'collectedAtUtc') || readRowValue(snapshotRow, 'createdAtUtc')) : domRow?.querySelector('[data-label="Сбор"] [data-orbita-utc]')?.getAttribute('data-orbita-utc');
        var respondedUtc = snapshotRow ? readRowValue(snapshotRow, 'createdAtUtc') : domRow?.querySelector('[data-label="Отклик"] [data-orbita-utc]')?.getAttribute('data-orbita-utc');
        var collectedText = collectedUtc && window.OrbitaTime
            ? window.OrbitaTime.formatUtc(collectedUtc, 'datetime').replace(',', '')
            : '—';
        var respondedText = respondedUtc && window.OrbitaTime
            ? window.OrbitaTime.formatUtc(respondedUtc, 'datetime').replace(',', '')
            : '—';
        var vacancyUrl = snapshotRow ? readRowValue(snapshotRow, 'vacancyUrl') : '';
        var messengerUrl = snapshotRow ? readRowValue(snapshotRow, 'messengerUrl') : '';
        var cardCopy = snapshotRow ? readRowValue(snapshotRow, 'cardCopy') : '';
        if (cardCopy) return cardCopy;

        var lines = [
            'Имя: ' + (shared.displayAuthor(fullName) || '—'),
            'Телефон: ' + (phoneDisplay || '—'),
            'Город: ' + (city || '—'),
            'Возраст: ' + ageText,
            'Вакансия: ' + (vacancy || '—'),
            'Аккаунт: ' + (account || '—'),
            'Статус: ' + (statusLabel || '—'),
            'Сбор: ' + collectedText,
            'Отклик: ' + respondedText,
            'Обработан: Ещё не обработан'
        ];
        if (messengerUrl) lines.push('Чат: ' + messengerUrl);
        if (vacancyUrl) lines.push('Вакансия (URL): ' + vacancyUrl);

        var deliveries = snapshotRow ? readDeliveries(snapshotRow) : [];
        deliveries.forEach(function (delivery) {
            var label = readRowValue(delivery, 'bitrixLabel') || 'Битрикс';
            var outcome = readRowValue(delivery, 'outcomeLabel') || mapDeliveryOutcomeLabel(readRowValue(delivery, 'outcome'));
            var err = readRowValue(delivery, 'errorMessage');
            var line = label + ' — ' + outcome;
            if (err) line += ' (' + err + ')';
            lines.push('Битрикс: ' + line);
        });

        return lines.join('\n');
    }

    function getSelectedCards() {
        var cards = [];
        getSelectedIds().forEach(function (id) {
            var meta = selectionMeta[id];
            if (meta && meta.card) {
                cards.push(meta.card);
                return;
            }
            var row = document.querySelector('.responses-row[data-response-id="' + id.replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"]');
            var card = getRowCardCopy(row);
            if (card) cards.push(card);
        });
        return cards;
    }

    function renderSelectCell(row) {
        var shared = getShared();
        if (!shared) return '';
        var rowId = readRowValue(row, 'id');
        return '<td class="responses-select-col" data-label="">' +
            '<input type="checkbox" class="responses-select-checkbox" data-response-select value="' +
            shared.escapeHtml(rowId) + '" aria-label="Выбрать отклик" /></td>';
    }

    function updateResponseUrl(id) {
        var url = new URL(window.location.href);
        if (id) url.searchParams.set('id', id);
        else url.searchParams.delete('id');
        window.history.replaceState({}, '', url.toString());
    }

    function openResponseDetail(id) {
        var url = detailJsonUrl(id);
        if (!url || !window.Orbita || !window.Orbita.openDetailModal) return;
        fetch(url, { credentials: 'same-origin', headers: { Accept: 'application/json' } })
            .then(function (res) { return res.ok ? res.json() : null; })
            .then(function (payload) {
                if (!payload) return;
                updateResponseUrl(id);
                window.Orbita.openDetailModal({
                    title: payload.title,
                    subtitle: payload.subtitle,
                    responseProfile: payload.profile || null,
                    sections: payload.sections || [],
                    chatMessages: payload.chatMessages || [],
                    links: (payload.links || []).map(function (l) {
                        return { href: l.href, label: l.label, icon: 'fa-solid fa-arrow-up-right-from-square' };
                    }),
                    primaryActions: payload.primaryActions || [],
                    copyText: payload.copyText || '',
                    copyLabel: payload.copyLabel || 'Копировать',
                    onClose: function () { updateResponseUrl(null); }
                });
            });
    }

    function ensureSendModal() {
        var modal = document.getElementById('responsesSendBitrixDialog');
        if (modal && modal.dataset.uiVersion === '5') return modal;
        if (modal) modal.remove();

        modal = document.createElement('dialog');
        modal.id = 'responsesSendBitrixDialog';
        modal.className = 'settings-dialog responses-send-dialog';
        modal.dataset.uiVersion = '5';
        modal.innerHTML =
            '<form method="post" class="settings-dialog-form responses-send-form" data-send-bitrix-form>' +
            '<input type="hidden" name="__RequestVerificationToken" />' +
            '<input type="hidden" name="Id" data-send-bitrix-response-id />' +
            '<input type="hidden" name="From" data-send-bitrix-from />' +
            '<input type="hidden" name="To" data-send-bitrix-to />' +
            '<input type="hidden" name="Status" data-send-bitrix-status />' +
            '<input type="hidden" name="WorkerId" data-send-bitrix-worker-id />' +
            '<input type="hidden" name="AccountId" data-send-bitrix-account-id />' +
            '<input type="hidden" name="BitrixDestination" data-send-bitrix-destination />' +
            '<input type="hidden" name="Vacancy" data-send-bitrix-vacancy />' +
            '<input type="hidden" name="Search" data-send-bitrix-search />' +
            '<input type="hidden" name="Page" data-send-bitrix-page />' +
            '<input type="hidden" name="Sort" data-send-bitrix-sort />' +
            '<input type="hidden" name="Dir" data-send-bitrix-dir />' +
            '<h2 class="settings-dialog-title">Отправить отклик</h2>' +
            '<p class="settings-dialog-subtitle">Можно выбрать CRM и Bitrix24 вместе. Запоминаем последний выбор.</p>' +
            '<div class="responses-send-channels" role="group" aria-label="Каналы">' +
            '<label class="responses-send-check">' +
            '<input type="checkbox" name="ToCrm" value="true" checked data-deliver-to-crm />' +
            '<span>CRM офиса</span>' +
            '</label>' +
            '<label class="responses-send-check">' +
            '<input type="checkbox" name="ToBitrix" value="true" data-deliver-to-bitrix />' +
            '<span>Bitrix24</span>' +
            '</label>' +
            '</div>' +
            '<div class="responses-send-fields">' +
            '<div class="responses-send-multiselect" data-deliver-office-field hidden>' +
            '<span class="settings-field-label">Офисы CRM <em>(можно несколько)</em></span>' +
            '<div class="responses-send-check-list" data-deliver-office-list></div>' +
            '<span class="responses-send-hint" data-deliver-office-hint></span>' +
            '</div>' +
            '<div class="responses-send-multiselect" data-deliver-bitrix-field hidden>' +
            '<span class="settings-field-label">Порталы Bitrix24 <em>(можно несколько; пусто = схема офиса)</em></span>' +
            '<div class="responses-send-check-list" data-deliver-bitrix-list></div>' +
            '</div>' +
            '</div>' +
            '<p class="responses-send-error" data-deliver-error hidden role="alert"></p>' +
            '<div class="settings-dialog-actions">' +
            '<button type="button" class="settings-secondary-btn" data-send-bitrix-cancel>Отмена</button>' +
            '<button type="submit" class="settings-primary-btn" data-deliver-submit>Отправить</button>' +
            '</div></form>';
        document.body.appendChild(modal);

        bindSendModalInteractions(modal);
        return modal;
    }

    function bindSendModalInteractions(modal) {
        var form = modal.querySelector('[data-send-bitrix-form]');
        if (!form || form.dataset.bound === '1') return;
        form.dataset.bound = '1';

        var crmToggle = form.querySelector('[data-deliver-to-crm]');
        var bitrixToggle = form.querySelector('[data-deliver-to-bitrix]');
        var officeField = form.querySelector('[data-deliver-office-field]');
        var bitrixField = form.querySelector('[data-deliver-bitrix-field]');
        var errorEl = form.querySelector('[data-deliver-error]');

        function syncChannelUi() {
            var toCrm = !!(crmToggle && crmToggle.checked);
            var toBitrix = !!(bitrixToggle && bitrixToggle.checked);

            if (officeField) officeField.hidden = !toCrm;
            if (bitrixField) bitrixField.hidden = !toBitrix;

            if (errorEl) {
                errorEl.hidden = true;
                errorEl.textContent = '';
            }

            // Disable options of hidden channels so they are not posted.
            form.querySelectorAll('[data-deliver-office-option]').forEach(function (el) {
                el.disabled = !toCrm;
            });
            form.querySelectorAll('[data-deliver-bitrix-option]').forEach(function (el) {
                el.disabled = !toBitrix;
            });

            var submitBtn = form.querySelector('[data-deliver-submit]');
            if (submitBtn) submitBtn.disabled = !toCrm && !toBitrix;
        }

        if (crmToggle) crmToggle.addEventListener('change', syncChannelUi);
        if (bitrixToggle) bitrixToggle.addEventListener('change', syncChannelUi);

        form.addEventListener('submit', function (e) {
            var toCrm = !!(crmToggle && crmToggle.checked);
            var toBitrix = !!(bitrixToggle && bitrixToggle.checked);
            if (!toCrm && !toBitrix) {
                e.preventDefault();
                if (errorEl) {
                    errorEl.hidden = false;
                    errorEl.textContent = 'Выберите хотя бы один канал.';
                }
                return;
            }
            if (toCrm && getCheckedValues(form, '[data-deliver-office-option]').length === 0) {
                e.preventDefault();
                if (errorEl) {
                    errorEl.hidden = false;
                    errorEl.textContent = 'Выберите хотя бы один офис для CRM.';
                }
                return;
            }

            saveDeliverPrefs(readDeliverPrefsFromForm(form));

            var submitBtn = form.querySelector('[data-deliver-submit]');
            if (submitBtn) {
                submitBtn.disabled = true;
                submitBtn.classList.add('is-loading');
            }
        });

        modal._orbitaSyncChannels = syncChannelUi;
    }

    function fillFilterFields(form) {
        var params = new URLSearchParams(window.location.search);
        var fields = [
            ['from', 'From'],
            ['to', 'To'],
            ['status', 'Status'],
            ['workerId', 'WorkerId'],
            ['accountId', 'AccountId'],
            ['bitrixDestination', 'BitrixDestination'],
            ['vacancy', 'Vacancy'],
            ['search', 'Search'],
            ['page', 'Page'],
            ['sort', 'Sort'],
            ['dir', 'Dir']
        ];
        fields.forEach(function (pair) {
            var input = form.querySelector('[name="' + pair[1] + '"]');
            if (input) input.value = params.get(pair[0]) || '';
        });
    }

    function officeAcceptsCrm(item) {
        // Explicit false only — missing flag treated as off (safer for CRM picker).
        return item.crmEnabled === true || item.CrmEnabled === true;
    }

    function populateSendModalOptions(form, shared, allOffices, instances) {
        var officeList = form.querySelector('[data-deliver-office-list]');
        var bitrixList = form.querySelector('[data-deliver-bitrix-list]');
        var hint = form.querySelector('[data-deliver-office-hint]');

        function fillOffices(preferredIds) {
            if (!officeList) return;
            var list = allOffices.filter(officeAcceptsCrm);
            preferredIds = preferredIds || getCheckedValues(form, '[data-deliver-office-option]');

            if (!list.length) {
                officeList.innerHTML = '<p class="responses-send-empty">Нет офисов с включённой CRM</p>';
            } else {
                officeList.innerHTML = list.map(function (item) {
                    var label = item.name || item.Name || item.label || item.Label || 'Офис';
                    var id = item.id || item.Id || item.value || item.Value;
                    return '<label class="responses-send-check">' +
                        '<input type="checkbox" name="OfficeIds" value="' + shared.escapeHtml(id) +
                        '" data-deliver-office-option />' +
                        '<span>' + shared.escapeHtml(label) + '</span></label>';
                }).join('');

                if (preferredIds.length) {
                    setCheckedByValues(form, '[data-deliver-office-option]', preferredIds);
                } else if (list.length === 1) {
                    // Single office — auto-check for convenience.
                    var only = officeList.querySelector('[data-deliver-office-option]');
                    if (only) only.checked = true;
                }
            }

            if (hint) {
                if (!list.length) {
                    hint.textContent = 'Нет офисов с включённой CRM.';
                    hint.classList.add('is-warning');
                } else {
                    hint.textContent = '';
                    hint.classList.remove('is-warning');
                }
            }
        }

        function fillBitrix(preferredIds) {
            if (!bitrixList) return;
            preferredIds = preferredIds || getCheckedValues(form, '[data-deliver-bitrix-option]');

            if (!instances.length) {
                bitrixList.innerHTML = '<p class="responses-send-empty">Нет подключённых порталов — сработает схема офиса</p>';
            } else {
                bitrixList.innerHTML = instances.map(function (item) {
                    var label = item.label || item.Label || 'Битрикс';
                    var id = item.id || item.Id;
                    var host = item.portalHost || item.PortalHost;
                    var text = label + (host ? ' · ' + host : '');
                    return '<label class="responses-send-check">' +
                        '<input type="checkbox" name="BitrixInstanceIds" value="' + shared.escapeHtml(id) +
                        '" data-deliver-bitrix-option />' +
                        '<span>' + shared.escapeHtml(text) + '</span></label>';
                }).join('');
                setCheckedByValues(form, '[data-deliver-bitrix-option]', preferredIds);
            }
        }

        fillOffices();
        fillBitrix();

        return {
            fillOffices: fillOffices,
            fillBitrix: fillBitrix,
            crmOfficeCount: allOffices.filter(officeAcceptsCrm).length
        };
    }

    function openSendBitrixModal(responseId) {
        if (!responseId) return;
        var shared = getShared();
        if (!shared) {
            toast('Страница ещё загружается. Попробуйте снова.', 'error');
            return;
        }

        var sendUrl = getDeliverUrl() || getSendBitrixUrl();
        if (!sendUrl) {
            toast('Отправка недоступна. Обновите страницу.', 'error');
            return;
        }

        closeRowMenus();

        var modal = ensureSendModal();
        var form = modal.querySelector('[data-send-bitrix-form]');
        if (!form) return;

        var token = document.querySelector('input[name="__RequestVerificationToken"]');
        var tokenInput = form.querySelector('input[name="__RequestVerificationToken"]');
        if (token && tokenInput) tokenInput.value = token.value;
        form.action = sendUrl;
        form.querySelector('[data-send-bitrix-response-id]').value = responseId;
        fillFilterFields(form);

        // Restore last used channels / multi office / multi bitrix.
        var prefs = loadDeliverPrefs();
        var prefIds = normalizePrefIdList(prefs);
        applyDeliverPrefsToForm(form, prefs);

        var errorEl = form.querySelector('[data-deliver-error]');
        if (errorEl) {
            errorEl.hidden = true;
            errorEl.textContent = '';
        }
        var submitBtn = form.querySelector('[data-deliver-submit]');
        if (submitBtn) {
            submitBtn.disabled = false;
            submitBtn.classList.remove('is-loading');
            submitBtn.textContent = 'Отправить';
        }

        // Show dialog immediately, then fill with fresh options.
        if (!showDialog(modal)) {
            toast('Не удалось открыть окно отправки. Обновите страницу.', 'error');
            return;
        }

        if (typeof modal._orbitaSyncChannels === 'function') {
            modal._orbitaSyncChannels();
        }

        form.classList.add('is-loading-options');
        fetchDeliverOptions().then(function (opts) {
            form.classList.remove('is-loading-options');
            var allOffices = opts.offices || [];
            var instances = opts.instances || [];

            var populated = populateSendModalOptions(form, shared, allOffices, instances);
            populated.fillOffices(prefIds.officeIds);
            populated.fillBitrix(prefIds.bitrixInstanceIds);

            var toCrm = form.querySelector('[data-deliver-to-crm]');
            if (toCrm && toCrm.checked && populated.crmOfficeCount === 0) {
                toast('Нет офисов с CRM. Включите CRM в настройках офиса или снимите канал CRM.', 'error');
            }

            if (typeof modal._orbitaSyncChannels === 'function') {
                modal._orbitaSyncChannels();
            }
        });
    }

    function initSendBitrixUi() {
        if (document.documentElement.dataset.orbitaResponsesSendBound === '1') return;
        document.documentElement.dataset.orbitaResponsesSendBound = '1';

        document.addEventListener('click', function (e) {
            var btn = e.target.closest('[data-send-bitrix]');
            if (!btn) return;
            e.preventDefault();
            e.stopPropagation();
            e.stopImmediatePropagation();
            var responseId = btn.getAttribute('data-response-id');
            if (responseId) openSendBitrixModal(responseId);
        }, true);

        document.addEventListener('click', function (e) {
            var cancel = e.target.closest('[data-send-bitrix-cancel]');
            if (!cancel) return;
            var modal = document.getElementById('responsesSendBitrixDialog');
            if (modal) modal.close();
        });
    }

    function ensureBulkSendModal() {
        var modal = document.getElementById('responsesBulkSendBitrixDialog');
        if (modal && modal.dataset.uiVersion === '3') return modal;
        if (modal) modal.remove();

        modal = document.createElement('dialog');
        modal.id = 'responsesBulkSendBitrixDialog';
        modal.className = 'settings-dialog responses-send-dialog';
        modal.dataset.uiVersion = '3';
        modal.innerHTML =
            '<form class="settings-dialog-form responses-send-form" data-bulk-deliver-form>' +
            '<h2 class="settings-dialog-title">Массовая отправка</h2>' +
            '<p class="settings-dialog-subtitle" data-bulk-deliver-subtitle>Выберите каналы для выбранных откликов</p>' +
            '<div class="responses-send-channels" role="group" aria-label="Каналы">' +
            '<label class="responses-send-check">' +
            '<input type="checkbox" name="ToCrm" value="true" checked data-deliver-to-crm />' +
            '<span>CRM офиса</span>' +
            '</label>' +
            '<label class="responses-send-check">' +
            '<input type="checkbox" name="ToBitrix" value="true" data-deliver-to-bitrix />' +
            '<span>Bitrix24</span>' +
            '</label>' +
            '</div>' +
            '<div class="responses-send-fields">' +
            '<div class="responses-send-multiselect" data-deliver-office-field hidden>' +
            '<span class="settings-field-label">Офисы CRM <em>(можно несколько)</em></span>' +
            '<div class="responses-send-check-list" data-deliver-office-list></div>' +
            '<span class="responses-send-hint" data-deliver-office-hint></span>' +
            '</div>' +
            '<div class="responses-send-multiselect" data-deliver-bitrix-field hidden>' +
            '<span class="settings-field-label">Порталы Bitrix24 <em>(можно несколько; пусто = схема офиса)</em></span>' +
            '<div class="responses-send-check-list" data-deliver-bitrix-list></div>' +
            '</div>' +
            '</div>' +
            '<p class="responses-send-error" data-deliver-error hidden role="alert"></p>' +
            '<div class="settings-dialog-actions">' +
            '<button type="button" class="settings-secondary-btn" data-bulk-send-bitrix-cancel>Отмена</button>' +
            '<button type="submit" class="settings-primary-btn" data-bulk-deliver-submit>Отправить</button>' +
            '</div></form>';
        document.body.appendChild(modal);

        var form = modal.querySelector('[data-bulk-deliver-form]');
        var crmToggle = form.querySelector('[data-deliver-to-crm]');
        var bitrixToggle = form.querySelector('[data-deliver-to-bitrix]');
        var officeField = form.querySelector('[data-deliver-office-field]');
        var bitrixField = form.querySelector('[data-deliver-bitrix-field]');
        var errorEl = form.querySelector('[data-deliver-error]');

        function syncBulkChannels() {
            var toCrm = !!(crmToggle && crmToggle.checked);
            var toBitrix = !!(bitrixToggle && bitrixToggle.checked);
            if (officeField) officeField.hidden = !toCrm;
            if (bitrixField) bitrixField.hidden = !toBitrix;
            form.querySelectorAll('[data-deliver-office-option]').forEach(function (el) {
                el.disabled = !toCrm;
            });
            form.querySelectorAll('[data-deliver-bitrix-option]').forEach(function (el) {
                el.disabled = !toBitrix;
            });
            if (errorEl) {
                errorEl.hidden = true;
                errorEl.textContent = '';
            }
            var submitBtn = form.querySelector('[data-bulk-deliver-submit]');
            if (submitBtn) submitBtn.disabled = !toCrm && !toBitrix;
        }

        if (crmToggle) crmToggle.addEventListener('change', syncBulkChannels);
        if (bitrixToggle) bitrixToggle.addEventListener('change', syncBulkChannels);
        modal._orbitaSyncChannels = syncBulkChannels;
        return modal;
    }

    function openBulkSendModal() {
        var count = selectedIds.size;
        if (!count) return;
        if (count > BULK_SEND_MAX) {
            toast('За один раз можно отправить не более ' + BULK_SEND_MAX + ' откликов.', 'error');
            return;
        }

        var shared = getShared();
        if (!shared) {
            toast('Страница ещё загружается. Попробуйте снова.', 'error');
            return;
        }

        var sendUrl = getBulkDeliverUrl() || getBulkSendUrl();
        if (!sendUrl) {
            toast('Отправка недоступна. Обновите страницу.', 'error');
            return;
        }

        var modal = ensureBulkSendModal();
        var form = modal.querySelector('[data-bulk-deliver-form]');
        if (!form) return;

        var subtitle = modal.querySelector('[data-bulk-deliver-subtitle]');
        if (subtitle) {
            subtitle.textContent = 'Отправить ' + count + ' ' + pluralizeResponses(count) +
                '. CRM / Bitrix / несколько офисов и порталов.';
        }

        var prefs = loadDeliverPrefs();
        var prefIds = normalizePrefIdList(prefs);
        applyDeliverPrefsToForm(form, prefs);

        var errorEl = form.querySelector('[data-deliver-error]');
        if (errorEl) {
            errorEl.hidden = true;
            errorEl.textContent = '';
        }
        var submitBtn = form.querySelector('[data-bulk-deliver-submit]');
        if (submitBtn) {
            submitBtn.disabled = false;
            submitBtn.classList.remove('is-loading');
        }

        if (!showDialog(modal)) {
            toast('Не удалось открыть окно отправки. Обновите страницу.', 'error');
            return;
        }

        if (typeof modal._orbitaSyncChannels === 'function') {
            modal._orbitaSyncChannels();
        }

        form.classList.add('is-loading-options');
        fetchDeliverOptions().then(function (opts) {
            form.classList.remove('is-loading-options');
            var populated = populateSendModalOptions(form, shared, opts.offices || [], opts.instances || []);
            populated.fillOffices(prefIds.officeIds);
            populated.fillBitrix(prefIds.bitrixInstanceIds);
            if (typeof modal._orbitaSyncChannels === 'function') {
                modal._orbitaSyncChannels();
            }
        });
    }

    function pluralizeResponses(count) {
        var mod10 = count % 10;
        var mod100 = count % 100;
        if (mod10 === 1 && mod100 !== 11) return 'отклик';
        if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return 'отклика';
        return 'откликов';
    }

    function submitBulkDeliver(form) {
        var url = getBulkDeliverUrl();
        var ids = getSelectedIds();
        if (!url || !ids.length) {
            return Promise.resolve({ ok: false, payload: { error: 'Отправка недоступна.' } });
        }

        var crm = form.querySelector('[data-deliver-to-crm]');
        var bitrix = form.querySelector('[data-deliver-to-bitrix]');
        var toCrm = !!(crm && crm.checked);
        var toBitrix = !!(bitrix && bitrix.checked);
        var officeIds = toCrm ? getCheckedValues(form, '[data-deliver-office-option]') : [];
        var bitrixIds = toBitrix ? getCheckedValues(form, '[data-deliver-bitrix-option]') : [];

        var shared = getShared();
        var formData = new FormData();
        var token = shared ? shared.getRequestVerificationToken() : '';
        if (token) formData.append('__RequestVerificationToken', token);
        ids.forEach(function (id) { formData.append('ResponseIds', id); });
        // Always send both flags — property defaults on the model are not reliable for unchecked boxes.
        formData.append('ToCrm', toCrm ? 'true' : 'false');
        formData.append('ToBitrix', toBitrix ? 'true' : 'false');
        officeIds.forEach(function (id) { formData.append('OfficeIds', id); });
        bitrixIds.forEach(function (id) { formData.append('BitrixInstanceIds', id); });

        return fetch(url, {
            method: 'POST',
            credentials: 'same-origin',
            body: formData,
            headers: { Accept: 'application/json' }
        }).then(function (res) {
            return res.json().then(function (payload) {
                return { ok: res.ok, payload: payload };
            }).catch(function () {
                return { ok: res.ok, payload: null };
            });
        });
    }

    function copyBulkCards() {
        var cards = getSelectedCards();
        if (!cards.length) {
            toast('Нет карточек для копирования среди выбранных откликов.', 'error');
            return;
        }
        var text = cards.join('\n\n---\n\n');
        if (window.Orbita && typeof window.Orbita.copyText === 'function') {
            window.Orbita.copyText(text, 'Скопировано карточек: ' + cards.length);
        }
    }

    function initBulkSelection() {
        if (document.documentElement.dataset.orbitaResponsesBulkBound === '1') return;
        document.documentElement.dataset.orbitaResponsesBulkBound = '1';

        document.addEventListener('change', function (e) {
            var selectAll = e.target.closest('[data-responses-select-all]');
            if (selectAll) {
                toggleSelectAll(selectAll.checked);
                return;
            }
            var cb = e.target.closest('[data-response-select]');
            if (!cb) return;
            e.stopPropagation();
            var row = cb.closest('.responses-row');
            setSelection(cb.value, cb.checked, row);
        });

        document.addEventListener('click', function (e) {
            if (e.target.closest('[data-response-select], .responses-select-col, .responses-select-checkbox')) {
                e.stopPropagation();
            }

            var bulkSend = e.target.closest('[data-responses-bulk-send]');
            if (bulkSend) {
                e.preventDefault();
                openBulkSendModal();
                return;
            }

            var bulkCopy = e.target.closest('[data-responses-bulk-copy-cards]');
            if (bulkCopy) {
                e.preventDefault();
                copyBulkCards();
                return;
            }

            var bulkClear = e.target.closest('[data-responses-bulk-clear]');
            if (bulkClear) {
                e.preventDefault();
                clearSelection();
                return;
            }

            var bulkCancel = e.target.closest('[data-bulk-send-bitrix-cancel]');
            if (bulkCancel) {
                var modal = document.getElementById('responsesBulkSendBitrixDialog');
                if (modal) modal.close();
            }
        });

        document.addEventListener('submit', function (e) {
            var form = e.target.closest('[data-bulk-deliver-form]');
            if (!form) return;
            e.preventDefault();

            var crm = form.querySelector('[data-deliver-to-crm]');
            var bitrix = form.querySelector('[data-deliver-to-bitrix]');
            var errorEl = form.querySelector('[data-deliver-error]');
            var toCrm = !!(crm && crm.checked);
            var toBitrix = !!(bitrix && bitrix.checked);

            if (!toCrm && !toBitrix) {
                if (errorEl) {
                    errorEl.hidden = false;
                    errorEl.textContent = 'Выберите хотя бы один канал.';
                }
                return;
            }
            if (toCrm && getCheckedValues(form, '[data-deliver-office-option]').length === 0) {
                if (errorEl) {
                    errorEl.hidden = false;
                    errorEl.textContent = 'Выберите хотя бы один офис для CRM.';
                }
                return;
            }

            saveDeliverPrefs(readDeliverPrefsFromForm(form));

            var submitBtn = form.querySelector('[data-bulk-deliver-submit]');
            if (submitBtn) {
                submitBtn.disabled = true;
                submitBtn.classList.add('is-loading');
            }

            var selectedCount = selectedIds.size;
            submitBulkDeliver(form).then(function (result) {
                if (submitBtn) {
                    submitBtn.disabled = false;
                    submitBtn.classList.remove('is-loading');
                }
                var modal = document.getElementById('responsesBulkSendBitrixDialog');
                if (modal) modal.close();

                if (!result.ok) {
                    var err = (result.payload && (result.payload.error || result.payload.message)) ||
                        'Не удалось выполнить массовую отправку.';
                    toast(err, 'error');
                    return;
                }

                var data = result.payload || {};
                var succeeded = data.succeeded != null ? data.succeeded : data.Succeeded;
                var failed = data.failed != null ? data.failed : data.Failed;
                var total = data.total != null ? data.total : data.Total;
                var msg = 'Отправлено: ' + (succeeded || 0) + ' из ' + (total || selectedCount);
                if (failed > 0) msg += ', ошибок: ' + failed;
                toast(msg, failed > 0 ? 'info' : 'success');

                // Always clear multi-select after a bulk send attempt that the server accepted.
                clearSelection();
                fetchSnapshot();
            }).catch(function () {
                if (submitBtn) {
                    submitBtn.disabled = false;
                    submitBtn.classList.remove('is-loading');
                }
                toast('Не удалось выполнить массовую отправку.', 'error');
            });
        });
    }

    function initRowNavigation() {
        document.querySelectorAll('.responses-row').forEach(function (row) {
            if (row.hasAttribute('data-responses-row-bound')) return;
            row.setAttribute('data-responses-row-bound', '1');
            row.addEventListener('click', function (e) {
                if (e.target.closest('[data-row-menu], a, button, .responses-select-col, [data-response-select]')) return;
                var id = row.getAttribute('data-response-id');
                if (id) openResponseDetail(id);
            });
        });
        document.querySelectorAll('[data-orbita-response-open]').forEach(function (btn) {
            if (btn.hasAttribute('data-responses-open-bound')) return;
            btn.setAttribute('data-responses-open-bound', '1');
            btn.addEventListener('click', function (e) {
                e.stopPropagation();
                var id = btn.getAttribute('data-response-id');
                if (id) openResponseDetail(id);
            });
        });
    }

    function renderResponseMenu(row, accountUrl, workerUrl) {
        var shared = getShared();
        if (!shared) return '';
        var rowId = readRowValue(row, 'id');
        var vacancyUrl = readRowValue(row, 'vacancyUrl');
        var bitrixEntityUrl = readRowValue(row, 'bitrixEntityUrl');
        var items = '<button type="button" class="row-menu-item" data-orbita-response-open data-response-id="' + shared.escapeHtml(rowId) + '"><i class="fa-regular fa-eye" aria-hidden="true"></i>Просмотреть отклик</button>';

        if (vacancyUrl) {
            items += '<a class="row-menu-item" href="' + shared.escapeHtml(vacancyUrl) + '" target="_blank" rel="noopener"><i class="fa-regular fa-rectangle-list" aria-hidden="true"></i>Открыть объявление</a>';
        }
        items += '<a class="row-menu-item" href="' + shared.escapeHtml(accountUrl) + '"><i class="fa-regular fa-user" aria-hidden="true"></i>Перейти к аккаунту</a>' +
            '<a class="row-menu-item" href="' + shared.escapeHtml(workerUrl) + '"><i class="fa-solid fa-server" aria-hidden="true"></i>Перейти к воркеру</a>';
        items += '<button type="button" class="row-menu-item" data-copy-response-card><i class="fa-regular fa-copy" aria-hidden="true"></i>Копировать карточку</button>';
        if (readRowBool(row, 'canSend')) {
            items += '<button type="button" class="row-menu-item" data-send-bitrix data-response-id="' + shared.escapeHtml(rowId) + '"><i class="fa-solid fa-paper-plane" aria-hidden="true"></i>Отправить…</button>';
        }
        var deliveries = readDeliveries(row);
        deliveries.forEach(function (delivery) {
            var url = readRowValue(delivery, 'bitrixEntityUrl');
            var outcome = readRowValue(delivery, 'outcome');
            var label = readRowValue(delivery, 'bitrixLabel') || 'Битрикс';
            if (outcome === 'Sent' && url) {
                items += '<a class="row-menu-item" href="' + shared.escapeHtml(url) + '" target="_blank" rel="noopener"><i class="fa-solid fa-arrow-up-right-from-square" aria-hidden="true"></i>Bitrix24 · ' + shared.escapeHtml(label) + '</a>';
            }
        });
        if (!deliveries.length && bitrixEntityUrl) {
            items += '<a class="row-menu-item" href="' + shared.escapeHtml(bitrixEntityUrl) + '" target="_blank" rel="noopener"><i class="fa-solid fa-arrow-up-right-from-square" aria-hidden="true"></i>Открыть карточку Bitrix24</a>';
        }
        return shared.rowMenuShell('row-menu-dropdown--responses', items);
    }

    function mapDeliveryOutcomeLabel(outcome) {
        switch (outcome) {
            case 'Sent': return 'отправлен';
            case 'Duplicate': return 'дубль';
            case 'Error': return 'ошибка';
            case 'Unavailable': return 'недоступен';
            default: return outcome || '';
        }
    }

    function mapDeliveryChipTone(outcome) {
        switch (outcome) {
            case 'Sent': return 'sent';
            case 'Duplicate': return 'duplicate';
            case 'Error': return 'error';
            case 'Unavailable': return 'unavailable';
            default: return 'muted';
        }
    }

    function readBitrixDeliveries(row) {
        var deliveries = readRowValue(row, 'bitrixDeliveries');
        return Array.isArray(deliveries) ? deliveries : [];
    }

    function readCrmDeliveries(row) {
        var deliveries = readRowValue(row, 'crmDeliveries');
        return Array.isArray(deliveries) ? deliveries : [];
    }

    function renderBitrixDeliveries(deliveries) {
        var shared = getShared();
        if (!shared || !deliveries.length) return '';
        return '<div class="responses-bitrix-deliveries">' + deliveries.map(function (delivery) {
            var label = readRowValue(delivery, 'bitrixLabel') || 'Битрикс';
            var outcome = readRowValue(delivery, 'outcome') || '';
            var outcomeLabel = readRowValue(delivery, 'outcomeLabel') || mapDeliveryOutcomeLabel(outcome);
            var tone = readRowValue(delivery, 'chipTone') || mapDeliveryChipTone(outcome);
            var url = readRowValue(delivery, 'bitrixEntityUrl');
            var errorMessage = readRowValue(delivery, 'errorMessage') || outcomeLabel;
            var inner = '<span class="responses-delivery-chip-channel">Битрикс</span>' +
                '<span class="responses-bitrix-chip-label">' + shared.escapeHtml(label) + '</span>' +
                '<span class="responses-bitrix-chip-outcome">' + shared.escapeHtml(outcomeLabel) + '</span>';
            if (url) {
                return '<a class="responses-bitrix-chip responses-bitrix-chip--' + shared.escapeHtml(tone) + '" href="' +
                    shared.escapeHtml(url) + '" target="_blank" rel="noopener" title="' + shared.escapeHtml(errorMessage) + '">' +
                    inner + '</a>';
            }
            return '<span class="responses-bitrix-chip responses-bitrix-chip--' + shared.escapeHtml(tone) + '" title="' +
                shared.escapeHtml(errorMessage) + '">' + inner + '</span>';
        }).join('') + '</div>';
    }

    function renderBitrixCell(row) {
        var shared = getShared();
        if (!shared) return '<span class="responses-bitrix-empty">—</span>';
        var deliveries = readBitrixDeliveries(row);
        if (deliveries.length) {
            return renderBitrixDeliveries(deliveries);
        }
        var bitrixLabel = readRowValue(row, 'bitrixLabel');
        if (bitrixLabel) {
            return '<span class="responses-bitrix-label">Битрикс · ' + shared.escapeHtml(bitrixLabel) + '</span>';
        }
        return '<span class="responses-bitrix-empty">—</span>';
    }

    function renderCrmDeliveries(deliveries) {
        var shared = getShared();
        if (!shared || !deliveries.length) return '';
        return '<div class="responses-crm-deliveries">' + deliveries.map(function (delivery) {
            var officeName = readRowValue(delivery, 'officeName') || 'Офис';
            var outcome = readRowValue(delivery, 'outcome') || '';
            var outcomeLabel = readRowValue(delivery, 'outcomeLabel') || mapDeliveryOutcomeLabel(outcome);
            var tone = readRowValue(delivery, 'chipTone') || mapDeliveryChipTone(outcome);
            var title = readRowValue(delivery, 'errorMessage') || outcomeLabel;
            return '<span class="responses-crm-chip responses-crm-chip--' + shared.escapeHtml(tone) + '" title="' +
                shared.escapeAttr(title) + '"><span class="responses-delivery-chip-channel">CRM</span><span class="responses-crm-chip-label">' +
                shared.escapeHtml(officeName) + '</span><span class="responses-crm-chip-outcome">' +
                shared.escapeHtml(outcomeLabel) + '</span></span>';
        }).join('') + '</div>';
    }

    function renderStatusDestinations(row) {
        var crmHtml = renderCrmDeliveries(readCrmDeliveries(row));
        var bitrixHtml = renderBitrixCell(row);
        return crmHtml + (bitrixHtml.indexOf('responses-bitrix-empty') === -1 ? bitrixHtml : (crmHtml ? '' : bitrixHtml));
    }

    function renderResponses(rows) {
        var shared = getShared();
        var tbody = document.querySelector('[data-orbita-live-body="responses"]');
        if (!tbody || !shared) return;

        tbody.innerHTML = (rows || []).map(function (row) {
            var rowId = readRowValue(row, 'id');
            var workerUrl = workerDetailsUrl(readRowValue(row, 'workerId'));
            var accountUrl = accountSearchUrl(readRowValue(row, 'accountName'));
            var vacancyUrl = readRowValue(row, 'vacancyUrl');
            var adId = shared.displayAdId(readRowValue(row, 'sourceResponseId'), vacancyUrl);
            var author = shared.displayAuthor(readRowValue(row, 'fullName'));
            var vacancy = readRowValue(row, 'vacancy') || '';
            var adHtml = vacancyUrl
                ? '<a class="responses-ad-link" href="' + shared.escapeHtml(vacancyUrl) + '" target="_blank" rel="noopener">' + shared.escapeHtml(vacancy) + '</a>'
                : '<span class="responses-ad-link">' + shared.escapeHtml(vacancy) + '</span>';
            adHtml += '<span class="responses-ad-tags"><span>' + shared.escapeHtml(readRowValue(row, 'source') || 'Avito') +
                '</span><span>ID: ' + shared.escapeHtml(adId) + '</span></span>';

            var phoneDisplay = shared.formatPhone(readRowValue(row, 'phoneRaw'), readRowValue(row, 'phoneNormalized'));
            var phoneHidden = readRowBool(row, 'isPhoneHidden');
            var phoneCell = '<div class="responses-phone__number"><i class="fa-solid fa-phone" aria-hidden="true"></i>' +
                shared.escapeHtml(phoneHidden ? 'Скрыт' : phoneDisplay) + '</div>';
            var phoneMetricLabel = readRowValue(row, 'phoneMetricLabel') || '';
            var phoneMetricKind = readRowValue(row, 'phoneMetricKind') || '';
            if (phoneMetricLabel) {
                var phoneMetricTone = phoneMetricKind === 'PhoneChanged'
                    ? 'responses-phone-metric--changed'
                    : 'responses-phone-metric--unchanged';
                var previousPhoneRaw = readRowValue(row, 'previousPhoneRaw');
                var previousPhoneNormalized = readRowValue(row, 'previousPhoneNormalized');
                var phoneMetricText = phoneMetricKind === 'PhoneChanged' && previousPhoneNormalized
                    ? 'Был: ' + shared.formatPhone(previousPhoneRaw, previousPhoneNormalized)
                    : phoneMetricLabel;
                phoneCell += '<span class="responses-phone-metric ' + phoneMetricTone + '" title="' +
                    shared.escapeAttr(phoneMetricLabel) + '">' + shared.escapeHtml(phoneMetricText) + '</span>';
            }
            var city = readRowValue(row, 'city');
            var cityDisplay = city && String(city).trim() ? shared.escapeHtml(city) : '—';
            var age = readRowValue(row, 'age');
            var ageDisplay = age > 0 ? String(age) : '—';
            var highlightLabels = readHighlightLabels(row);
            var isHighlighted = readRowBool(row, 'isHighlighted') || highlightLabels.length > 0;
            var highlightLabel = highlightLabels[0] || '';
            var highlightsHtml = highlightLabels.length
                ? '<div class="responses-candidate__highlights" aria-label="Причины выделения">' + highlightLabels.map(function (label) {
                    return '<span class="responses-candidate__highlight">' + shared.escapeHtml(label) + '</span>';
                }).join('') + '</div>'
                : '';
            var genderDisplay = formatGenderLabel(readRowValue(row, 'gender'));
            var canSend = readRowBool(row, 'canSend');
            var collectedAtUtc = readRowValue(row, 'collectedAtUtc') || readRowValue(row, 'createdAtUtc');
            var respondedAtUtc = readRowValue(row, 'createdAtUtc');
            var statusTone = readRowValue(row, 'statusTone') || 'unique';
            var statusLabel = readRowValue(row, 'statusLabel') || '';
            var statusShort = String(statusLabel).split('·')[0].trim();
            var candidateMeta = [age > 0 ? String(age) : '', genderDisplay !== '—' ? genderDisplay : ''].filter(Boolean).join(' · ');
            var avatarUrl = readRowValue(row, 'avatarUrl') || '';
            var candidateHtml = '<div class="responses-candidate__identity"><span class="responses-candidate__avatar" aria-hidden="true">' +
                shared.escapeHtml(candidateInitials(author)) +
                (avatarUrl ? '<img src="' + shared.escapeAttr(avatarUrl) + '" alt="" loading="lazy">' : '') +
                '</span><div class="responses-candidate__copy"><strong title="' +
                shared.escapeAttr(author) + '">' + shared.escapeHtml(author) + '</strong>' +
                (candidateMeta ? '<span>' + shared.escapeHtml(candidateMeta) + '</span>' : '') + highlightsHtml + '</div></div>';
            var accountName = readRowValue(row, 'accountName') || '';
            var accountSubProfile = readRowValue(row, 'avitoSubProfileName') || '';
            var workerName = readRowValue(row, 'workerName') || '';
            var accountHtml = '<a href="' + shared.escapeHtml(accountUrl) + '" title="' + shared.escapeAttr(accountName) + '">' +
                shared.escapeHtml(accountName) + '</a>' +
                (accountSubProfile ? '<span class="responses-account-sub" title="Субпрофиль Avito">' + shared.escapeHtml(accountSubProfile) + '</span>' : '') +
                (workerName ? '<span class="responses-account-worker">' + shared.escapeHtml(workerName) + '</span>' : '');
            var statusHtml = '<div class="responses-status__card"><span class="response-status-badge response-status-badge--' +
                shared.escapeHtml(statusTone) + '" title="' + shared.escapeAttr(statusLabel) + '"><span class="response-status-badge__label">' +
                shared.escapeHtml(statusShort) + '</span></span><div class="responses-status__destination">' + renderStatusDestinations(row) + '</div></div>';
            var lastResponseHtml = renderResponseTimingCell(respondedAtUtc, collectedAtUtc, shared);

            var cardCopy = readRowValue(row, 'cardCopy') || '';
            return '<tr class="responses-row' + (isHighlighted ? ' responses-row--highlighted' : '') + '" data-response-id="' + shared.escapeHtml(rowId) + '" data-phone="' + shared.escapeHtml(phoneDisplay) + '" data-can-send="' + (canSend ? 'true' : 'false') + '" data-phone-hidden="' + (phoneHidden ? 'true' : 'false') + '" data-highlighted="' + (isHighlighted ? 'true' : 'false') + '" data-highlight-label="' + shared.escapeAttr(highlightLabel) + '" data-response-card="' + shared.escapeAttr(cardCopy) + '" data-detail-json-url="' + shared.escapeHtml(detailJsonUrl(rowId)) + '">' +
                renderSelectCell(row) +
                '<td class="responses-candidate" data-label="Кандидат">' + candidateHtml + '</td>' +
                '<td class="responses-phone" data-label="Телефон">' + phoneCell + '</td>' +
                '<td class="responses-city" data-label="Город">' + cityDisplay + '</td>' +
                '<td class="responses-ad" data-label="Объявление">' + adHtml + '</td>' +
                '<td class="cell-link responses-account" data-label="Аккаунт">' + accountHtml + '</td>' +
                '<td class="responses-status" data-label="Статус">' + statusHtml + '</td>' +
                lastResponseHtml +
                '<td class="data-table-menu" data-label="">' + renderResponseMenu(row, accountUrl, workerUrl) + '</td></tr>';
        }).join('');

        if (window.OrbitaTime) window.OrbitaTime.localizeAll(tbody);
        localizeRelativeResponseTimes(tbody);
        closeRowMenus();
        shared.reinitLiveContent();
        initRowNavigation();
        syncRowCheckboxes();
        refreshSelectionMetaFromDom();
        updateBulkBar();
    }

    function refreshSelectionMetaFromDom() {
        getSelectedIds().forEach(function (id) {
            var row = document.querySelector('.responses-row[data-response-id="' + id.replace(/\\/g, '\\\\').replace(/"/g, '\\"') + '"]');
            if (row) selectionMeta[id] = captureRowMeta(row);
        });
        saveSelectionState();
    }

    function applySnapshot(snapshot, highlightChanged) {
        var shared = getShared();
        if (!snapshot || !shared) return;
        var prev = liveState ? shared.stableJson(liveState.responses) : null;
        var next = shared.stableJson(snapshot.responses || []);
        shared.updateKpiCards(snapshot.kpiCards || [], highlightChanged);
        shared.updatePaginationInfo(snapshot.pagination);
        if (prev !== next) {
            renderResponses(snapshot.responses || []);
            if (highlightChanged) shared.highlightCard(document.querySelector('.card--responses-table'));
        }
        var liveOpts = normalizeDeliverOptionsPayload(snapshot);
        if (liveOpts.offices.length || liveOpts.instances.length) {
            writeDeliverOptionsToDom(
                liveOpts.offices.length ? liveOpts.offices : null,
                liveOpts.instances.length ? liveOpts.instances : null);
        }
        liveState = snapshot;
        shared.updateUpdatedClock(snapshot.updatedAtUtc);
    }

    function fetchSnapshot() {
        var shared = getShared();
        var root = shared && shared.getLiveRoot();
        if (!root) return Promise.resolve();
        var url = root.getAttribute('data-orbita-snapshot');
        if (!url) return Promise.resolve();
        return fetch(url, { credentials: 'same-origin', headers: { Accept: 'application/json' } })
            .then(function (res) {
                if (!res.ok) throw new Error('Responses snapshot failed: ' + res.status);
                return res.json();
            })
            .then(function (snapshot) { applySnapshot(snapshot, true); });
    }

    function initResponsesPage() {
        initKpiCounters();
        initRowNavigation();
        initSendBitrixUi();
        initBulkSelection();
        localizeRelativeResponseTimes();
        syncRowCheckboxes();
        updateBulkBar();
        var params = new URLSearchParams(window.location.search);
        var selectedId = params.get('id');
        if (selectedId) openResponseDetail(selectedId);
        var shared = getShared();
        if (window.OrbitaLive && shared && shared.getLiveRoot()) {
            window.OrbitaLive.register('responses', { fetchSnapshot: fetchSnapshot });
        }
    }

    window.OrbitaResponses = window.OrbitaResponses || {};
    window.OrbitaResponses.openSendBitrixModal = openSendBitrixModal;
    window.OrbitaResponses.getRowCardCopy = getRowCardCopy;

    initResponsesPage();
    document.addEventListener('orbita:content-updated', initResponsesPage);
})();
