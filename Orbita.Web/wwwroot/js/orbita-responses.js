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

    var shared = window.OrbitaLiveShared;
    var liveState = null;

    function workerDetailsUrl(id) {
        return shared.urlFromTemplate(shared.getLiveAttr('data-worker-details-url'), '__id__', id);
    }

    function accountSearchUrl(name) {
        return shared.urlFromTemplate(shared.getLiveAttr('data-account-search-url'), '__q__', name);
    }

    function detailJsonUrl(id) {
        return shared.urlFromTemplate(shared.getLiveAttr('data-response-detail-json-url'), '__id__', id);
    }

    function responsesFilterUrl(accountId) {
        return shared.urlFromTemplate(shared.getLiveAttr('data-responses-filter-url'), '__id__', accountId);
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

    function initRowNavigation() {
        document.querySelectorAll('.responses-row').forEach(function (row) {
            if (row.hasAttribute('data-responses-row-bound')) return;
            row.setAttribute('data-responses-row-bound', '1');
            row.addEventListener('click', function (e) {
                if (e.target.closest('[data-row-menu], a, button')) return;
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
        var items = '<button type="button" class="row-menu-item" data-orbita-response-open data-response-id="' + shared.escapeHtml(row.id) + '"><i class="fa-regular fa-eye" aria-hidden="true"></i>Просмотреть отклик</button>';

        if (row.vacancyUrl) {
            items += '<a class="row-menu-item" href="' + shared.escapeHtml(row.vacancyUrl) + '" target="_blank" rel="noopener"><i class="fa-regular fa-rectangle-list" aria-hidden="true"></i>Открыть объявление</a>';
        }
        items += '<a class="row-menu-item" href="' + shared.escapeHtml(accountUrl) + '"><i class="fa-regular fa-user" aria-hidden="true"></i>Перейти к аккаунту</a>' +
            '<a class="row-menu-item" href="' + shared.escapeHtml(workerUrl) + '"><i class="fa-solid fa-server" aria-hidden="true"></i>Перейти к воркеру</a>';
        if (!row.isPhoneHidden) {
            items += '<button type="button" class="row-menu-item" data-copy-phone><i class="fa-regular fa-copy" aria-hidden="true"></i>Копировать телефон</button>';
        }
        if (row.bitrixEntityUrl) {
            items += '<a class="row-menu-item" href="' + shared.escapeHtml(row.bitrixEntityUrl) + '" target="_blank" rel="noopener"><i class="fa-solid fa-arrow-up-right-from-square" aria-hidden="true"></i>Открыть карточку Bitrix24</a>';
        }
        return shared.rowMenuShell('row-menu-dropdown--responses', items);
    }

    function renderResponses(rows) {
        var tbody = document.querySelector('[data-orbita-live-body="responses"]');
        if (!tbody || !shared) return;

        tbody.innerHTML = (rows || []).map(function (row) {
            var workerUrl = workerDetailsUrl(row.workerId);
            var accountUrl = accountSearchUrl(row.accountName);
            var adId = shared.displayAdId(row.sourceResponseId, row.vacancyUrl);
            var author = shared.displayAuthor(row.fullName);
            var adHtml = row.vacancyUrl
                ? '<a class="responses-ad-link" href="' + shared.escapeHtml(row.vacancyUrl) + '" target="_blank" rel="noopener">' + shared.escapeHtml(row.vacancy || '') + '</a>'
                : '<span class="responses-ad-link">' + shared.escapeHtml(row.vacancy || '') + '</span>';
            adHtml += '<span class="responses-ad-id">ID: ' + shared.escapeHtml(adId) + '</span>';

            var phoneDisplay = shared.formatPhone(row.phoneRaw, row.phoneNormalized);
            var phoneCell = row.isPhoneHidden
                ? '<span class="responses-phone-hidden">Скрыт</span>'
                : '<span>' + shared.escapeHtml(phoneDisplay) + '</span>';
            var cityDisplay = row.city && row.city.trim() ? shared.escapeHtml(row.city) : '—';
            var ageDisplay = row.age > 0 ? String(row.age) : '—';

            return '<tr class="responses-row" data-response-id="' + shared.escapeHtml(row.id) + '" data-phone="' + shared.escapeHtml(phoneDisplay) + '" data-detail-json-url="' + shared.escapeHtml(detailJsonUrl(row.id)) + '">' +
                '<td class="responses-time" data-label="Время"><time data-orbita-utc="' + shared.escapeHtml(row.createdAtUtc) + '" data-orbita-format="datetime"></time></td>' +
                '<td class="responses-author" data-label="Автор">' + shared.escapeHtml(author) + '</td>' +
                '<td class="responses-phone" data-label="Телефон">' + phoneCell + '</td>' +
                '<td class="responses-city" data-label="Город">' + cityDisplay + '</td>' +
                '<td class="responses-age" data-label="Возраст">' + ageDisplay + '</td>' +
                '<td class="responses-ad" data-label="Объявление">' + adHtml + '</td>' +
                '<td class="cell-link responses-account" data-label="Аккаунт">' + shared.renderResponseAccountCell(row.accountName, row.avitoSubProfileName, accountUrl) + '</td>' +
                '<td data-label="Статус"><span class="response-status-badge response-status-badge--' + shared.escapeHtml(row.statusTone || 'unique') + '">' + shared.escapeHtml(row.statusLabel || '') + '</span></td>' +
                '<td class="data-table-menu" data-label="">' + renderResponseMenu(row, accountUrl, workerUrl) + '</td></tr>';
        }).join('');

        if (window.OrbitaTime) window.OrbitaTime.localizeAll(tbody);
        shared.reinitLiveContent();
        initRowNavigation();
    }

    function applySnapshot(snapshot, highlightChanged) {
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
        var params = new URLSearchParams(window.location.search);
        var selectedId = params.get('id');
        if (selectedId) openResponseDetail(selectedId);
        if (window.OrbitaLive && shared && shared.getLiveRoot()) {
            window.OrbitaLive.register('responses', { fetchSnapshot: fetchSnapshot });
        }
    }

    initResponsesPage();
    document.addEventListener('orbita:content-updated', initResponsesPage);
})();