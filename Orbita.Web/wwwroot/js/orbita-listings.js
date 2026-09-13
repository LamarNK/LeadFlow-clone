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

    function renderLocation(row) {
        return '<div class="listing-location__primary">' +
            renderInternalLink(row.workerUrl, row.workerName) +
            '<span aria-hidden="true">/</span>' +
            renderInternalLink(row.accountUrl, row.accountName) +
            '</div><div class="listing-location__subprofile">' +
            escapeHtml(row.subProfileName) +
            '</div>';
    }

    function renderAd(row) {
        return renderTitle(row) +
            '<code class="listing-ad__id">ID ' + escapeHtml(row.avitoItemId) + '</code>';
    }

    function renderStatus(row) {
        return '<span class="listing-status__avito">' + escapeHtml(row.statusText) + '</span>' +
            '<span class="listing-state listing-state--' + escapeHtml(row.stateTone || 'ok') + '">' +
            escapeHtml(row.stateLabel) +
            '</span>';
    }

    function renderDeadline(row) {
        var remaining = row.remainingDays == null
            ? ''
            : '<span class="listing-deadline__remaining">Осталось: ' + escapeHtml(row.remainingDays) + ' дн.</span>';
        return formatUtc(toIso(row.expiresAtUtc), 'datetime') + remaining;
    }

    function renderRow(row) {
        return '<tr class="listings-row listings-row--' + escapeHtml(row.stateTone || 'ok') + '" data-listing-id="' + escapeHtml(row.id) + '">' +
            '<td data-label="Где" class="listing-location">' + renderLocation(row) + '</td>' +
            '<td data-label="Объявление" class="listing-ad">' + renderAd(row) + '</td>' +
            '<td data-label="Статус" class="listing-status">' + renderStatus(row) + '</td>' +
            '<td data-label="Срок" class="listing-deadline">' + renderDeadline(row) + '</td>' +
            '<td data-label="Последняя проверка" class="listing-last-check">' + formatUtc(toIso(row.lastSeenAtUtc), 'short') + '</td>' +
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
