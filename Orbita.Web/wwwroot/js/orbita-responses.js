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

    /** Matches CandidateGenders.FormatLabel on the server. */
    function formatGenderLabel(gender) {
        if (!gender) return '—';
        var g = String(gender).trim().toLowerCase();
        if (g === 'male') return 'Мужчина';
        if (g === 'female') return 'Женщина';
        return '—';
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

    function getBitrixInstances() {
        var shared = getShared();
        var root = shared && shared.getLiveRoot();
        if (!root) return [];
        try {
            return JSON.parse(root.getAttribute('data-bitrix-instances-json') || '[]');
        } catch (e) {
            return [];
        }
    }

    function getSendBitrixUrl() {
        var shared = getShared();
        var root = shared && shared.getLiveRoot();
        return root ? root.getAttribute('data-send-bitrix-url') : null;
    }

    function getBulkSendUrl() {
        var shared = getShared();
        var root = shared && shared.getLiveRoot();
        return root ? root.getAttribute('data-bulk-send-bitrix-url') : null;
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
    var BULK_SEND_MAX = 200;

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
        if (modal) return modal;
        modal = document.createElement('dialog');
        modal.id = 'responsesSendBitrixDialog';
        modal.className = 'settings-dialog responses-send-bitrix-dialog';
        modal.innerHTML =
            '<form method="post" class="settings-dialog-form" data-send-bitrix-form>' +
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
            '<h2 class="settings-dialog-title">Отправить в Bitrix24</h2>' +
            '<p class="settings-dialog-subtitle">Выберите Битрикс для ручной отправки отклика</p>' +
            '<label class="settings-field">' +
            '<span class="settings-field-label">Битрикс</span>' +
            '<select name="BitrixInstanceId" class="settings-select" required data-send-bitrix-select></select>' +
            '</label>' +
            '<div class="settings-dialog-actions">' +
            '<button type="button" class="settings-secondary-btn" data-send-bitrix-cancel>Отмена</button>' +
            '<button type="submit" class="settings-primary-btn">Отправить</button>' +
            '</div></form>';
        document.body.appendChild(modal);
        return modal;
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

    function openSendBitrixModal(responseId) {
        if (!responseId) return;
        var shared = getShared();
        if (!shared) {
            toast('Страница ещё загружается. Попробуйте снова.', 'error');
            return;
        }

        var instances = getBitrixInstances();
        var sendUrl = getSendBitrixUrl();
        if (!sendUrl) {
            toast('Отправка недоступна. Обновите страницу.', 'error');
            return;
        }
        if (!instances.length) {
            toast('Нет доступных Битриксов. Настройте их в разделе «Битриксы».', 'error');
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

        var select = form.querySelector('[data-send-bitrix-select]');
        select.innerHTML = instances.map(function (item) {
            var label = item.label || item.Label || 'Битрикс';
            var id = item.id || item.Id;
            var host = item.portalHost || item.PortalHost;
            return '<option value="' + shared.escapeHtml(id) + '">' +
                shared.escapeHtml(label) + (host ? ' · ' + shared.escapeHtml(host) : '') +
                '</option>';
        }).join('');

        if (!showDialog(modal)) {
            toast('Не удалось открыть окно отправки. Обновите страницу.', 'error');
        }
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
        if (modal) return modal;
        modal = document.createElement('dialog');
        modal.id = 'responsesBulkSendBitrixDialog';
        modal.className = 'settings-dialog responses-send-bitrix-dialog';
        modal.innerHTML =
            '<form class="settings-dialog-form" data-bulk-send-bitrix-form>' +
            '<h2 class="settings-dialog-title">Массовая отправка в Bitrix24</h2>' +
            '<p class="settings-dialog-subtitle" data-bulk-send-bitrix-subtitle>Выберите Битрикс для отправки выбранных откликов</p>' +
            '<label class="settings-field">' +
            '<span class="settings-field-label">Битрикс</span>' +
            '<select name="BitrixInstanceId" class="settings-select" required data-bulk-send-bitrix-select></select>' +
            '</label>' +
            '<div class="settings-dialog-actions">' +
            '<button type="button" class="settings-secondary-btn" data-bulk-send-bitrix-cancel>Отмена</button>' +
            '<button type="submit" class="settings-primary-btn" data-bulk-send-bitrix-submit>Отправить</button>' +
            '</div></form>';
        document.body.appendChild(modal);
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

        var instances = getBitrixInstances();
        var sendUrl = getBulkSendUrl();
        if (!sendUrl) {
            toast('Отправка недоступна. Обновите страницу.', 'error');
            return;
        }
        if (!instances.length) {
            toast('Нет доступных Битриксов. Настройте их в разделе «Битриксы».', 'error');
            return;
        }

        var modal = ensureBulkSendModal();
        var subtitle = modal.querySelector('[data-bulk-send-bitrix-subtitle]');
        if (subtitle) {
            subtitle.textContent = 'Отправить ' + count + ' ' + pluralizeResponses(count) + ' в выбранный Битрикс';
        }

        var select = modal.querySelector('[data-bulk-send-bitrix-select]');
        select.innerHTML = instances.map(function (item) {
            var label = item.label || item.Label || 'Битрикс';
            var id = item.id || item.Id;
            var host = item.portalHost || item.PortalHost;
            return '<option value="' + shared.escapeHtml(id) + '">' +
                shared.escapeHtml(label) + (host ? ' · ' + shared.escapeHtml(host) : '') +
                '</option>';
        }).join('');

        if (!showDialog(modal)) {
            toast('Не удалось открыть окно отправки. Обновите страницу.', 'error');
        }
    }

    function pluralizeResponses(count) {
        var mod10 = count % 10;
        var mod100 = count % 100;
        if (mod10 === 1 && mod100 !== 11) return 'отклик';
        if (mod10 >= 2 && mod10 <= 4 && (mod100 < 10 || mod100 >= 20)) return 'отклика';
        return 'откликов';
    }

    function submitBulkSend(bitrixInstanceId) {
        var url = getBulkSendUrl();
        var ids = getSelectedIds();
        if (!url || !ids.length || !bitrixInstanceId) return Promise.resolve();

        var shared = getShared();
        var formData = new FormData();
        var token = shared ? shared.getRequestVerificationToken() : '';
        if (token) formData.append('__RequestVerificationToken', token);
        formData.append('BitrixInstanceId', bitrixInstanceId);
        ids.forEach(function (id) { formData.append('ResponseIds', id); });

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
            var form = e.target.closest('[data-bulk-send-bitrix-form]');
            if (!form) return;
            e.preventDefault();

            var select = form.querySelector('[data-bulk-send-bitrix-select]');
            var bitrixId = select ? select.value : '';
            if (!bitrixId) return;

            var submitBtn = form.querySelector('[data-bulk-send-bitrix-submit]');
            if (submitBtn) submitBtn.disabled = true;

            submitBulkSend(bitrixId).then(function (result) {
                if (submitBtn) submitBtn.disabled = false;
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
                var msg = 'Отправлено: ' + (succeeded || 0) + ' из ' + (total || selectedIds.size);
                if (failed > 0) msg += ', ошибок: ' + failed;
                toast(msg, failed > 0 ? 'info' : 'success');
                fetchSnapshot();
            }).catch(function () {
                if (submitBtn) submitBtn.disabled = false;
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
            items += '<button type="button" class="row-menu-item" data-send-bitrix data-response-id="' + shared.escapeHtml(rowId) + '"><i class="fa-solid fa-paper-plane" aria-hidden="true"></i>Отправить в Bitrix24</button>';
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

    function readDeliveries(row) {
        var deliveries = readRowValue(row, 'bitrixDeliveries');
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
            var inner = '<span class="responses-bitrix-chip-label">' + shared.escapeHtml(label) + '</span>' +
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
        var deliveries = readDeliveries(row);
        if (deliveries.length) {
            return renderBitrixDeliveries(deliveries);
        }
        var bitrixLabel = readRowValue(row, 'bitrixLabel');
        if (bitrixLabel) {
            return '<span class="responses-bitrix-label">' + shared.escapeHtml(bitrixLabel) + '</span>';
        }
        return '<span class="responses-bitrix-empty">—</span>';
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
            adHtml += '<span class="responses-ad-id">ID: ' + shared.escapeHtml(adId) + '</span>';

            var phoneDisplay = shared.formatPhone(readRowValue(row, 'phoneRaw'), readRowValue(row, 'phoneNormalized'));
            var phoneHidden = readRowBool(row, 'isPhoneHidden');
            var phoneCell = phoneHidden
                ? '<span class="responses-phone-hidden">Скрыт</span>'
                : '<span>' + shared.escapeHtml(phoneDisplay) + '</span>';
            var city = readRowValue(row, 'city');
            var cityDisplay = city && String(city).trim() ? shared.escapeHtml(city) : '—';
            var age = readRowValue(row, 'age');
            var ageDisplay = age > 0 ? String(age) : '—';
            var isHighlighted = readRowBool(row, 'isHighlighted');
            var highlightLabel = readRowValue(row, 'highlightLabel') || '';
            var ageBadge = isHighlighted && highlightLabel
                ? '<span class="responses-age-highlight-badge">' + shared.escapeHtml(highlightLabel) + '</span>'
                : '';
            var genderDisplay = formatGenderLabel(readRowValue(row, 'gender'));
            var canSend = readRowBool(row, 'canSend');
            var collectedAtUtc = readRowValue(row, 'collectedAtUtc') || readRowValue(row, 'createdAtUtc');
            var respondedAtUtc = readRowValue(row, 'createdAtUtc');
            var statusTone = readRowValue(row, 'statusTone') || 'unique';
            var statusLabel = readRowValue(row, 'statusLabel') || '';

            var cardCopy = readRowValue(row, 'cardCopy') || '';
            return '<tr class="responses-row' + (isHighlighted ? ' responses-row--highlighted' : '') + '" data-response-id="' + shared.escapeHtml(rowId) + '" data-phone="' + shared.escapeHtml(phoneDisplay) + '" data-can-send="' + (canSend ? 'true' : 'false') + '" data-phone-hidden="' + (phoneHidden ? 'true' : 'false') + '" data-highlighted="' + (isHighlighted ? 'true' : 'false') + '" data-highlight-label="' + shared.escapeAttr(highlightLabel) + '" data-response-card="' + shared.escapeAttr(cardCopy) + '" data-detail-json-url="' + shared.escapeHtml(detailJsonUrl(rowId)) + '">' +
                renderSelectCell(row) +
                '<td class="responses-time" data-label="Сбор"><time data-orbita-utc="' + shared.escapeHtml(collectedAtUtc) + '" data-orbita-format="datetime"></time></td>' +
                '<td class="responses-time responses-time--responded" data-label="Отклик"><time data-orbita-utc="' + shared.escapeHtml(respondedAtUtc) + '" data-orbita-format="datetime"></time></td>' +
                '<td class="responses-author" data-label="Автор">' + shared.escapeHtml(author) + '</td>' +
                '<td class="responses-phone" data-label="Телефон">' + phoneCell + '</td>' +
                '<td class="responses-city" data-label="Город">' + cityDisplay + '</td>' +
                '<td class="responses-age" data-label="Возраст">' + ageDisplay + ageBadge + '</td>' +
                '<td class="responses-gender" data-label="Пол">' + shared.escapeHtml(genderDisplay) + '</td>' +
                '<td class="responses-ad" data-label="Объявление">' + adHtml + '</td>' +
                '<td class="cell-link responses-account" data-label="Аккаунт">' + shared.renderResponseAccountCell(readRowValue(row, 'accountName'), readRowValue(row, 'avitoSubProfileName'), accountUrl) + '</td>' +
                '<td data-label="Статус"><span class="response-status-badge response-status-badge--' + shared.escapeHtml(statusTone) + '">' + shared.escapeHtml(statusLabel) + '</span></td>' +
                '<td class="responses-bitrix" data-label="Битрикс">' + renderBitrixCell(row) + '</td>' +
                '<td class="data-table-menu" data-label="">' + renderResponseMenu(row, accountUrl, workerUrl) + '</td></tr>';
        }).join('');

        if (window.OrbitaTime) window.OrbitaTime.localizeAll(tbody);
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
