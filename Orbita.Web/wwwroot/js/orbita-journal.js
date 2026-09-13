(function () {
    function initKpiCounters() {
        if (window.OrbitaLiveShared && typeof window.OrbitaLiveShared.initializeKpiCounters === 'function') {
            window.OrbitaLiveShared.initializeKpiCounters('.events-kpi-row [data-kpi-count]');
            return;
        }
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

    var shared = window.OrbitaLiveShared;
    var liveState = null;

    function getJournalView() {
        var root = shared && shared.getLiveRoot();
        return root ? (root.getAttribute('data-orbita-journal-view') || 'all') : 'all';
    }

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
                '<a class="row-menu-item" href="' + shared.escapeHtml(settingsLogsUrl(evt.workerId)) + '"><i class="fa-regular fa-file-lines" aria-hidden="true"></i>Открыть лог</a>');
            var captchaAttrs = (evt.canSolveCaptcha && evt.captchaUrl && evt.accountId)
                ? ' data-captcha-can-solve="1" data-captcha-url="' + shared.escapeHtml(evt.captchaUrl) + '"' +
                  ' data-captcha-kind="' + shared.escapeHtml(evt.captchaKind || 'captcha') + '"' +
                  ' data-captcha-account-id="' + shared.escapeHtml(evt.accountId) + '"' +
                  ' data-captcha-worker-id="' + shared.escapeHtml(evt.workerId) + '"' +
                  ' data-captcha-account-name="' + shared.escapeHtml(evt.accountName || '') + '"' +
                  (evt.captchaSubProfileId ? ' data-captcha-subprofile-id="' + shared.escapeHtml(evt.captchaSubProfileId) + '"' : '')
                : '';
            return '<tr class="events-row" data-event-id="' + shared.escapeHtml(evt.id) + '" data-copy="' + shared.escapeHtml(evt.copyText || '') + '"' + captchaAttrs +
                ' data-detail-title="' + shared.escapeHtml(evt.eventTypeLabel || 'Детали') + '"' +
                ' data-detail-subtitle="' + shared.escapeHtml((evt.workerName || '') + ' · ' + (evt.levelLabel || '')) + '"' +
                ' data-detail-body="' + shared.escapeHtml(evt.description || '') + '"' +
                ' data-detail-attachment="' + shared.escapeHtml(attachmentUrl) + '"' +
                ' data-detail-log-url="' + shared.escapeHtml(settingsLogsUrl(evt.workerId)) + '"' +
                ' data-detail-worker-url="' + shared.escapeHtml(workerUrl) + '"' +
                ' data-detail-account-url="' + shared.escapeHtml(accountUrl) + '">' +
                '<td class="events-time" data-label="Время"><time data-orbita-utc="' + shared.escapeHtml(evt.occurredAtUtc) + '" data-orbita-format="datetime-seconds"></time></td>' +
                '<td data-label="Тип события"><span class="event-type event-type--' + shared.escapeHtml(evt.eventTypeTone || 'info') + '"><i class="' + shared.escapeHtml(evt.eventTypeIcon || 'fa-regular fa-circle') + ' event-type-icon" aria-hidden="true"></i><span>' + shared.escapeHtml(evt.eventTypeLabel || '') + '</span></span></td>' +
                '<td data-label="Уровень"><span class="event-level-badge event-level-badge--' + shared.escapeHtml(evt.level || 'info') + '">' + shared.escapeHtml(evt.levelLabel || '') + '</span></td>' +
                '<td class="cell-link" data-label="Аккаунт">' + accountCell + '</td>' +
                '<td class="cell-link" data-label="Воркер"><a href="' + shared.escapeHtml(workerUrl) + '">' + shared.escapeHtml(evt.workerName || '') + '</a></td>' +
                '<td class="events-desc" data-label="Описание" title="' + shared.escapeHtml(evt.description || '') + '">' + shared.escapeHtml(evt.description || '') + '</td>' +
                '<td class="data-table-menu" data-label="">' + menu + '</td></tr>';
        }).join('');
        if (window.OrbitaTime) window.OrbitaTime.localizeAll(tbody);
        shared.reinitLiveContent();
    }

    function renderErrors(rows) {
        var tbody = document.querySelector('[data-orbita-live-body="errors"]');
        if (!tbody || !shared) return;
        tbody.innerHTML = (rows || []).map(function (error) {
            var workerUrl = workerDetailsUrl(error.workerId);
            var accountUrl = error.accountName ? accountSearchUrl(error.accountName) : '';
            var accountCell = accountUrl
                ? '<a href="' + shared.escapeHtml(accountUrl) + '">' + shared.escapeHtml(error.accountName) + '</a>'
                : '<span class="errors-muted">—</span>';
            var attachmentUrl = error.attachmentId ? '/Diagnostics/Image/' + error.attachmentId : '';
            var countClass = (error.occurrenceCount || 0) > 10 ? ' errors-count-high' : '';
            var menu = shared.rowMenuShell('row-menu-dropdown--errors',
                '<a class="row-menu-item" href="' + shared.escapeHtml(settingsLogsUrl(error.workerId)) + '"><i class="fa-regular fa-file-lines" aria-hidden="true"></i>Открыть лог</a>');
            var captchaAttrs = (error.canSolveCaptcha && error.captchaUrl && error.accountId)
                ? ' data-captcha-can-solve="1" data-captcha-url="' + shared.escapeHtml(error.captchaUrl) + '"' +
                  ' data-captcha-kind="' + shared.escapeHtml(error.captchaKind || 'captcha') + '"' +
                  ' data-captcha-account-id="' + shared.escapeHtml(error.accountId) + '"' +
                  ' data-captcha-worker-id="' + shared.escapeHtml(error.workerId) + '"' +
                  ' data-captcha-account-name="' + shared.escapeHtml(error.accountName || '') + '"' +
                  (error.captchaSubProfileId ? ' data-captcha-subprofile-id="' + shared.escapeHtml(error.captchaSubProfileId) + '"' : '')
                : '';
            return '<tr class="errors-row" data-event-id="' + shared.escapeHtml(error.id) + '" data-copy="' + shared.escapeHtml(error.copyText || '') + '"' + captchaAttrs +
                ' data-detail-title="' + shared.escapeHtml(error.errorTypeLabel || 'Детали') + '"' +
                ' data-detail-subtitle="' + shared.escapeHtml((error.workerName || '') + ' · ' + (error.severityLabel || '')) + '"' +
                ' data-detail-body="' + shared.escapeHtml(error.message || '') + '"' +
                ' data-detail-attachment="' + shared.escapeHtml(attachmentUrl) + '"' +
                ' data-detail-log-url="' + shared.escapeHtml(settingsLogsUrl(error.workerId)) + '"' +
                ' data-detail-worker-url="' + shared.escapeHtml(workerUrl) + '"' +
                ' data-detail-account-url="' + shared.escapeHtml(accountUrl) + '">' +
                '<td class="errors-time" data-label="Время"><time data-orbita-utc="' + shared.escapeHtml(error.occurredAtUtc) + '" data-orbita-format="datetime-seconds"></time></td>' +
                '<td data-label="Уровень"><span class="error-severity-badge error-severity-badge--' + shared.escapeHtml(error.severity || 'medium') + '">' + shared.escapeHtml(error.severityLabel || '') + '</span></td>' +
                '<td class="errors-type" data-label="Тип ошибки">' + shared.escapeHtml(error.errorTypeLabel || '') + '</td>' +
                '<td class="errors-message" data-label="Сообщение" title="' + shared.escapeHtml(error.message || '') + '">' + shared.escapeHtml(error.message || '') + '</td>' +
                '<td class="cell-link" data-label="Аккаунт">' + accountCell + '</td>' +
                '<td class="cell-link" data-label="Воркер"><a href="' + shared.escapeHtml(workerUrl) + '">' + shared.escapeHtml(error.workerName || '') + '</a></td>' +
                '<td class="cell-num' + countClass + '" data-label="Повторений">' + (error.occurrenceCount || 0) + '</td>' +
                '<td class="errors-last-seen" data-label="Последнее появление"><time data-orbita-utc="' + shared.escapeHtml(error.lastSeenUtc) + '" data-orbita-format="time"></time></td>' +
                '<td class="data-table-menu" data-label="">' + menu + '</td></tr>';
        }).join('');
        if (window.OrbitaTime) window.OrbitaTime.localizeAll(tbody);
        shared.reinitLiveContent();
    }

    function applySnapshot(snapshot, highlightChanged) {
        if (!snapshot || !shared) return;
        var view = getJournalView();
        shared.updateKpiCards(snapshot.kpiCards || [], highlightChanged);
        shared.updatePaginationInfo(snapshot.pagination);

        if (view === 'errors') {
            var prevErrors = liveState ? shared.stableJson(liveState.errors) : null;
            var nextErrors = shared.stableJson(snapshot.errors || []);
            if (prevErrors !== nextErrors) {
                renderErrors(snapshot.errors || []);
                if (highlightChanged) shared.highlightCard(document.querySelector('.card--errors-table'));
            }
            liveState = { errors: snapshot.errors };
        } else {
            var prevEvents = liveState ? shared.stableJson(liveState.events) : null;
            var nextEvents = shared.stableJson(snapshot.events || []);
            if (prevEvents !== nextEvents) {
                renderEvents(snapshot.events || []);
                if (highlightChanged) shared.highlightCard(document.querySelector('.card--events-table'));
            }
            liveState = { events: snapshot.events };
        }
        shared.updateUpdatedClock(snapshot.updatedAtUtc);
    }

    var snapshotFetcher = shared && shared.createSnapshotFetcher
        ? shared.createSnapshotFetcher('journal', function (payload) { applySnapshot(payload, true); }, { errorName: 'Journal' })
        : null;

    function initJournalPage() {
        if (shared && shared.registerLivePage) {
            shared.registerLivePage('journal', snapshotFetcher, initKpiCounters);
            return;
        }
        initKpiCounters();
    }

    initJournalPage();
    document.addEventListener('orbita:content-updated', initJournalPage);
})();
