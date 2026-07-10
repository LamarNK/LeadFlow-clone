(function () {
    // Mirrors BrowserMonitorCoordinator: catalogIntervalMs=2000, frameIntervalMs=900.
    var CATALOG_INTERVAL_MS = 2000;
    var FRAME_INTERVAL_MS = 900;
    var FRAME_TIMEOUT_MS = CATALOG_INTERVAL_MS * 6 + FRAME_INTERVAL_MS * 3;

    var STATUS_LABELS = {
        running: 'Работает',
        stopped: 'Остановлен',
        error: 'Ошибка'
    };

    function normalizeGuid(value) {
        if (window.OrbitaLive && typeof window.OrbitaLive.normalizeGuid === 'function') {
            return window.OrbitaLive.normalizeGuid(value);
        }
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
            accountName: message.accountName || message.AccountName || '',
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
        var fullscreenState = null;
        var frameTimeouts = {};

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
                '<div class="orbita-empty-state" data-browser-grid-empty hidden>' +
                '<div class="orbita-empty-state__icon" aria-hidden="true"><i class="fa-solid fa-window-maximize"></i></div>' +
                '<p class="orbita-empty-state__title">Нет активных браузеров</p>' +
                '<p class="orbita-empty-state__desc">Плитки появятся, когда воркер откроет AdsPower-сессию.</p>' +
                '</div>' +
                '<div class="orbita-browser-grid-fullscreen" data-browser-grid-fullscreen hidden>' +
                '<div class="orbita-browser-grid-fullscreen__backdrop" data-browser-grid-fullscreen-close></div>' +
                '<div class="orbita-browser-grid-fullscreen__dialog" role="dialog" aria-modal="true"></div>' +
                '<button type="button" class="orbita-browser-grid-fullscreen__close" data-browser-grid-fullscreen-close aria-label="Закрыть полноэкранный просмотр">' +
                '<i class="fa-solid fa-xmark" aria-hidden="true"></i></button></div>';

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

        function clearFrameTimeout(id) {
            if (frameTimeouts[id]) {
                clearTimeout(frameTimeouts[id]);
                delete frameTimeouts[id];
            }
        }

        function setTileFrameError(tileState, message) {
            if (!tileState.loadingEl) return;
            tileState.awaitingFirstFrame = false;
            tileState.loadingEl.removeAttribute('hidden');
            if (tileState.loadingTextEl) {
                tileState.loadingTextEl.textContent = message || 'Кадр не получен';
            }
            var icon = tileState.loadingEl.querySelector('i');
            if (icon) {
                icon.className = 'fa-solid fa-triangle-exclamation';
            }
        }

        function resetTileLoading(tileState) {
            if (!tileState.loadingEl) return;
            if (tileState.loadingTextEl) {
                tileState.loadingTextEl.textContent = 'Ожидание кадра…';
            }
            var icon = tileState.loadingEl.querySelector('i');
            if (icon) {
                icon.className = 'fa-solid fa-spinner fa-spin';
            }
        }

        function scheduleFirstFrameTimeout(tileState, id) {
            if (!tileState.awaitingFirstFrame || frameTimeouts[id]) {
                return;
            }

            frameTimeouts[id] = setTimeout(function () {
                delete frameTimeouts[id];
                if (!tileState.awaitingFirstFrame || !tileState.loadingEl || tileState.loadingEl.hasAttribute('hidden')) {
                    return;
                }
                setTileFrameError(tileState, 'Кадр не получен. Проверьте подключение воркера.');
            }, FRAME_TIMEOUT_MS);
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
                '<span data-browser-loading-text>Ожидание кадра…</span></div>' +
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

            var tileState = {
                el: tile,
                browser: browser,
                img: tile.querySelector('.orbita-browser-tile__frame'),
                loadingEl: tile.querySelector('[data-browser-loading]'),
                loadingTextEl: tile.querySelector('[data-browser-loading-text]'),
                awaitingFirstFrame: true,
                pendingFrame: null,
                frameRenderPromise: null,
                lastRenderedTimestampMs: 0
            };
            tiles[id] = tileState;
            scheduleFirstFrameTimeout(tileState, id);
            return tileState;
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

            if (browser.lastFrameAtMs) {
                tileState.awaitingFirstFrame = false;
                clearFrameTimeout(normalizeGuid(browser.accountId));
            }
        }

        function setTileLoading(tileState, visible) {
            if (!tileState.loadingEl) return;
            if (visible) {
                tileState.loadingEl.removeAttribute('hidden');
            } else {
                tileState.loadingEl.setAttribute('hidden', '');
            }
        }

        function updateEmptyState(visibleCount, totalCount) {
            var emptyEl = root.querySelector('[data-browser-grid-empty]');
            var grid = root.querySelector('[data-browser-grid]');
            var titleEl = emptyEl && emptyEl.querySelector('.orbita-empty-state__title');
            var descEl = emptyEl && emptyEl.querySelector('.orbita-empty-state__desc');
            if (!emptyEl || !grid) {
                return;
            }

            if (totalCount === 0) {
                if (titleEl) {
                    titleEl.textContent = 'Нет активных браузеров';
                }
                if (descEl) {
                    descEl.textContent = 'Плитки появятся, когда воркер откроет AdsPower-сессию.';
                }
                emptyEl.removeAttribute('hidden');
                grid.setAttribute('hidden', '');
                return;
            }

            if (visibleCount === 0) {
                if (titleEl) {
                    titleEl.textContent = 'Ничего не найдено';
                }
                if (descEl) {
                    descEl.textContent = 'Измените поисковый запрос, чтобы увидеть активные браузеры.';
                }
                emptyEl.removeAttribute('hidden');
                grid.setAttribute('hidden', '');
                return;
            }

            emptyEl.setAttribute('hidden', '');
            grid.removeAttribute('hidden');
        }

        function renderTiles() {
            ensureShell();
            var grid = root.querySelector('[data-browser-grid]');
            if (!grid) return;

            var activeIds = {};
            browsers.forEach(function (browser) {
                activeIds[normalizeGuid(browser.accountId)] = true;
            });

            Object.keys(tiles).forEach(function (id) {
                if (!activeIds[id]) {
                    if (fullscreenState && fullscreenState.tileId === id) {
                        closeFullscreen();
                    }
                    clearFrameTimeout(id);
                    if (tiles[id].el.parentNode) {
                        tiles[id].el.parentNode.removeChild(tiles[id].el);
                    }
                    delete tiles[id];
                }
            });

            var visible = browsers.filter(matchesSearch);
            grid.setAttribute('data-size', tileSize);
            var countEl = root.querySelector('[data-browser-grid-count]');
            if (countEl) {
                if (browsers.length === 0) {
                    countEl.textContent = '0 активных';
                } else if (visible.length === browsers.length) {
                    countEl.textContent = browsers.length + ' активных';
                } else {
                    countEl.textContent = visible.length + ' из ' + browsers.length + ' активных';
                }
            }

            updateEmptyState(visible.length, browsers.length);

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

        function ensureBrowserFromFrame(frame) {
            var id = normalizeGuid(frame.accountId);
            var existing = browsers.find(function (browser) {
                return normalizeGuid(browser.accountId) === id;
            });
            if (existing) {
                return existing;
            }

            var browser = {
                accountId: frame.accountId,
                accountName: frame.accountName || frame.subProfileName || 'Браузер',
                adsPowerProfileId: '',
                subProfileId: frame.subProfileId || '',
                subProfileName: frame.subProfileName || '',
                index: browsers.length + 1,
                status: 'running',
                pageUrl: frame.pageUrl || '',
                statusMessage: '',
                lastFrameAtMs: frame.timestampMs || null
            };
            browsers.push(browser);
            renderTiles();
            return browser;
        }

        function applyBrowserFrame(tileState, frame) {
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
        }

        function renderTileFrame(tileState, frame) {
            return new Promise(function (resolve) {
                if (frame.timestampMs && frame.timestampMs < tileState.lastRenderedTimestampMs) {
                    resolve();
                    return;
                }

                resetTileLoading(tileState);
                setTileLoading(tileState, true);
                window.OrbitaBrowserFrame.renderLiveFrame(
                    tileState.img,
                    frame.imageBase64,
                    frame.contentType,
                    {
                        onReady: function () {
                            tileState.awaitingFirstFrame = false;
                            clearFrameTimeout(normalizeGuid(frame.accountId));
                            setTileLoading(tileState, false);
                            if (frame.timestampMs) {
                                tileState.lastRenderedTimestampMs = frame.timestampMs;
                            }
                            applyBrowserFrame(tileState, frame);
                            resolve();
                        },
                        onError: function () {
                            setTileFrameError(tileState, 'Ошибка отображения кадра');
                            resolve();
                        }
                    });
            });
        }

        function processTileFrames(tileState) {
            return (async function () {
                try {
                    while (tileState.pendingFrame) {
                        var frame = tileState.pendingFrame;
                        tileState.pendingFrame = null;
                        await renderTileFrame(tileState, frame);
                    }
                } finally {
                    tileState.frameRenderPromise = null;
                    if (tileState.pendingFrame) {
                        tileState.frameRenderPromise = processTileFrames(tileState);
                    }
                }
            })();
        }

        function enqueueTileFrame(tileState, frame) {
            if (frame.timestampMs && tileState.lastRenderedTimestampMs
                && frame.timestampMs <= tileState.lastRenderedTimestampMs
                && !tileState.pendingFrame) {
                return;
            }

            tileState.pendingFrame = frame;
            if (!tileState.frameRenderPromise) {
                tileState.frameRenderPromise = processTileFrames(tileState);
            }
        }

        function applyFrame(frameMessage) {
            var frame = readFrame(frameMessage);
            var tileId = normalizeGuid(frame.accountId);
            ensureBrowserFromFrame(frame);
            var tileState = tiles[tileId];
            if (!tileState) {
                return;
            }

            if (!frame.imageBase64 || !window.OrbitaBrowserFrame) {
                setTileFrameError(tileState, 'Пустой кадр от воркера');
                return;
            }

            enqueueTileFrame(tileState, frame);
        }

        function openFullscreen(accountId) {
            var tileState = tiles[normalizeGuid(accountId)];
            if (!tileState || fullscreenState) return;

            var overlay = root.querySelector('[data-browser-grid-fullscreen]');
            var dialog = overlay && overlay.querySelector('.orbita-browser-grid-fullscreen__dialog');
            var viewport = tileState.el.querySelector('.orbita-browser-tile__viewport');
            if (!overlay || !dialog || !viewport) return;

            fullscreenState = {
                tileId: normalizeGuid(accountId),
                tileState: tileState,
                parent: viewport.parentNode,
                nextSibling: viewport.nextSibling
            };

            viewport.classList.add('orbita-browser-tile__viewport--fullscreen');
            dialog.appendChild(viewport);
            overlay.removeAttribute('hidden');
        }

        function closeFullscreen() {
            if (!fullscreenState) return;

            var viewport = fullscreenState.tileState.el.querySelector('.orbita-browser-tile__viewport');
            if (viewport) {
                viewport.classList.remove('orbita-browser-tile__viewport--fullscreen');
                if (fullscreenState.nextSibling) {
                    fullscreenState.parent.insertBefore(viewport, fullscreenState.nextSibling);
                } else {
                    fullscreenState.parent.appendChild(viewport);
                }
            }

            var overlay = root.querySelector('[data-browser-grid-fullscreen]');
            if (overlay) {
                overlay.setAttribute('hidden', '');
            }

            fullscreenState = null;
        }

        ensureShell();

        function getActiveCount() {
            return browsers.length;
        }

        return {
            setCatalog: setCatalog,
            applyFrame: applyFrame,
            closeFullscreen: closeFullscreen,
            getActiveCount: getActiveCount,
            destroy: function () {
                closeFullscreen();
                Object.keys(frameTimeouts).forEach(clearFrameTimeout);
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