(function () {
    function initKpiCounters() {
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

        document.querySelectorAll('.workers-kpi-row [data-kpi-count]').forEach(function (el, index) {
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
        document.querySelectorAll('.workers-row[data-href]').forEach(function (row) {
            if (row.hasAttribute('data-workers-row-bound')) return;
            row.setAttribute('data-workers-row-bound', '1');

            row.addEventListener('click', function (e) {
                if (e.target.closest('[data-row-menu]') || e.target.closest('a') || e.target.closest('form')) return;
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

    function initAddWorkerModal() {
        var modal = document.getElementById('workersAddModal');
        if (!modal) return;

        function openModal() {
            modal.removeAttribute('hidden');
            var input = modal.querySelector('#workerDisplayName');
            if (input) input.focus();
        }

        function closeModal() {
            modal.setAttribute('hidden', '');
        }

        document.querySelectorAll('[data-workers-add-open]').forEach(function (btn) {
            btn.addEventListener('click', openModal);
        });

        modal.querySelectorAll('[data-workers-add-close]').forEach(function (el) {
            el.addEventListener('click', closeModal);
        });

        if (!window.__orbitaWorkersModalKeydown) {
            document.addEventListener('keydown', function (e) {
                if (e.key === 'Escape' && !modal.hasAttribute('hidden')) closeModal();
            });
            window.__orbitaWorkersModalKeydown = true;
        }
    }

    var shared = window.OrbitaLiveShared;
    var liveState = null;

    function workerDetailsUrl(id) {
        return shared.urlFromTemplate(shared.getLiveAttr('data-worker-details-url'), '__id__', id);
    }

    function settingsLogsUrl(workerId) {
        return shared.urlFromTemplate(shared.getLiveAttr('data-settings-logs-url'), '__id__', workerId);
    }

    function renderWorkerNameCell(w) {
        var detailsUrl = workerDetailsUrl(w.id);
        var machineHtml = shared.shouldShowMachineName(w.displayName, w.machineName)
            ? '<span class="cell-name-machine">' + shared.escapeHtml(w.machineName) + '</span>'
            : '';
        return '<div class="cell-name-stack"><a href="' + shared.escapeHtml(detailsUrl) + '">' + shared.escapeHtml(w.displayName) + '</a>' + machineHtml + '</div>';
    }

    function renderWorkerMenu(w, token) {
        var detailsUrl = workerDetailsUrl(w.id);
        var items = '<a class="row-menu-item" href="' + shared.escapeHtml(detailsUrl) + '"><i class="fa-regular fa-eye" aria-hidden="true"></i>Открыть</a>';
        if (w.isEnabled) {
            items += '<button type="button" class="row-menu-item" data-worker-restart data-worker-id="' + shared.escapeHtml(w.id) + '"><i class="fa-solid fa-rotate-right" aria-hidden="true"></i>Перезапустить</button>' +
                '<form action="/Workers/Disable" method="post" class="settings-inline-form"><input type="hidden" name="__RequestVerificationToken" value="' + shared.escapeHtml(token) + '" /><input type="hidden" name="workerId" value="' + shared.escapeHtml(w.id) + '" /><input type="hidden" name="returnTo" value="index" /><button type="submit" class="row-menu-item"><i class="fa-solid fa-circle-pause" aria-hidden="true"></i>Приостановить</button></form>';
        } else {
            items += '<form action="/Workers/Enable" method="post" class="settings-inline-form"><input type="hidden" name="__RequestVerificationToken" value="' + shared.escapeHtml(token) + '" /><input type="hidden" name="workerId" value="' + shared.escapeHtml(w.id) + '" /><input type="hidden" name="returnTo" value="index" /><button type="submit" class="row-menu-item"><i class="fa-solid fa-circle-play" aria-hidden="true"></i>Включить</button></form>';
        }
        items += '<a class="row-menu-item" href="' + shared.escapeHtml(detailsUrl + '#worker-settings') + '"><i class="fa-regular fa-pen-to-square" aria-hidden="true"></i>Настройки</a>';
        if (shared.getLiveAttr('data-show-admin-logs') === 'true') {
            items += '<a class="row-menu-item" href="' + shared.escapeHtml(settingsLogsUrl(w.id)) + '"><i class="fa-solid fa-file-lines" aria-hidden="true"></i>Просмотреть логи</a>';
        }
        items += '<form action="/Workers/Delete" method="post" class="settings-inline-form" onsubmit="return confirm(\'Удалить воркер? Это действие необратимо.\');"><input type="hidden" name="__RequestVerificationToken" value="' + shared.escapeHtml(token) + '" /><input type="hidden" name="workerId" value="' + shared.escapeHtml(w.id) + '" /><button type="submit" class="row-menu-item row-menu-item--danger"><i class="fa-regular fa-trash-can" aria-hidden="true"></i>Удалить</button></form>';
        return shared.rowMenuShell('row-menu-dropdown--workers', items);
    }

    function renderWorkers(workers) {
        var tbody = document.querySelector('[data-orbita-live-body="workers"]');
        if (!tbody || !shared) return;
        var token = shared.getRequestVerificationToken();

        tbody.innerHTML = (workers || []).map(function (w) {
            var detailsUrl = workerDetailsUrl(w.id);
            var statusClass = !w.isEnabled ? ' offline' : (w.isOnline ? '' : ' offline');
            var statusText = !w.isEnabled ? 'Приостановлен' : (w.isOnline ? 'Онлайн' : 'Оффлайн');
            var iso = w.lastActivityUtc || '';
            var timeHtml = iso
                ? '<time data-orbita-utc="' + shared.escapeHtml(iso) + '" data-orbita-format="activity"></time>'
                : '—';
            var disabledRow = !w.isEnabled ? ' workers-row--disabled' : '';
            var updateBadge = w.updateAvailable
                ? '<span class="workers-update-badge"' + (w.latestReleaseVersion ? ' title="Доступна версия ' + shared.escapeHtml(w.latestReleaseVersion) + '"' : '') + '>Обновление</span>'
                : '';
            var pausedBadge = !w.isEnabled
                ? '<span class="workers-status-badge workers-status-badge--disabled">Приостановлен</span>'
                : '';

            return '<tr class="workers-row' + disabledRow + '" data-href="' + shared.escapeHtml(detailsUrl) + '" data-worker-id="' + shared.escapeHtml(w.id) + '">' +
                '<td class="cell-name" data-label="Воркер">' + renderWorkerNameCell(w) + pausedBadge + updateBadge + '</td>' +
                '<td data-label="Статус"><span class="status-dot' + statusClass + '"><i class="fa-solid fa-circle status-dot-icon" aria-hidden="true"></i>' + statusText + '</span></td>' +
                '<td data-label="Сейчас">' + shared.renderActivityPill(w.currentActivityLabel, w.currentActivityTone, w.isActivityLive) + '</td>' +
                '<td data-label="Аккаунтов">' + w.activeAccounts + ' / ' + w.totalAccounts + '</td>' +
                '<td data-label="Откликов">' + w.responses + '</td>' +
                '<td data-label="Дублей">' + w.duplicates + '</td>' +
                '<td data-label="Ошибок">' + w.errors + '</td>' +
                '<td data-label="Последняя активность">' + timeHtml + '</td>' +
                '<td class="data-table-menu" data-label="">' + renderWorkerMenu(w, token) + '</td></tr>';
        }).join('');

        if (window.OrbitaTime) {
            window.OrbitaTime.localizeAll(tbody);
        }
        shared.reinitLiveContent();
    }

    function applySnapshot(snapshot, highlightChanged) {
        if (!snapshot || !shared) return;
        var prev = liveState ? shared.stableJson(liveState.workers) : null;
        var next = shared.stableJson(snapshot.workers || []);
        shared.updateKpiCards(snapshot.kpiCards || [], highlightChanged);
        shared.updatePaginationInfo(snapshot.pagination);
        if (prev !== next) {
            renderWorkers(snapshot.workers || []);
            if (highlightChanged) {
                shared.highlightCard(document.querySelector('.card--workers-table'));
            }
            initRowNavigation();
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
                if (!res.ok) throw new Error('Workers snapshot failed: ' + res.status);
                return res.json();
            })
            .then(function (snapshot) { applySnapshot(snapshot, true); });
    }

    function initWorkersPage() {
        initKpiCounters();
        if (window.Orbita && typeof window.Orbita.initWorkerRestartButtons === 'function') {
            window.Orbita.initWorkerRestartButtons();
        }
        initRowNavigation();
        initAddWorkerModal();
        if (window.OrbitaLive && shared && shared.getLiveRoot()) {
            window.OrbitaLive.register('workers', { fetchSnapshot: fetchSnapshot });
        }
    }

    initWorkersPage();
    document.addEventListener('orbita:content-updated', initWorkersPage);
})();