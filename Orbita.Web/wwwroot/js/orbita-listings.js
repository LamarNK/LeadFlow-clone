(function () {
    function escapeHtml(text) {
        return String(text || '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function formatUtc(iso, format) {
        if (!iso) return '—';
        return '<time datetime="' + escapeHtml(iso) + '" data-orbita-utc="' + escapeHtml(iso) + '" data-orbita-format="' + (format || 'datetime') + '"></time>';
    }

    function toIso(value) {
        if (!value) return '';
        return value;
    }

    function renderInternalLink(url, text) {
        var label = escapeHtml(text);
        var href = String(url || '').trim();
        if (!href) {
            return label;
        }
        return '<a href="' + escapeHtml(href) + '">' + label + '</a>';
    }

    function renderTitle(row) {
        var title = escapeHtml(row.title);
        var url = String(row.url || '').trim();
        if (!url) {
            return '<strong>' + title + '</strong>';
        }
        return '<a class="listings-title-link" href="' + escapeHtml(url) + '" target="_blank" rel="noopener noreferrer">' + title + '</a>';
    }

    function renderRow(row) {
        return '<tr class="listings-row listings-row--' + escapeHtml(row.stateTone || 'ok') + '" data-listing-id="' + escapeHtml(row.id) + '">' +
            '<td data-label="Воркер" class="cell-link">' + renderInternalLink(row.workerUrl, row.workerName) + '</td>' +
            '<td data-label="Аккаунт" class="cell-link">' + renderInternalLink(row.accountUrl, row.accountName) + '</td>' +
            '<td data-label="Субпрофиль">' + escapeHtml(row.subProfileName) + '</td>' +
            '<td data-label="Название">' + renderTitle(row) + '</td>' +
            '<td data-label="ID Avito"><code>' + escapeHtml(row.avitoItemId) + '</code></td>' +
            '<td data-label="Статус Avito">' + escapeHtml(row.statusText) + '</td>' +
            '<td data-label="Публикация">' + formatUtc(toIso(row.publishedAtUtc), 'datetime') + '</td>' +
            '<td data-label="Контрольный срок">' + formatUtc(toIso(row.expiresAtUtc), 'datetime') + '</td>' +
            '<td data-label="Возраст" class="cell-num">' + (row.ageDays == null ? '—' : row.ageDays) + '</td>' +
            '<td data-label="Осталось" class="cell-num">' + (row.remainingDays == null ? '—' : row.remainingDays) + '</td>' +
            '<td data-label="Состояние"><span class="listing-state listing-state--' + escapeHtml(row.stateTone || 'ok') + '">' + escapeHtml(row.stateLabel) + '</span></td>' +
            '<td data-label="Источник даты">' + escapeHtml(row.publicationDateSourceLabel) + '</td>' +
            '<td data-label="Обнаружено">' + formatUtc(toIso(row.lastSeenAtUtc), 'short') + '</td>' +
            '<td data-label="Детальная проверка">' + formatUtc(toIso(row.detailCheckedAtUtc), 'short') + '</td>' +
            '</tr>';
    }

    function applySnapshot(payload) {
        var body = document.querySelector('[data-orbita-live-body="listings"]');
        if (!body || !payload) return;
        var rows = payload.rows || payload.Rows || [];
        body.innerHTML = rows.map(renderRow).join('');
        if (window.OrbitaTime && typeof window.OrbitaTime.localize === 'function') {
            window.OrbitaTime.localize(body);
        } else if (window.OrbitaTime && typeof window.OrbitaTime.localizeElement === 'function') {
            body.querySelectorAll('[data-orbita-utc]').forEach(function (el) {
                window.OrbitaTime.localizeElement(el);
            });
        }

        if (window.OrbitaLiveShared && typeof window.OrbitaLiveShared.updateKpiCards === 'function') {
            window.OrbitaLiveShared.updateKpiCards(payload.kpiCards || payload.KpiCards || [], true);
        }
        if (window.OrbitaLiveShared && typeof window.OrbitaLiveShared.updateUpdatedClock === 'function') {
            window.OrbitaLiveShared.updateUpdatedClock(payload.updatedAtUtc || payload.UpdatedAtUtc);
        }
    }

    function init() {
        var root = document.querySelector('[data-orbita-live-page="listings"]');
        if (!root || !window.OrbitaLiveShared) return;
        var fetcher = window.OrbitaLiveShared.createSnapshotFetcher('listings', applySnapshot);
        window.OrbitaLiveShared.registerLivePage('listings', fetcher);
    }

    window.OrbitaListings = { init: init, applySnapshot: applySnapshot };

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
    document.addEventListener('orbita:content-updated', init);
})();
