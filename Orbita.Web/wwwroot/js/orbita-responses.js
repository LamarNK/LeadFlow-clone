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
        var form = document.querySelector('.responses-filters');
        if (!form) return;

        form.querySelectorAll('select').forEach(function (select) {
            select.addEventListener('change', function () {
                form.submit();
            });
        });
    }

    function initRowNavigation() {
        document.querySelectorAll('.responses-row').forEach(function (row) {
            if (row.hasAttribute('data-responses-row-bound')) return;
            row.setAttribute('data-responses-row-bound', '1');
            row.addEventListener('click', function (e) {
                if (e.target.closest('[data-row-menu], a, button')) return;
                var url = row.getAttribute('data-detail-url');
                if (url) {
                    if (window.Orbita && typeof window.Orbita.navigateTo === 'function') {
                        window.Orbita.navigateTo(url, true);
                    } else {
                        window.location.href = url;
                    }
                }
            });
        });
    }

    function initDetailModal() {
        var modal = document.getElementById('responsesDetailModal');
        if (!modal) return;

        function closeModal() {
            var url = new URL(window.location.href);
            url.searchParams.delete('id');
            window.history.replaceState({}, '', url.toString());
            modal.setAttribute('hidden', '');
        }

        modal.removeAttribute('hidden');

        modal.querySelectorAll('[data-responses-modal-close]').forEach(function (el) {
            el.addEventListener('click', closeModal);
        });

        if (!window.__orbitaResponsesModalKeydown) {
            document.addEventListener('keydown', function (e) {
                if (e.key === 'Escape' && !modal.hasAttribute('hidden')) closeModal();
            });
            window.__orbitaResponsesModalKeydown = true;
        }
    }

    var shared = window.OrbitaLiveShared;
    var liveState = null;

    function workerDetailsUrl(id) {
        return shared.urlFromTemplate(shared.getLiveAttr('data-worker-details-url'), '__id__', id);
    }

    function accountSearchUrl(name) {
        return shared.urlFromTemplate(shared.getLiveAttr('data-account-search-url'), '__q__', name);
    }

    function responseDetailUrl(id) {
        return shared.urlFromTemplate(shared.getLiveAttr('data-response-detail-url'), '__id__', id);
    }

    function responsesFilterUrl(accountId) {
        return shared.urlFromTemplate(shared.getLiveAttr('data-responses-filter-url'), '__id__', accountId);
    }

    function renderPhoneCell(row, phoneDisplay) {
        if (row.isPhoneHidden) {
            return '<span class="responses-phone-hidden">Скрыт</span>';
        }
        var html = '<span>' + shared.escapeHtml(phoneDisplay || '—') + '</span>';
        if (row.hasMessenger && row.messengerUrl) {
            html += '<a class="responses-phone-action" href="' + shared.escapeHtml(row.messengerUrl) + '" target="_blank" rel="noopener" aria-label="WhatsApp"><i class="fa-brands fa-whatsapp" aria-hidden="true"></i></a>';
        } else if (!row.isPhoneHidden) {
            html += '<span class="responses-phone-action" aria-hidden="true"><i class="fa-solid fa-phone" aria-hidden="true"></i></span>';
        }
        return html;
    }

    function renderResponseMenu(row, detailUrl, accountUrl, workerUrl) {
        var items = '<a class="row-menu-item" href="' + shared.escapeHtml(detailUrl) + '"><i class="fa-regular fa-eye" aria-hidden="true"></i>Просмотреть отклик</a>';
        if (!row.isPhoneHidden && row.messengerUrl) {
            items += '<a class="row-menu-item" href="' + shared.escapeHtml(row.messengerUrl) + '" target="_blank" rel="noopener"><i class="fa-regular fa-user" aria-hidden="true"></i>Перейти к автору</a>';
        }
        if (row.vacancyUrl) {
            items += '<a class="row-menu-item" href="' + shared.escapeHtml(row.vacancyUrl) + '" target="_blank" rel="noopener"><i class="fa-regular fa-rectangle-list" aria-hidden="true"></i>Открыть объявление</a>';
        }
        items += '<a class="row-menu-item" href="' + shared.escapeHtml(accountUrl) + '"><i class="fa-regular fa-user" aria-hidden="true"></i>Перейти к аккаунту</a>' +
            '<a class="row-menu-item" href="' + shared.escapeHtml(workerUrl) + '"><i class="fa-solid fa-server" aria-hidden="true"></i>Перейти к воркеру</a>';
        if (!row.isPhoneHidden) {
            items += '<button type="button" class="row-menu-item" data-copy-phone><i class="fa-regular fa-copy" aria-hidden="true"></i>Копировать телефон</button>';
        }
        if (row.bitrixEntityId) {
            items += '<button type="button" class="row-menu-item" data-bitrix-id="' + shared.escapeHtml(row.bitrixEntityId) + '"><i class="fa-solid fa-arrow-up-right-from-square" aria-hidden="true"></i>Открыть карточку Bitrix24</button>';
        }
        return shared.rowMenuShell('row-menu-dropdown--responses', items);
    }

    function renderResponses(rows) {
        var tbody = document.querySelector('[data-orbita-live-body="responses"]');
        if (!tbody || !shared) return;

        tbody.innerHTML = (rows || []).map(function (row) {
            var phoneDisplay = shared.formatPhone(row.phoneRaw, row.phoneNormalized);
            var workerUrl = workerDetailsUrl(row.workerId);
            var accountUrl = accountSearchUrl(row.accountName);
            var detailUrl = responseDetailUrl(row.id);
            var adId = shared.displayAdId(row.sourceResponseId, row.vacancyUrl);
            var author = shared.displayAuthor(row.fullName);
            var adHtml = row.vacancyUrl
                ? '<a class="responses-ad-link" href="' + shared.escapeHtml(row.vacancyUrl) + '" target="_blank" rel="noopener">' + shared.escapeHtml(row.vacancy || '') + '</a>'
                : '<span class="responses-ad-link">' + shared.escapeHtml(row.vacancy || '') + '</span>';
            adHtml += '<span class="responses-ad-id">ID: ' + shared.escapeHtml(adId) + '</span>';
            var subProfile = row.avitoSubProfileName ? shared.escapeHtml(row.avitoSubProfileName) : '—';

            return '<tr class="responses-row" data-response-id="' + shared.escapeHtml(row.id) + '" data-phone="' + shared.escapeHtml(phoneDisplay) + '" data-detail-url="' + shared.escapeHtml(detailUrl) + '">' +
                '<td class="responses-time" data-label="Время"><time data-orbita-utc="' + shared.escapeHtml(row.createdAtUtc) + '" data-orbita-format="datetime"></time></td>' +
                '<td class="responses-ad" data-label="Объявление">' + adHtml + '</td>' +
                '<td class="responses-author" data-label="Автор">' + shared.escapeHtml(author) + '</td>' +
                '<td class="responses-phone" data-label="Телефон">' + renderPhoneCell(row, phoneDisplay) + '</td>' +
                '<td class="cell-link" data-label="Аккаунт"><a href="' + shared.escapeHtml(accountUrl) + '">' + shared.escapeHtml(row.accountName || '') + '</a></td>' +
                '<td data-label="Субпрофиль">' + subProfile + '</td>' +
                '<td class="cell-link" data-label="Воркер"><a href="' + shared.escapeHtml(workerUrl) + '">' + shared.escapeHtml(row.workerName || '') + '</a></td>' +
                '<td data-label="Статус"><span class="response-status-badge response-status-badge--' + shared.escapeHtml(row.statusTone || 'unique') + '">' + shared.escapeHtml(row.statusLabel || '') + '</span></td>' +
                '<td class="responses-source" data-label="Источник">' + shared.escapeHtml(row.source || '') + '</td>' +
                '<td class="data-table-menu" data-label="">' + renderResponseMenu(row, detailUrl, accountUrl, workerUrl) + '</td></tr>';
        }).join('');

        if (window.OrbitaTime) {
            window.OrbitaTime.localizeAll(tbody);
        }
        shared.reinitLiveContent();
    }

    function applySnapshot(snapshot, highlightChanged) {
        if (!snapshot || !shared) return;
        if (document.getElementById('responsesDetailModal') && !document.getElementById('responsesDetailModal').hasAttribute('hidden')) {
            return;
        }

        var prev = liveState ? shared.stableJson(liveState.responses) : null;
        var next = shared.stableJson(snapshot.responses || []);
        shared.updateKpiCards(snapshot.kpiCards || [], highlightChanged);
        shared.updatePaginationInfo(snapshot.pagination);
        if (prev !== next) {
            renderResponses(snapshot.responses || []);
            if (highlightChanged) {
                shared.highlightCard(document.querySelector('.card--responses-table'));
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
                if (!res.ok) throw new Error('Responses snapshot failed: ' + res.status);
                return res.json();
            })
            .then(function (snapshot) { applySnapshot(snapshot, true); });
    }

    function initResponsesPage() {
        initKpiCounters();
        initFilterAutoSubmit();
        initRowNavigation();
        initDetailModal();
        if (window.OrbitaLive && shared && shared.getLiveRoot()) {
            window.OrbitaLive.register('responses', { fetchSnapshot: fetchSnapshot });
        }
    }

    initResponsesPage();
    document.addEventListener('orbita:content-updated', initResponsesPage);
})();