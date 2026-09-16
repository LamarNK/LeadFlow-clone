(function () {
    function initKpiCounters() {
        if (window.OrbitaLiveShared && typeof window.OrbitaLiveShared.initializeKpiCounters === 'function') {
            window.OrbitaLiveShared.initializeKpiCounters('.listings-page [data-kpi-count]');
            return;
        }
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

        document.querySelectorAll('.listings-page [data-kpi-count]').forEach(function (el, index) {
            if (el.hasAttribute('data-listings-kpi-initialized')) return;
            el.setAttribute('data-listings-kpi-initialized', '1');

            var target = parseFloat(el.getAttribute('data-kpi-count'));
            if (isNaN(target)) return;

            var suffix = el.getAttribute('data-kpi-suffix') || '';
            var displayed = parseFloat(el.textContent);
            if (!isNaN(displayed) && Math.round(displayed) === Math.round(target)) {
                return;
            }

            if (reduced || !window.OrbitaLiveShared
                || typeof window.OrbitaLiveShared.animateKpiValue !== 'function') {
                el.textContent = Math.round(target) + suffix;
                return;
            }

            window.OrbitaLiveShared.animateKpiValue(
                el,
                0,
                target,
                suffix,
                720,
                80 + index * 70);
        });
    }

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
        var tone = escapeHtml(row.stateTone || 'ok');
        var image = row.imageUrl
            ? '<img src="' + escapeHtml(row.imageUrl) + '" alt="" loading="lazy">'
            : '<i class="fa-regular fa-image" aria-hidden="true"></i>';
        var geo = [row.city, row.addressText, row.districtText].filter(Boolean).filter(function (value, index, values) {
            return values.indexOf(value) === index;
        }).join(', ');
        var salary = row.salary ? '<div class="listing-card__salary">' + escapeHtml(row.salary) + '</div>' : '';
        var error = row.errorReason
            ? '<div class="listing-card__error"><i class="fa-solid fa-circle-exclamation" aria-hidden="true"></i><div><strong>Причина</strong><span>' + escapeHtml(row.errorReason) + '</span></div></div>'
            : '';
        var renewal = row.canPublish
            ? '<div class="listing-card__renewal"><i class="fa-solid fa-rotate" aria-hidden="true"></i> Можно продлить через воркер</div>'
            : '';
        var conversion = row.views > 0
            ? '<div class="listing-card__conversion">Конверсия <strong>' + escapeHtml(Math.round((row.contacts * 100 / row.views) * 10) / 10) + '%</strong></div>'
            : '';
        var remaining = row.remainingDays == null ? '' : '<small>Осталось ' + escapeHtml(row.remainingDays) + ' дн.</small>';
        var age = row.ageDays == null ? escapeHtml(row.statusText) : escapeHtml(row.ageDays) + ' дн. на Авито';

        return '<article class="listing-card listing-card--' + tone + '" data-listing-id="' + escapeHtml(row.id) + '">' +
            '<div class="listing-card__media">' + image + '</div>' +
            '<div class="listing-card__main"><div class="listing-card__heading">' + renderTitle(row) +
            '<span class="listing-state listing-state--' + tone + '">' + escapeHtml(row.stateLabel) + '</span></div>' +
            salary +
            (geo ? '<div class="listing-card__geo"><i class="fa-solid fa-location-dot" aria-hidden="true"></i><span>' + escapeHtml(geo) + '</span></div>' : '') +
            '<div class="listing-card__source">' + escapeHtml(row.accountName) + ' <span>/</span> ' + escapeHtml(row.subProfileName) + ' <span>·</span> ID ' + escapeHtml(row.avitoItemId) + '</div>' +
            error + renewal + '</div>' +
            '<div class="listing-card__aside"><div class="listing-metrics">' +
            '<span title="Просмотры"><i class="fa-regular fa-eye"></i><strong>' + escapeHtml(row.views) + '</strong></span>' +
            '<span title="Контакты"><i class="fa-regular fa-user"></i><strong>' + escapeHtml(row.contacts) + '</strong></span>' +
            '<span title="В избранном"><i class="fa-regular fa-heart"></i><strong>' + escapeHtml(row.favorites) + '</strong></span></div>' +
            conversion + '<div class="listing-card__deadline"><span>Срок размещения</span><strong>' + formatUtc(toIso(row.expiresAtUtc), 'datetime') + '</strong>' + remaining + '</div>' +
            '<div class="listing-card__age">' + age + '</div><div class="listing-card__checked">Проверено ' + formatUtc(toIso(row.lastSeenAtUtc), 'short') + '</div></div></article>';
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
        initKpiCounters();
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
