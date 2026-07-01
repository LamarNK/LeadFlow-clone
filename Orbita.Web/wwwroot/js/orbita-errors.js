(function () {
    function initKpiCounters() {
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

        document.querySelectorAll('.errors-kpi-row [data-kpi-count]').forEach(function (el, index) {
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
        var form = document.querySelector('.errors-filters');
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
                '<button type="button" class="row-menu-item" data-orbita-detail-open><i class="fa-regular fa-eye" aria-hidden="true"></i>Просмотреть детали</button>' +
                '<a class="row-menu-item" href="' + shared.escapeHtml(settingsLogsUrl(error.workerId)) + '"><i class="fa-regular fa-file-lines" aria-hidden="true"></i>Открыть лог</a>' +
                (accountUrl ? '<a class="row-menu-item" href="' + shared.escapeHtml(accountUrl) + '"><i class="fa-regular fa-user" aria-hidden="true"></i>Перейти к аккаунту</a>' : '') +
                '<a class="row-menu-item" href="' + shared.escapeHtml(workerUrl) + '"><i class="fa-solid fa-server" aria-hidden="true"></i>Перейти к воркеру</a>' +
                '<button type="button" class="row-menu-item" data-copy-error><i class="fa-regular fa-copy" aria-hidden="true"></i>Копировать текст ошибки</button>' +
                '<button type="button" class="row-menu-item" data-error-dismiss data-event-id="' + shared.escapeHtml(error.id) + '"><i class="fa-regular fa-circle-check" aria-hidden="true"></i>Отметить как обработанную</button>');

            return '<tr class="errors-row" data-copy="' + shared.escapeHtml(error.copyText || '') + '"' +
                ' data-detail-title="' + shared.escapeHtml(error.errorTypeLabel || 'Детали') + '"' +
                ' data-detail-subtitle="' + shared.escapeHtml((error.workerName || '') + ' · ' + (error.severityLabel || '')) + '"' +
                ' data-detail-body="' + shared.escapeHtml(error.message || '') + '"' +
                ' data-detail-attachment="' + shared.escapeHtml(attachmentUrl) + '">' +
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

        if (window.OrbitaTime) {
            window.OrbitaTime.localizeAll(tbody);
        }
        shared.reinitLiveContent();
    }

    function applySnapshot(snapshot, highlightChanged) {
        if (!snapshot || !shared) return;
        var prev = liveState ? shared.stableJson(liveState.errors) : null;
        var next = shared.stableJson(snapshot.errors || []);
        shared.updateKpiCards(snapshot.kpiCards || [], highlightChanged);
        shared.updatePaginationInfo(snapshot.pagination);
        if (prev !== next) {
            renderErrors(snapshot.errors || []);
            if (highlightChanged) {
                shared.highlightCard(document.querySelector('.card--errors-table'));
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
                if (!res.ok) throw new Error('Errors snapshot failed: ' + res.status);
                return res.json();
            })
            .then(function (snapshot) { applySnapshot(snapshot, true); });
    }

    function initErrorsPage() {
        initKpiCounters();
        initFilterAutoSubmit();
        if (window.OrbitaLive && shared && shared.getLiveRoot()) {
            window.OrbitaLive.register('errors', { fetchSnapshot: fetchSnapshot });
        }
    }

    initErrorsPage();
    document.addEventListener('orbita:content-updated', initErrorsPage);
})();