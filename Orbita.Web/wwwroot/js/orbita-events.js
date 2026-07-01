(function () {
    function initKpiCounters() {
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

        document.querySelectorAll('.events-kpi-row [data-kpi-count]').forEach(function (el, index) {
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

    function initFilterAutoSubmit() {
        var form = document.querySelector('.events-filters');
        if (!form) return;

        form.querySelectorAll('select').forEach(function (select) {
            select.addEventListener('change', function () {
                form.submit();
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

    function settingsLogsUrl(workerId) {
        return shared.urlFromTemplate(shared.getLiveAttr('data-settings-logs-url'), '__id__', workerId);
    }

    function renderEvents(rows) {
        var tbody = document.querySelector('[data-orbita-live-body="events"]');
        if (!tbody || !shared) return;

        tbody.innerHTML = (rows || []).map(function (evt) {
            var workerUrl = workerDetailsUrl(evt.workerId);
            var accountUrl = evt.accountName ? accountSearchUrl(evt.accountName) : '';
            var accountCell = accountUrl
                ? '<a href="' + shared.escapeHtml(accountUrl) + '">' + shared.escapeHtml(evt.accountName) + '</a>'
                : '<span class="events-muted">—</span>';
            var attachmentUrl = evt.attachmentId ? '/Diagnostics/Image/' + evt.attachmentId : '';
            var menu = shared.rowMenuShell('row-menu-dropdown--events',
                '<button type="button" class="row-menu-item" data-orbita-detail-open><i class="fa-regular fa-eye" aria-hidden="true"></i>Просмотреть детали</button>' +
                '<a class="row-menu-item" href="' + shared.escapeHtml(settingsLogsUrl(evt.workerId)) + '"><i class="fa-regular fa-file-lines" aria-hidden="true"></i>Открыть лог</a>' +
                (accountUrl ? '<a class="row-menu-item" href="' + shared.escapeHtml(accountUrl) + '"><i class="fa-regular fa-user" aria-hidden="true"></i>Перейти к аккаунту</a>' : '') +
                '<a class="row-menu-item" href="' + shared.escapeHtml(workerUrl) + '"><i class="fa-solid fa-server" aria-hidden="true"></i>Перейти к воркеру</a>' +
                '<button type="button" class="row-menu-item" data-copy-event><i class="fa-regular fa-copy" aria-hidden="true"></i>Копировать сообщение</button>' +
                '<button type="button" class="row-menu-item" data-event-dismiss data-event-id="' + shared.escapeHtml(evt.id) + '"><i class="fa-regular fa-circle-check" aria-hidden="true"></i>Отметить обработанным</button>');

            return '<tr class="events-row" data-copy="' + shared.escapeHtml(evt.copyText || '') + '"' +
                ' data-detail-title="' + shared.escapeHtml(evt.eventTypeLabel || 'Детали') + '"' +
                ' data-detail-subtitle="' + shared.escapeHtml((evt.workerName || '') + ' · ' + (evt.levelLabel || '')) + '"' +
                ' data-detail-body="' + shared.escapeHtml(evt.description || '') + '"' +
                ' data-detail-attachment="' + shared.escapeHtml(attachmentUrl) + '">' +
                '<td class="events-time" data-label="Время"><time data-orbita-utc="' + shared.escapeHtml(evt.occurredAtUtc) + '" data-orbita-format="datetime-seconds"></time></td>' +
                '<td data-label="Тип события"><span class="event-type event-type--' + shared.escapeHtml(evt.eventTypeTone || 'info') + '"><i class="' + shared.escapeHtml(evt.eventTypeIcon || 'fa-regular fa-circle') + ' event-type-icon" aria-hidden="true"></i><span>' + shared.escapeHtml(evt.eventTypeLabel || '') + '</span></span></td>' +
                '<td data-label="Уровень"><span class="event-level-badge event-level-badge--' + shared.escapeHtml(evt.level || 'info') + '">' + shared.escapeHtml(evt.levelLabel || '') + '</span></td>' +
                '<td class="cell-link" data-label="Аккаунт">' + accountCell + '</td>' +
                '<td class="cell-link" data-label="Воркер"><a href="' + shared.escapeHtml(workerUrl) + '">' + shared.escapeHtml(evt.workerName || '') + '</a></td>' +
                '<td class="events-desc" data-label="Описание" title="' + shared.escapeHtml(evt.description || '') + '">' + shared.escapeHtml(evt.description || '') + '</td>' +
                '<td class="data-table-menu" data-label="">' + menu + '</td></tr>';
        }).join('');

        if (window.OrbitaTime) {
            window.OrbitaTime.localizeAll(tbody);
        }
        shared.reinitLiveContent();
    }

    function applySnapshot(snapshot, highlightChanged) {
        if (!snapshot || !shared) return;
        var prev = liveState ? shared.stableJson(liveState.events) : null;
        var next = shared.stableJson(snapshot.events || []);
        shared.updateKpiCards(snapshot.kpiCards || [], highlightChanged);
        shared.updatePaginationInfo(snapshot.pagination);
        if (prev !== next) {
            renderEvents(snapshot.events || []);
            if (highlightChanged) {
                shared.highlightCard(document.querySelector('.card--events-table'));
            }
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
                if (!res.ok) throw new Error('Events snapshot failed: ' + res.status);
                return res.json();
            })
            .then(function (snapshot) { applySnapshot(snapshot, true); });
    }

    function initEventsPage() {
        initKpiCounters();
        initFilterAutoSubmit();
        if (window.OrbitaLive && shared && shared.getLiveRoot()) {
            window.OrbitaLive.register('events', { fetchSnapshot: fetchSnapshot });
        }
    }

    initEventsPage();
    document.addEventListener('orbita:content-updated', initEventsPage);
})();