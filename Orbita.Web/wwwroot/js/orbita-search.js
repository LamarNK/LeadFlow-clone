(function () {
    var palette = document.getElementById('orbitaSearchPalette');
    if (!palette) return;

    var input = palette.querySelector('[data-orbita-search-input]');
    var resultsEl = palette.querySelector('[data-orbita-search-results]');
    var spinner = palette.querySelector('[data-orbita-search-spinner]');
    var debounceTimer = null;
    var fetchSeq = 0;
    var activeIndex = -1;
    var closeTimer = null;

    var sections = [
        { key: 'workers', label: 'Воркеры', tone: 'workers' },
        { key: 'accounts', label: 'Аккаунты', tone: 'accounts' },
        { key: 'responses', label: 'Отклики', tone: 'responses' },
        { key: 'errors', label: 'События', tone: 'events' }
    ];

    function escapeHtml(text) {
        return window.OrbitaLiveShared.escapeHtml(text == null ? '' : text);
    }

    function highlightQuery(text, query) {
        var safe = escapeHtml(text);
        if (!query || !text) return safe;
        var tokens = query.trim().split(/\s+/).filter(Boolean);
        if (!tokens.length) return safe;
        var pattern = tokens.map(function (t) {
            return t.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
        }).join('|');
        try {
            return safe.replace(new RegExp('(' + pattern + ')', 'gi'), '<mark class="orbita-search__mark">$1</mark>');
        } catch (e) {
            return safe;
        }
    }

    function getHits() {
        return Array.prototype.slice.call(resultsEl.querySelectorAll('[data-orbita-search-hit]'));
    }

    function setActiveIndex(index) {
        var hits = getHits();
        activeIndex = hits.length ? Math.max(0, Math.min(index, hits.length - 1)) : -1;
        hits.forEach(function (hit, i) {
            var isActive = i === activeIndex;
            hit.classList.toggle('is-active', isActive);
            hit.setAttribute('aria-selected', isActive ? 'true' : 'false');
        });
        if (activeIndex >= 0 && hits[activeIndex]) {
            hits[activeIndex].scrollIntoView({ block: 'nearest' });
        }
    }

    function setLoading(isLoading) {
        if (spinner) spinner.toggleAttribute('hidden', !isLoading);
        if (input) input.setAttribute('aria-busy', isLoading ? 'true' : 'false');
    }

    function renderIdleState() {
        activeIndex = -1;
        resultsEl.innerHTML =
            '<div class="orbita-search__idle">' +
            '<div class="orbita-search__idle-icon" aria-hidden="true"><i class="fa-solid fa-magnifying-glass"></i></div>' +
            '<p class="orbita-search__idle-title">Быстрый поиск по панели</p>' +
            '<p class="orbita-search__idle-sub">Введите минимум 2 символа</p>' +
            '<ul class="orbita-search__tips">' +
            '<li><i class="fa-solid fa-server" aria-hidden="true"></i>Имя воркера или машины</li>' +
            '<li><i class="fa-solid fa-user" aria-hidden="true"></i>Аккаунт Avito</li>' +
            '<li><i class="fa-solid fa-inbox" aria-hidden="true"></i>Телефон, имя или вакансия в отклике</li>' +
            '<li><i class="fa-regular fa-clipboard" aria-hidden="true"></i>Текст события или ошибки</li>' +
            '</ul>' +
            '</div>';
    }

    function renderEmptyState(query) {
        activeIndex = -1;
        resultsEl.innerHTML =
            '<div class="orbita-search__empty">' +
            '<div class="orbita-search__empty-icon" aria-hidden="true"><i class="fa-regular fa-face-frown"></i></div>' +
            '<p class="orbita-search__empty-title">Ничего не найдено</p>' +
            '<p class="orbita-search__empty-sub">По запросу «' + escapeHtml(query) + '» результатов нет. Попробуйте телефон, имя или текст ошибки.</p>' +
            '</div>';
    }

    function renderResults(data) {
        if (!resultsEl) return;
        if (!data) {
            renderIdleState();
            return;
        }

        var query = data.query || (input ? input.value.trim() : '');
        var html = '';

        sections.forEach(function (section) {
            var items = data[section.key] || [];
            if (!items.length) return;
            html += '<div class="orbita-search__section orbita-search__section--' + section.tone + '">' +
                '<div class="orbita-search__section-title">' + escapeHtml(section.label) + '</div>';
            items.forEach(function (item) {
                html += '<button type="button" class="orbita-search__hit" data-orbita-search-hit data-url="' + escapeHtml(item.url) + '" role="option" aria-selected="false">' +
                    '<span class="orbita-search__hit-icon orbita-search__hit-icon--' + section.tone + '"><i class="' + escapeHtml(item.iconClass) + '" aria-hidden="true"></i></span>' +
                    '<span class="orbita-search__hit-text">' +
                    '<span class="orbita-search__hit-title">' + highlightQuery(item.title, query) + '</span>' +
                    (item.subtitle ? '<span class="orbita-search__hit-sub">' + highlightQuery(item.subtitle, query) + '</span>' : '') +
                    '</span>' +
                    '<span class="orbita-search__hit-enter" aria-hidden="true"><kbd>↵</kbd></span>' +
                    '</button>';
            });
            html += '</div>';
        });

        if (!html) {
            renderEmptyState(query);
            return;
        }
        resultsEl.innerHTML = html;
        setActiveIndex(0);
    }

    function navigateHit(hit) {
        if (!hit) return;
        var url = hit.getAttribute('data-url');
        closePalette();
        if (url && window.Orbita && window.Orbita.navigateTo) {
            window.Orbita.navigateTo(url, true);
        } else if (url) {
            window.location.href = url;
        }
    }

    function openPalette() {
        if (closeTimer) {
            window.clearTimeout(closeTimer);
            closeTimer = null;
        }
        palette.removeAttribute('hidden');
        document.body.classList.add('orbita-search-open');
        requestAnimationFrame(function () {
            palette.classList.add('is-open');
        });
        if (input) {
            input.value = '';
            input.focus();
            input.select();
        }
        activeIndex = -1;
        setLoading(false);
        renderIdleState();
    }

    function closePalette() {
        palette.classList.remove('is-open');
        document.body.classList.remove('orbita-search-open');
        if (closeTimer) window.clearTimeout(closeTimer);
        closeTimer = window.setTimeout(function () {
            palette.setAttribute('hidden', '');
            closeTimer = null;
        }, 160);
        setLoading(false);
        activeIndex = -1;
    }

    function fetchResults(query) {
        var seq = ++fetchSeq;
        setLoading(true);
        fetch('/Search?q=' + encodeURIComponent(query), { credentials: 'same-origin' })
            .then(function (res) { return res.ok ? res.json() : null; })
            .then(function (data) {
                if (seq !== fetchSeq) return;
                setLoading(false);
                if (!data || !data.query) {
                    renderIdleState();
                    return;
                }
                renderResults(data);
            })
            .catch(function () {
                if (seq !== fetchSeq) return;
                setLoading(false);
                renderIdleState();
            });
    }

    document.addEventListener('click', function (e) {
        if (e.target.closest('[data-orbita-search-open]')) {
            e.preventDefault();
            openPalette();
        }
        if (e.target.closest('[data-orbita-search-close]')) {
            closePalette();
        }
        var hit = e.target.closest('[data-orbita-search-hit]');
        if (hit && palette.contains(hit)) {
            navigateHit(hit);
        }
    });

    document.addEventListener('keydown', function (e) {
        if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
            e.preventDefault();
            if (palette.hasAttribute('hidden')) openPalette();
            else closePalette();
            return;
        }

        if (palette.hasAttribute('hidden')) return;

        if (e.key === 'Escape') {
            e.preventDefault();
            closePalette();
            return;
        }

        if (!input || document.activeElement !== input) return;

        var hits = getHits();
        if (!hits.length) return;

        if (e.key === 'ArrowDown') {
            e.preventDefault();
            setActiveIndex(activeIndex < 0 ? 0 : activeIndex + 1);
        } else if (e.key === 'ArrowUp') {
            e.preventDefault();
            setActiveIndex(activeIndex < 0 ? hits.length - 1 : activeIndex - 1);
        } else if (e.key === 'Enter') {
            e.preventDefault();
            if (activeIndex >= 0 && hits[activeIndex]) {
                navigateHit(hits[activeIndex]);
            } else if (hits[0]) {
                navigateHit(hits[0]);
            }
        }
    });

    if (input) {
        input.addEventListener('input', function () {
            var q = input.value.trim();
            clearTimeout(debounceTimer);
            fetchSeq++;
            setLoading(false);
            if (q.length < 2) {
                renderIdleState();
                return;
            }
            debounceTimer = window.setTimeout(function () { fetchResults(q); }, 220);
        });
    }

    resultsEl.addEventListener('mousemove', function (e) {
        var hit = e.target.closest('[data-orbita-search-hit]');
        if (!hit || !palette.contains(hit)) return;
        var hits = getHits();
        var index = hits.indexOf(hit);
        if (index >= 0) setActiveIndex(index);
    });
})();