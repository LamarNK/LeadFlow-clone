(function () {
    var palette = document.getElementById('orbitaSearchPalette');
    if (!palette) return;

    var input = palette.querySelector('[data-orbita-search-input]');
    var resultsEl = palette.querySelector('[data-orbita-search-results]');
    var debounceTimer = null;

    function openPalette() {
        palette.removeAttribute('hidden');
        if (input) {
            input.value = '';
            input.focus();
        }
        renderResults(null);
    }

    function closePalette() {
        palette.setAttribute('hidden', '');
    }

    function renderResults(data) {
        if (!resultsEl) return;
        if (!data) {
            resultsEl.innerHTML = '<p class="orbita-search__hint">Введите минимум 2 символа. <kbd>Ctrl+K</kbd></p>';
            return;
        }

        var sections = [
            { key: 'workers', label: 'Воркеры' },
            { key: 'accounts', label: 'Аккаунты' },
            { key: 'responses', label: 'Отклики' },
            { key: 'errors', label: 'Ошибки' }
        ];

        var html = '';
        sections.forEach(function (section) {
            var items = data[section.key] || [];
            if (!items.length) return;
            html += '<div class="orbita-search__section"><div class="orbita-search__section-title">' + section.label + '</div>';
            items.forEach(function (item) {
                html += '<button type="button" class="orbita-search__hit" data-orbita-search-hit data-url="' + item.url + '">' +
                    '<i class="' + item.iconClass + '" aria-hidden="true"></i>' +
                    '<span class="orbita-search__hit-text"><span class="orbita-search__hit-title">' + item.title + '</span>' +
                    (item.subtitle ? '<span class="orbita-search__hit-sub">' + item.subtitle + '</span>' : '') +
                    '</span></button>';
            });
            html += '</div>';
        });

        resultsEl.innerHTML = html || '<p class="orbita-search__hint">Ничего не найдено</p>';
    }

    function fetchResults(query) {
        fetch('/Search?q=' + encodeURIComponent(query), { credentials: 'same-origin' })
            .then(function (res) { return res.ok ? res.json() : null; })
            .then(function (data) { renderResults(data); })
            .catch(function () { renderResults(null); });
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
        if (hit) {
            var url = hit.getAttribute('data-url');
            closePalette();
            if (url && window.Orbita && window.Orbita.navigateTo) {
                window.Orbita.navigateTo(url, true);
            } else if (url) {
                window.location.href = url;
            }
        }
    });

    document.addEventListener('keydown', function (e) {
        if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
            e.preventDefault();
            if (palette.hasAttribute('hidden')) openPalette();
            else closePalette();
        }
        if (e.key === 'Escape' && !palette.hasAttribute('hidden')) {
            closePalette();
        }
    });

    if (input) {
        input.addEventListener('input', function () {
            var q = input.value.trim();
            clearTimeout(debounceTimer);
            if (q.length < 2) {
                renderResults(null);
                return;
            }
            debounceTimer = setTimeout(function () { fetchResults(q); }, 250);
        });
    }
})();