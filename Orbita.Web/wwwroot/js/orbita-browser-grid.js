(function () {
    var STATUS_LABELS = {
        running: 'Работает',
        stopped: 'Остановлен',
        error: 'Ошибка'
    };

    function normalizeGuid(value) {
        return String(value || '').trim().toLowerCase();
    }

    function readBrowser(message) {
        return {
            accountId: message.accountId || message.AccountId,
            accountName: message.accountName || message.AccountName || '',
            adsPowerProfileId: message.adsPowerProfileId || message.AdsPowerProfileId || '',
            subProfileId: message.subProfileId || message.SubProfileId || '',
            subProfileName: message.subProfileName || message.SubProfileName || '',
            index: message.index || message.Index || 0,
            status: message.status || message.Status || 'stopped',
            pageUrl: message.pageUrl || message.PageUrl || '',
            statusMessage: message.statusMessage || message.StatusMessage || '',
            lastFrameAtMs: message.lastFrameAtMs || message.LastFrameAtMs || null
        };
    }

    function readFrame(message) {
        return {
            sessionId: message.sessionId || message.SessionId,
            accountId: message.accountId || message.AccountId,
            imageBase64: message.imageBase64 || message.ImageBase64,
            contentType: message.contentType || message.ContentType || 'image/jpeg',
            timestampMs: message.timestampMs || message.TimestampMs,
            pageUrl: message.pageUrl || message.PageUrl || '',
            subProfileId: message.subProfileId || message.SubProfileId || '',
            subProfileName: message.subProfileName || message.SubProfileName || ''
        };
    }

    function createGrid(options) {
        var root = options.root;
        var interactive = !!options.interactive;
        var tiles = {};
        var browsers = [];
        var searchQuery = '';
        var tileSize = 'md';
        var fullscreenTileId = null;

        function ensureShell() {
            if (!root || root.querySelector('[data-browser-grid]')) {
                return;
            }

            root.innerHTML =
                '<div class="orbita-browser-grid-page__toolbar">' +
                '<div class="orbita-browser-grid-page__search">' +
                '<i class="fa-solid fa-magnifying-glass" aria-hidden="true"></i>' +
                '<input type="search" data-browser-grid-search placeholder="Поиск браузеров…" autocomplete="off" />' +
                '</div>' +
                '<div class="orbita-browser-grid-page__controls">' +
                '<label class="orbita-browser-grid-page__size-label">Размер плиток' +
                '<select data-browser-grid-size>' +
                '<option value="sm">Компактный</option>' +
                '<option value="md" selected>Средний</option>' +
                '<option value="lg">Крупный</option>' +
                '</select></label>' +
                '<span class="orbita-browser-grid-page__count" data-browser-grid-count></span>' +
                '</div></div>' +
                '<div class="orbita-browser-grid" data-browser-grid data-interactive="' + (interactive ? '1' : '0') + '"></div>' +
                '<div class="orbita-browser-grid-fullscreen" data-browser-grid-fullscreen hidden>' +
                '<div class="orbita-browser-grid-fullscreen__backdrop" data-browser-grid-fullscreen-close></div>' +
                '<div class="orbita-browser-grid-fullscreen__dialog" role="dialog" aria-modal="true"></div></div>';

            var searchInput = root.querySelector('[data-browser-grid-search]');
            var sizeSelect = root.querySelector('[data-browser-grid-size]');
            if (searchInput) {
                searchInput.addEventListener('input', function () {
                    searchQuery = String(searchInput.value || '').trim().toLowerCase();
                    renderTiles();
                });
            }
            if (sizeSelect) {
                sizeSelect.addEventListener('change', function () {
                    tileSize = sizeSelect.value || 'md';
                    var grid = root.querySelector('[data-browser-grid]');
                    if (grid) {
                        grid.setAttribute('data-size', tileSize);
                    }
                });
            }

            root.querySelectorAll('[data-browser-grid-fullscreen-close]').forEach(function (btn) {
                btn.addEventListener('click', closeFullscreen);
            });
        }

        function statusLabel(status) {
            return STATUS_LABELS[status] || status || '—';
        }

        function statusTone(status) {
            if (status === 'running') return 'live';
            if (status === 'error') return 'danger';
            return 'muted';
        }

        function formatFrameTime(ms) {
            if (!ms) return '—';
            if (window.OrbitaTime && typeof window.OrbitaTime.formatUtcIso === 'function') {
                return window.OrbitaTime.formatUtcIso(new Date(ms).toISOString(), 'time-short');
            }
            return new Date(ms).toLocaleTimeString();
        }

        function matchesSearch(browser) {
            if (!searchQuery) return true;
            var haystack = [
                browser.accountName,
                browser.subProfileName,
                browser.adsPowerProfileId,
                browser.pageUrl,
                String(browser.index)
            ].join(' ').toLowerCase();
            return haystack.indexOf(searchQuery) >= 0;
        }

        function ensureTile(browser) {
            var id = normalizeGuid(browser.accountId);
            if (tiles[id]) {
                return tiles[id];
            }

            var tile = document.createElement('article');
            tile.className = 'orbita-browser-tile' + (interactive ? '' : ' orbita-browser-tile--readonly');
            tile.setAttribute('data-browser-tile', browser.accountId);
            tile.innerHTML =
                '<header class="orbita-browser-tile__head">' +
                '<div class="orbita-browser-tile__title-row">' +
                '<span class="orbita-browser-tile__index" data-browser-index></span>' +
                '<h3 class="orbita-browser-tile__title" data-browser-title></h3>' +
                '<span class="orbita-browser-tile__status" data-browser-status></span>' +
                '</div>' +
                '<p class="orbita-browser-tile__url" data-browser-url></p>' +
                '</header>' +
                '<div class="orbita-browser-tile__viewport">' +
                '<div class="orbita-browser-tile__loading" data-browser-loading>' +
                '<i class="fa-solid fa-spinner fa-spin" aria-hidden="true"></i>' +
                '<span>Ожидание кадра…</span></div>' +
                '<img class="orbita-browser-tile__frame" alt="" draggable="false" />' +
                '</div>' +
                '<footer class="orbita-browser-tile__foot">' +
                '<span data-browser-updated>Обновление: —</span>' +
                '<button type="button" class="orbita-browser-tile__fullscreen" data-browser-fullscreen aria-label="Полноэкранный просмотр">' +
                '<i class="fa-solid fa-up-right-and-down-left-from-center" aria-hidden="true"></i></button>' +
                '</footer>';

            if (!interactive) {
                tile.querySelector('.orbita-browser-tile__viewport').setAttribute('aria-hidden', 'false');
            }

            tile.querySelector('[data-browser-fullscreen]').addEventListener('click', function () {
                openFullscreen(browser.accountId);
            });

            tiles[id] = {
                el: tile,
                browser: browser,
                img: tile.querySelector('.orbita-browser-tile__frame'),
                loadingEl: tile.querySelector('[data-browser-loading]')
            };
            return tiles[id];
        }

        function updateTileMeta(tileState, browser) {
            tileState.browser = browser;
            tileState.el.querySelector('[data-browser-index]').textContent = '#' + (browser.index || '—');
            var title = browser.accountName || 'Браузер';
            if (browser.subProfileName) {
                title += ' · ' + browser.subProfileName;
            }
            tileState.el.querySelector('[data-browser-title]').textContent = title;
            tileState.el.querySelector('[data-browser-title]').title = title;
            var statusEl = tileState.el.querySelector('[data-browser-status]');
            statusEl.textContent = statusLabel(browser.status);
            statusEl.className = 'orbita-browser-tile__status orbita-browser-tile__status--' + statusTone(browser.status);
            var urlEl = tileState.el.querySelector('[data-browser-url]');
            urlEl.textContent = browser.pageUrl || browser.statusMessage || 'URL недоступен';
            urlEl.title = browser.pageUrl || browser.statusMessage || '';
            tileState.el.querySelector('[data-browser-updated]').textContent =
                'Обновление: ' + formatFrameTime(browser.lastFrameAtMs);
        }

        function setTileLoading(tileState, visible) {
            if (!tileState.loadingEl) return;
            if (visible) {
                tileState.loadingEl.removeAttribute('hidden');
            } else {
                tileState.loadingEl.setAttribute('hidden', '');
            }
        }

        function renderTiles() {
            ensureShell();
            var grid = root.querySelector('[data-browser-grid]');
            if (!grid) return;

            var visible = browsers.filter(matchesSearch);
            grid.setAttribute('data-size', tileSize);
            root.querySelector('[data-browser-grid-count]').textContent =
                visible.length + ' из ' + browsers.length;

            var visibleIds = {};
            visible.forEach(function (browser) {
                var tileState = ensureTile(browser);
                visibleIds[normalizeGuid(browser.accountId)] = true;
                updateTileMeta(tileState, browser);
                if (!tileState.el.parentNode) {
                    grid.appendChild(tileState.el);
                }
            });

            Object.keys(tiles).forEach(function (id) {
                if (!visibleIds[id] && tiles[id].el.parentNode) {
                    tiles[id].el.parentNode.removeChild(tiles[id].el);
                }
            });
        }

        function setCatalog(catalog) {
            browsers = (catalog || []).map(readBrowser);
            renderTiles();
        }

        function applyFrame(frameMessage) {
            var frame = readFrame(frameMessage);
            var tileState = tiles[normalizeGuid(frame.accountId)];
            if (!tileState || !frame.imageBase64 || !window.OrbitaBrowserFrame) {
                return;
            }

            setTileLoading(tileState, true);
            window.OrbitaBrowserFrame.renderLiveFrame(
                tileState.img,
                frame.imageBase64,
                frame.contentType,
                {
                    onReady: function () {
                        setTileLoading(tileState, false);
                        tileState.browser.lastFrameAtMs = frame.timestampMs;
                        if (frame.pageUrl) {
                            tileState.browser.pageUrl = frame.pageUrl;
                        }
                        if (frame.subProfileId) {
                            tileState.browser.subProfileId = frame.subProfileId;
                        }
                        if (frame.subProfileName) {
                            tileState.browser.subProfileName = frame.subProfileName;
                        }
                        tileState.browser.status = 'running';
                        updateTileMeta(tileState, tileState.browser);
                    },
                    onError: function () {
                        setTileLoading(tileState, false);
                    }
                });
        }

        function openFullscreen(accountId) {
            var tileState = tiles[normalizeGuid(accountId)];
            if (!tileState) return;
            var overlay = root.querySelector('[data-browser-grid-fullscreen]');
            var dialog = overlay && overlay.querySelector('.orbita-browser-grid-fullscreen__dialog');
            if (!overlay || !dialog) return;

            fullscreenTileId = accountId;
            dialog.innerHTML = '';
            var clone = tileState.el.cloneNode(true);
            clone.classList.add('orbita-browser-tile--fullscreen');
            var fullscreenBtn = clone.querySelector('[data-browser-fullscreen]');
            if (fullscreenBtn) {
                fullscreenBtn.remove();
            }
            dialog.appendChild(clone);
            overlay.removeAttribute('hidden');
        }

        function closeFullscreen() {
            fullscreenTileId = null;
            var overlay = root.querySelector('[data-browser-grid-fullscreen]');
            if (overlay) {
                overlay.setAttribute('hidden', '');
            }
        }

        ensureShell();

        return {
            setCatalog: setCatalog,
            applyFrame: applyFrame,
            closeFullscreen: closeFullscreen,
            destroy: function () {
                tiles = {};
                browsers = [];
                if (root) {
                    root.innerHTML = '';
                }
            }
        };
    }

    window.OrbitaBrowserGrid = {
        create: createGrid
    };
})();