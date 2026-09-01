(() => {
    const reducedMotion = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const scrollBehavior = () => (reducedMotion() ? 'auto' : 'smooth');

    const refreshers = new Set();
    const boardStateKey = 'orbita.crm.board.position.v1';
    const focusStageKey = 'orbita.crm.board.focusStage';
    const cardTaskReturnKey = 'orbita.crm.card.taskReturn.v1';
    const cardOpenAtTopKey = 'orbita.crm.card.openAtTop.v1';
    const responsibleFilterScrollKey = 'orbita.crm.responsibleFilter.scroll.v1';
    const bulkSelectionKey = 'orbita.crm.bulkSelection.v1';

    const normalizeBoardPath = (pathname) => {
        const normalized = (pathname || '').replace(/\/+$/, '').toLowerCase();
        return normalized === '/crm/index' ? '/crm' : (normalized || '/crm');
    };

    const normalizeBoardUrl = (value) => {
        try {
            const url = new URL(value || (window.location.pathname + window.location.search), window.location.origin);
            url.searchParams.delete('stage');
            url.searchParams.sort();
            const query = url.searchParams.toString();
            return normalizeBoardPath(url.pathname) + (query ? '?' + query : '');
        } catch {
            return normalizeBoardPath(window.location.pathname);
        }
    };

    const readStoredBoardState = () => {
        try {
            const state = JSON.parse(sessionStorage.getItem(boardStateKey) || 'null');
            if (!state || state.version !== 1) return null;
            return state;
        } catch {
            return null;
        }
    };

    const readBoardState = () => {
        const state = readStoredBoardState();
        if (!state) return null;
        return normalizeBoardUrl(state.boardUrl || state.pathname) === normalizeBoardUrl() ? state : null;
    };

    const writeBoardState = (state) => {
        try { sessionStorage.setItem(boardStateKey, JSON.stringify(state)); } catch { /* ignore */ }
    };

    const rememberResponsibleFilterScroll = (targetUrl) => {
        try {
            sessionStorage.setItem(responsibleFilterScrollKey, JSON.stringify({
                version: 1,
                targetUrl: normalizeBoardUrl(targetUrl),
                scrollX: window.scrollX,
                scrollY: window.scrollY,
                savedAt: Date.now()
            }));
        } catch { /* ignore */ }
    };

    const restoreResponsibleFilterScroll = () => {
        let state = null;
        try {
            state = JSON.parse(sessionStorage.getItem(responsibleFilterScrollKey) || 'null');
            sessionStorage.removeItem(responsibleFilterScrollKey);
        } catch {
            return;
        }

        if (!state || state.version !== 1 || state.targetUrl !== normalizeBoardUrl()) return;
        if (!Number.isFinite(state.savedAt) || Date.now() - state.savedAt > 2 * 60 * 1000) return;

        const restore = () => window.scrollTo({
            left: Number.isFinite(state.scrollX) ? Math.max(0, state.scrollX) : 0,
            top: Number.isFinite(state.scrollY) ? Math.max(0, state.scrollY) : 0,
            behavior: 'auto'
        });
        restore();
        window.requestAnimationFrame(() => {
            restore();
            window.requestAnimationFrame(restore);
        });
    };

    const clearCardTaskReturn = () => {
        try { sessionStorage.removeItem(cardTaskReturnKey); } catch { /* ignore */ }
    };

    const markCardOpenAtTop = () => {
        try { sessionStorage.setItem(cardOpenAtTopKey, String(Date.now())); } catch { /* ignore */ }
    };

    const consumeCardOpenAtTop = () => {
        try {
            const savedAt = Number(sessionStorage.getItem(cardOpenAtTopKey));
            sessionStorage.removeItem(cardOpenAtTopKey);
            return Number.isFinite(savedAt) && Date.now() - savedAt < 2 * 60 * 1000;
        } catch {
            return false;
        }
    };

    const readCardTaskReturn = (cardId) => {
        try {
            const state = JSON.parse(sessionStorage.getItem(cardTaskReturnKey) || 'null');
            if (!state || state.version !== 1 || state.cardId !== cardId || !state.url) return null;
            if (!Number.isFinite(state.savedAt) || Date.now() - state.savedAt > 8 * 60 * 60 * 1000) return null;
            const url = new URL(state.url, window.location.origin);
            if (url.origin !== window.location.origin || !url.pathname.toLowerCase().startsWith('/crm')) return null;
            return { url: url.pathname + url.search + url.hash };
        } catch {
            return null;
        }
    };

    const rememberCardTaskReturn = (cardId, url) => {
        if (!cardId || !url) return;
        try {
            const target = new URL(url, window.location.origin);
            if (target.origin !== window.location.origin || !target.pathname.toLowerCase().startsWith('/crm')) return;
            sessionStorage.setItem(cardTaskReturnKey, JSON.stringify({
                version: 1,
                cardId,
                url: target.pathname + target.search + target.hash,
                savedAt: Date.now()
            }));
        } catch { /* ignore */ }
    };

    const restoreBoardBackLinks = () => {
        const state = readStoredBoardState();
        if (!state || !state.boardUrl) return;

        try {
            const boardUrl = new URL(state.boardUrl, window.location.origin);
            if (boardUrl.origin !== window.location.origin) return;
            if (normalizeBoardPath(boardUrl.pathname) !== normalizeBoardPath(state.pathname)) return;
            const localUrl = boardUrl.pathname + boardUrl.search + boardUrl.hash;
            document.querySelectorAll('[data-crm-back-to-board]').forEach((link) => {
                link.setAttribute('href', localUrl);
                const label = link.querySelector('[data-crm-back-to-board-label]');
                if (label) {
                    label.textContent = (boardUrl.searchParams.get('scope') || '').toLowerCase() === 'closed'
                        ? 'К закрытым'
                        : 'К воронке';
                }
            });
        } catch { /* ignore */ }
    };

    const initClosedArchiveNavigation = () => {
        const archive = document.querySelector('.crm-closed[aria-label="Закрытые кандидаты"]');
        if (!archive || archive.dataset.crmClosedNavigationReady === 'true') return;
        archive.dataset.crmClosedNavigationReady = 'true';

        archive.addEventListener('click', (event) => {
            const openCard = event.target.closest('.crm-table-candidate__identity');
            if (!openCard) return;

            clearCardTaskReturn();
            writeBoardState({
                version: 1,
                pathname: window.location.pathname,
                boardUrl: window.location.pathname + window.location.search,
                scrollLeft: 0,
                jumpsScrollLeft: 0,
                windowScrollX: window.scrollX,
                windowScrollY: window.scrollY,
                stageScrollTops: {},
                focusStage: '',
                savedAt: Date.now()
            });
            markCardOpenAtTop();
        });

        const storedState = readBoardState();
        if (!storedState || !Number.isFinite(storedState.windowScrollY)) return;

        const restore = () => window.scrollTo({
            left: Number.isFinite(storedState.windowScrollX) ? Math.max(0, storedState.windowScrollX) : 0,
            top: Math.max(0, storedState.windowScrollY),
            behavior: 'auto'
        });
        restore();
        window.requestAnimationFrame(() => {
            restore();
            window.requestAnimationFrame(restore);
        });
    };

    if (!window.__orbitaCrmBoardResizeBound) {
        window.__orbitaCrmBoardResizeBound = true;
        window.addEventListener('resize', () => {
            refreshers.forEach((fn) => {
                try { fn(); } catch { /* ignore stale board */ }
            });
        });
    }

    const initBoardDragAndDrop = (root) => {
        const board = root.querySelector('[data-crm-board]');
        if (!board) return;

        const requiresStageComment = root.dataset.crmRequireStageComment === 'true';

        board.addEventListener('dragstart', (event) => {
            const tile = event.target.closest('.crm-tile[draggable="true"]');
            if (!tile || !event.dataTransfer) return;
            event.dataTransfer.effectAllowed = 'move';
            event.dataTransfer.setData('text/plain', tile.dataset.cardId || '');
            root.classList.add('is-dragging');
        });

        board.addEventListener('dragover', (event) => {
            if (!root.classList.contains('is-dragging')) return;
            const stage = event.target.closest('.crm-stage');
            if (!stage) return;
            const stageName = stage.getAttribute('data-stage');
            if (!stageName || !event.dataTransfer) return;
            event.preventDefault();
            event.dataTransfer.dropEffect = 'move';
            stage.classList.add('is-drop-target');
        });

        board.addEventListener('dragleave', (event) => {
            const stage = event.target.closest('.crm-stage');
            if (stage) stage.classList.remove('is-drop-target');
        });

        board.addEventListener('drop', (event) => {
            const stage = event.target.closest('.crm-stage');
            const stageName = stage ? stage.getAttribute('data-stage') : null;
            if (!stage || !stageName || !event.dataTransfer) return;
            event.preventDefault();
            const cardId = event.dataTransfer.getData('text/plain');
            const sourceTile = board.querySelector(`.crm-tile[data-card-id="${CSS.escape(cardId)}"]`);
            if (!cardId || !sourceTile) return;
            const sourceStage = sourceTile.dataset.cardStage;
            if (sourceStage === stageName) return;

            let comment = '';
            if (requiresStageComment) {
                comment = window.prompt('Добавьте комментарий к смене этапа:') || '';
                if (!comment.trim()) {
                    if (window.Orbita && typeof window.Orbita.toast === 'function') {
                        window.Orbita.toast('Для смены этапа нужен комментарий.', { variant: 'error' });
                    }
                    return;
                }
                comment = comment.trim();
            }

            if (window.Orbita && typeof window.Orbita.postForm === 'function') {
                sourceTile.setAttribute('aria-busy', 'true');
                window.Orbita.postForm('/Crm/MoveAjax', { id: cardId, stage: stageName, comment: comment })
                    .then(function (result) {
                        if (!result.ok || !result.payload || result.payload.ok !== true) {
                            var message = (result.payload && result.payload.error) || 'Не удалось сменить этап.';
                            if (window.Orbita && typeof window.Orbita.toast === 'function') {
                                window.Orbita.toast(message, { variant: 'error' });
                            }
                            return;
                        }

                        if (window.Orbita && typeof window.Orbita.toast === 'function') {
                            window.Orbita.toast('Карточка перемещена.', { variant: 'success' });
                        }
                        return refreshBoard().catch(function () {
                            if (window.Orbita && typeof window.Orbita.toast === 'function') {
                                window.Orbita.toast('Этап сохранён, но доска не обновилась. Обновите страницу.', { variant: 'error' });
                            }
                        });
                    })
                    .catch(function () {
                        if (window.Orbita && typeof window.Orbita.toast === 'function') {
                            window.Orbita.toast('Не удалось переместить карточку. Проверьте соединение и повторите.', { variant: 'error' });
                        }
                    })
                    .finally(function () {
                        if (sourceTile.isConnected) sourceTile.removeAttribute('aria-busy');
                    });
            } else if (window.Orbita && typeof window.Orbita.toast === 'function') {
                window.Orbita.toast('Не удалось отправить смену этапа. Обновите страницу.', { variant: 'error' });
            }
        });

        const endDrag = () => {
            root.classList.remove('is-dragging');
            root.querySelectorAll('.crm-stage.is-drop-target').forEach((stage) => stage.classList.remove('is-drop-target'));
        };
        board.addEventListener('dragend', endDrag);
        board.addEventListener('dragcancel', endDrag);
    };

    const refreshBoard = () => {
        var fetcher = window.OrbitaLiveShared;
        if (fetcher && fetcher.createSnapshotFetcher && crmSnapshotFetcher) {
            return crmSnapshotFetcher.fetchSnapshot();
        }
        var root = document.querySelector('[data-orbita-live]');
        var liveWorkspace = document.querySelector('[data-crm-live-workspace]');
        if (!root || !liveWorkspace) return Promise.resolve();
        var url = root.getAttribute('data-orbita-snapshot');
        if (!url) return Promise.resolve();
        return fetch(url, {
            credentials: 'same-origin',
            headers: {
                'X-Orbita-Content-Only': '1',
                'X-Orbita-Snapshot': 'crm'
            }
        })
            .then(function (res) {
                if (res.status === 204) return null;
                if (!res.ok) throw new Error('Crm board snapshot failed: ' + res.status);
                return res.text();
            })
            .then(function (html) {
                if (!html || !html.trim()) return;
                applyCrmBoardHtml(html);
            });
    };

    const isManualCreateModalOpen = () => Array.from(
        document.querySelectorAll('[data-crm-manual-create-modal], [data-crm-lead-import-modal]')
    ).some((modal) => !modal.hidden);

    const applyCrmBoardHtml = (html) => {
        var liveWorkspace = document.querySelector('[data-crm-live-workspace]');
        if (!liveWorkspace || !html || !String(html).trim()) return;
        // A live snapshot replaces the entire CRM workspace. Do not destroy an
        // open form while the user is entering a candidate; the next scheduled
        // refresh will apply normally after the modal is closed or submitted.
        if (isManualCreateModalOpen()) return;
        liveWorkspace.innerHTML = html;
        initCrmBoardPage();
    };

    var crmSnapshotFetcher = window.OrbitaLiveShared && window.OrbitaLiveShared.createSnapshotFetcher
        ? window.OrbitaLiveShared.createSnapshotFetcher('crm', applyCrmBoardHtml, {
            errorName: 'Crm board',
            asText: true,
            headers: {
                'X-Orbita-Content-Only': '1',
                'X-Orbita-Snapshot': 'crm'
            }
        })
        : null;

    const initCrmStagePaging = (root) => {
        root.querySelectorAll('[data-crm-stage-cards]').forEach((cards) => {
            if (cards.dataset.crmStagePagingReady === 'true') return;
            cards.dataset.crmStagePagingReady = 'true';

            const pageUrl = cards.dataset.crmStagePageUrl || '';
            const loader = cards.querySelector('[data-crm-stage-loader]');
            let nextPage = Math.max(2, Number(cards.dataset.crmStageNextPage) || 2);
            let total = Math.max(0, Number(cards.dataset.crmStageTotal) || 0);
            let loading = false;
            let exhausted = cards.querySelectorAll('.crm-tile').length >= total || !pageUrl;

            const updateLoader = (message) => {
                if (!loader) return;
                loader.hidden = exhausted;
                loader.classList.toggle('is-loading', loading);
                const label = loader.querySelector('span');
                if (label && message) label.textContent = message;
            };

            const nearEnd = () => cards.scrollHeight - cards.scrollTop - cards.clientHeight < 320;

            const loadNext = async () => {
                if (loading || exhausted || !nearEnd()) return;
                loading = true;
                updateLoader('Загружаем карточки…');
                try {
                    const url = new URL(pageUrl, window.location.origin);
                    url.searchParams.set('page', String(nextPage));
                    const response = await fetch(url.toString(), {
                        credentials: 'same-origin',
                        headers: { Accept: 'text/html' }
                    });
                    if (response.status === 204) {
                        exhausted = true;
                        return;
                    }
                    if (!response.ok) throw new Error('CRM stage page failed: ' + response.status);

                    const html = await response.text();
                    if (!html.trim()) {
                        exhausted = true;
                        return;
                    }

                    const template = document.createElement('template');
                    template.innerHTML = html;
                    const existingIds = new Set(Array.from(cards.querySelectorAll('.crm-tile[data-card-id]'))
                        .map((tile) => tile.dataset.cardId)
                        .filter(Boolean));
                    const incoming = Array.from(template.content.querySelectorAll('.crm-tile'));
                    let appended = 0;
                    incoming.forEach((tile) => {
                        const cardId = tile.dataset.cardId || '';
                        if (cardId && existingIds.has(cardId)) return;
                        if (cardId) existingIds.add(cardId);
                        cards.insertBefore(tile, loader || null);
                        appended++;
                    });

                    nextPage++;
                    cards.dataset.crmStageNextPage = String(nextPage);
                    cards.dataset.crmStageLoaded = String(existingIds.size);
                    exhausted = existingIds.size >= total || appended === 0;
                    cards.closest('.crm-stage')?.classList.toggle(
                        'has-unassigned',
                        !!cards.querySelector('.crm-tile[data-card-manager-user-id=""]'));
                    root.dispatchEvent(new CustomEvent('orbita:crm-stage-appended', { bubbles: true }));
                } catch (error) {
                    console.warn('CRM stage paging:', error);
                    updateLoader('Не удалось загрузить. Прокрутите ещё раз.');
                } finally {
                    loading = false;
                    updateLoader('Загрузим дальше при прокрутке');
                    if (!exhausted && nearEnd()) {
                        window.requestAnimationFrame(loadNext);
                    }
                }
            };

            cards.addEventListener('scroll', loadNext, { passive: true });
            updateLoader('Загрузим дальше при прокрутке');
            window.requestAnimationFrame(loadNext);
        });
    };

    const initBoardNavigation = (root) => {
        if (root.dataset.navigationReady === 'true') return;

        const viewport = root.querySelector('[data-crm-board-scroll]');
        const boardViewport = root.querySelector('[data-crm-board-viewport]');
        const prevButtons = [...root.querySelectorAll('[data-crm-board-prev]')];
        const nextButtons = [...root.querySelectorAll('[data-crm-board-next]')];
        const jumps = [...root.querySelectorAll('[data-crm-board-jump]')];
        const jumpsStrip = root.querySelector('[data-crm-board-jumps]');
        const rangeLabel = root.querySelector('[data-crm-board-range]');
        if (!viewport || prevButtons.length === 0 || nextButtons.length === 0) return;

        root.dataset.navigationReady = 'true';

        const stages = () => [...root.querySelectorAll('.crm-stage')];

        const getPaddingLeft = () => {
            const style = window.getComputedStyle(viewport);
            return parseFloat(style.paddingLeft) || 0;
        };

        const scrollToLeft = (left) => {
            viewport.scrollTo({ left: Math.max(0, left), behavior: scrollBehavior() });
        };

        const scrollStageIntoView = (stage) => {
            if (!stage) return;
            const target = stage.offsetLeft - getPaddingLeft();
            scrollToLeft(target);
        };

        const findNextStage = (direction) => {
            const list = stages();
            if (list.length === 0) return null;

            const pad = getPaddingLeft();
            const current = viewport.scrollLeft;

            if (direction > 0) {
                return list.find((stage) => stage.offsetLeft > current + pad + 24) || list[list.length - 1];
            }

            const reverse = [...list].reverse();
            return reverse.find((stage) => stage.offsetLeft < current + pad - 24) || list[0];
        };

        const move = (direction) => {
            const target = findNextStage(direction);
            scrollStageIntoView(target);
        };

        const updateControls = () => {
            // Board may have been removed by SPA content swap
            if (!root.isConnected) {
                refreshers.delete(updateControls);
                return;
            }

            const maxScroll = Math.max(0, viewport.scrollWidth - viewport.clientWidth);
            const canScroll = maxScroll > 4;
            const atStart = viewport.scrollLeft <= 4;
            const atEnd = viewport.scrollLeft >= maxScroll - 4;

            root.classList.toggle('is-scrollable', canScroll);
            root.classList.toggle('can-scroll-left', canScroll && !atStart);
            root.classList.toggle('can-scroll-right', canScroll && !atEnd);

            if (boardViewport) {
                boardViewport.classList.toggle('can-scroll-left', canScroll && !atStart);
                boardViewport.classList.toggle('can-scroll-right', canScroll && !atEnd);
            }

            prevButtons.forEach((btn) => {
                btn.disabled = !canScroll || atStart;
                if (btn.classList.contains('crm-board-edge')) {
                    btn.hidden = !canScroll || atStart;
                }
            });
            nextButtons.forEach((btn) => {
                btn.disabled = !canScroll || atEnd;
                if (btn.classList.contains('crm-board-edge')) {
                    btn.hidden = !canScroll || atEnd;
                }
            });

            const list = stages();
            const viewLeft = viewport.scrollLeft;
            const viewRight = viewLeft + viewport.clientWidth;

            let bestVisibility = -1;
            let activeIndex = 0;
            const visibleIndexes = [];
            const visibleStages = [];

            list.forEach((stage, index) => {
                const left = stage.offsetLeft;
                const right = left + stage.offsetWidth;
                const visibleLeft = Math.max(left, viewLeft);
                const visibleRight = Math.min(right, viewRight);
                const visible = Math.max(0, visibleRight - visibleLeft);
                const ratio = visible / Math.max(1, stage.offsetWidth);
                if (ratio > 0) {
                    visibleStages.push({
                        index,
                        start: (visibleLeft - left) / Math.max(1, stage.offsetWidth),
                        end: (visibleRight - left) / Math.max(1, stage.offsetWidth)
                    });
                }
                if (ratio > 0.4) visibleIndexes.push(index);
                if (ratio > bestVisibility) {
                    bestVisibility = ratio;
                    activeIndex = index;
                }
            });

            const firstVisible = visibleIndexes.length > 0 ? visibleIndexes[0] : activeIndex;
            const lastVisible = visibleIndexes.length > 0 ? visibleIndexes[visibleIndexes.length - 1] : activeIndex;

            jumps.forEach((jump, index) => {
                const isActive = index === activeIndex;
                jump.classList.toggle('is-active', isActive);
                jump.setAttribute('aria-selected', isActive ? 'true' : 'false');
            });

            if (jumpsStrip && visibleStages.length > 0) {
                const first = visibleStages[0];
                const last = visibleStages[visibleStages.length - 1];
                const firstJump = jumps[first.index];
                const lastJump = jumps[last.index];
                if (firstJump && lastJump) {
                    const start = firstJump.offsetLeft + firstJump.offsetWidth * first.start;
                    const end = lastJump.offsetLeft + lastJump.offsetWidth * last.end;
                    jumpsStrip.style.setProperty('--crm-jumps-visible-start', `${start}px`);
                    jumpsStrip.style.setProperty('--crm-jumps-visible-width', `${Math.max(0, end - start)}px`);
                    jumpsStrip.classList.add('has-visible-progress');
                }
            } else if (jumpsStrip) {
                jumpsStrip.classList.remove('has-visible-progress');
            }

            if (rangeLabel && list.length > 0) {
                if (!canScroll) {
                    rangeLabel.textContent = `Все ${list.length} этап(ов)`;
                } else if (firstVisible === lastVisible) {
                    rangeLabel.textContent = `Этап ${firstVisible + 1} из ${list.length}`;
                } else {
                    rangeLabel.textContent = `Этапы ${firstVisible + 1}–${lastVisible + 1} из ${list.length}`;
                }
            }
        };

        refreshers.add(updateControls);

        prevButtons.forEach((btn) => btn.addEventListener('click', () => move(-1)));
        nextButtons.forEach((btn) => btn.addEventListener('click', () => move(1)));

        jumps.forEach((jump) => {
            jump.addEventListener('click', () => {
                const index = Number(jump.dataset.stageIndex);
                const list = stages();
                scrollStageIntoView(list[index]);
            });
        });

        viewport.addEventListener('wheel', (event) => {
            const maxScroll = viewport.scrollWidth - viewport.clientWidth;
            if (maxScroll <= 4) return;

            const mostlyHorizontal = Math.abs(event.deltaX) > Math.abs(event.deltaY);
            if (mostlyHorizontal) {
                window.requestAnimationFrame(updateControls);
                return;
            }

            if (!event.shiftKey) return;

            event.preventDefault();
            viewport.scrollBy({ left: event.deltaY, behavior: 'auto' });
            updateControls();
        }, { passive: false });

        let drag = null;
        viewport.addEventListener('pointerdown', (event) => {
            if (event.button !== 0) return;
            if (event.target.closest('a, button, input, select, textarea, summary, label, form, .crm-tile')) return;
            if (viewport.scrollWidth <= viewport.clientWidth + 4) return;

            drag = {
                pointerId: event.pointerId,
                startX: event.clientX,
                startScroll: viewport.scrollLeft,
                moved: false
            };
            viewport.classList.add('is-dragging');
            try { viewport.setPointerCapture(event.pointerId); } catch { /* ignore */ }
        });

        viewport.addEventListener('pointermove', (event) => {
            if (!drag || drag.pointerId !== event.pointerId) return;
            const dx = event.clientX - drag.startX;
            if (Math.abs(dx) > 3) drag.moved = true;
            viewport.scrollLeft = drag.startScroll - dx;
            updateControls();
        });

        const endDrag = (event) => {
            if (!drag || drag.pointerId !== event.pointerId) return;
            viewport.classList.remove('is-dragging');
            try { viewport.releasePointerCapture(event.pointerId); } catch { /* ignore */ }
            drag = null;
        };
        viewport.addEventListener('pointerup', endDrag);
        viewport.addEventListener('pointercancel', endDrag);

        viewport.addEventListener('keydown', (event) => {
            if (event.key === 'ArrowLeft') {
                event.preventDefault();
                move(-1);
            } else if (event.key === 'ArrowRight') {
                event.preventDefault();
                move(1);
            } else if (event.key === 'Home') {
                event.preventDefault();
                scrollStageIntoView(stages()[0]);
            } else if (event.key === 'End') {
                event.preventDefault();
                const list = stages();
                scrollStageIntoView(list[list.length - 1]);
            }
        });

        viewport.addEventListener('scroll', () => window.requestAnimationFrame(updateControls), { passive: true });

        // Keep hovered tiles fully visible so their quick actions are never clipped.
        root.addEventListener('mouseover', (event) => {
            const tile = event.target.closest('.crm-tile');
            if (!tile) return;
            const cards = tile.closest('.crm-stage__cards');
            if (!cards) return;
            const tileRect = tile.getBoundingClientRect();
            const cardsRect = cards.getBoundingClientRect();
            const overshoot = tileRect.bottom - (cardsRect.bottom - 8);
            if (overshoot > 4 && cards.scrollHeight > cards.clientHeight) {
                cards.scrollTop += overshoot;
            }
        }, true);

        if (jumpsStrip) {
            const syncJumpScroll = () => {
                const active = jumpsStrip.querySelector('.crm-board-jump.is-active');
                if (!active) return;
                const left = active.offsetLeft - 12;
                const right = active.offsetLeft + active.offsetWidth + 12;
                if (left < jumpsStrip.scrollLeft) {
                    jumpsStrip.scrollTo({ left: left, behavior: scrollBehavior() });
                } else if (right > jumpsStrip.scrollLeft + jumpsStrip.clientWidth) {
                    jumpsStrip.scrollTo({ left: right - jumpsStrip.clientWidth, behavior: scrollBehavior() });
                }
            };
            viewport.addEventListener('scroll', () => window.requestAnimationFrame(syncJumpScroll), { passive: true });
        }

        let storedBoardState = readBoardState();

        const resolveFocusStageName = () => {
            try {
                const params = new URLSearchParams(window.location.search || '');
                const fromQuery = (params.get('stage') || '').trim();
                if (fromQuery) return fromQuery;
            } catch { /* ignore */ }
            try {
                return (sessionStorage.getItem(focusStageKey) || '').trim();
            } catch {
                return '';
            }
        };

        const rememberFocusStage = (stageName) => {
            if (!stageName) return;
            try { sessionStorage.setItem(focusStageKey, stageName); } catch { /* ignore */ }
        };

        const captureStageScrollTops = () => {
            const positions = {};
            stages().forEach((stage) => {
                const stageName = (stage.getAttribute('data-stage') || '').trim();
                const cards = stage.querySelector('.crm-stage__cards');
                if (stageName && cards) positions[stageName] = cards.scrollTop;
            });
            return positions;
        };

        const restoreStageScrollTops = () => {
            const positions = storedBoardState?.stageScrollTops;
            if (!positions || typeof positions !== 'object') return;

            stages().forEach((stage) => {
                const stageName = (stage.getAttribute('data-stage') || '').trim();
                const cards = stage.querySelector('.crm-stage__cards');
                const savedTop = Number(positions[stageName]);
                if (!stageName || !cards || !Number.isFinite(savedTop)) return;
                const maxScroll = Math.max(0, cards.scrollHeight - cards.clientHeight);
                cards.scrollTop = Math.min(maxScroll, Math.max(0, savedTop));
            });
        };

        const rememberBoardPosition = (stageName) => {
            const nextState = {
                version: 1,
                pathname: window.location.pathname,
                boardUrl: window.location.pathname + window.location.search,
                scrollLeft: viewport.scrollLeft,
                jumpsScrollLeft: jumpsStrip ? jumpsStrip.scrollLeft : 0,
                windowScrollX: window.scrollX,
                windowScrollY: window.scrollY,
                stageScrollTops: captureStageScrollTops(),
                focusStage: stageName || storedBoardState?.focusStage || '',
                savedAt: Date.now()
            };
            storedBoardState = nextState;
            writeBoardState(nextState);
            if (nextState.focusStage) rememberFocusStage(nextState.focusStage);
        };

        const restoreBoardPosition = () => {
            if (!storedBoardState || !Number.isFinite(storedBoardState.scrollLeft)) return false;

            const maxScroll = Math.max(0, viewport.scrollWidth - viewport.clientWidth);
            viewport.scrollTo({
                left: Math.min(maxScroll, Math.max(0, storedBoardState.scrollLeft)),
                behavior: 'auto'
            });
            if (jumpsStrip && Number.isFinite(storedBoardState.jumpsScrollLeft)) {
                jumpsStrip.scrollLeft = Math.max(0, storedBoardState.jumpsScrollLeft);
            }
            restoreStageScrollTops();
            if (Number.isFinite(storedBoardState.windowScrollY)) {
                window.scrollTo({
                    left: Number.isFinite(storedBoardState.windowScrollX) ? storedBoardState.windowScrollX : 0,
                    top: Math.max(0, storedBoardState.windowScrollY),
                    behavior: 'auto'
                });
            }
            if (storedBoardState.focusStage) rememberFocusStage(storedBoardState.focusStage);
            updateControls();
            return true;
        };

        const restoreFocusStage = () => {
            const stageName = resolveFocusStageName();
            if (!stageName) return;
            const list = stages();
            const target = list.find((stage) => (stage.getAttribute('data-stage') || '') === stageName);
            if (!target) return;
            // Instant jump on first paint so managers land on the stage they left.
            viewport.scrollTo({ left: Math.max(0, target.offsetLeft - getPaddingLeft()), behavior: 'auto' });
            rememberFocusStage(stageName);
            updateControls();
        };

        root.addEventListener('click', (event) => {
            const openCard = event.target.closest('[data-crm-open-card]');
            if (!openCard) return;
            const stageName = openCard.getAttribute('data-crm-stage')
                || openCard.closest('.crm-stage')?.getAttribute('data-stage')
                || '';
            clearCardTaskReturn();
            rememberBoardPosition(stageName);
            markCardOpenAtTop();
        });

        let savePositionFrame = 0;
        const schedulePositionSave = () => {
            if (savePositionFrame) return;
            savePositionFrame = window.requestAnimationFrame(() => {
                savePositionFrame = 0;
                rememberBoardPosition('');
            });
        };
        viewport.addEventListener('scroll', schedulePositionSave, { passive: true });
        stages().forEach((stage) => {
            stage.querySelector('.crm-stage__cards')?.addEventListener('scroll', schedulePositionSave, { passive: true });
        });

        // Layout may settle after SPA swap / fonts; refresh a couple of frames later
        updateControls();
        if (!restoreBoardPosition()) restoreFocusStage();
        window.requestAnimationFrame(() => {
            updateControls();
            if (!restoreBoardPosition()) restoreFocusStage();
            window.requestAnimationFrame(() => {
                updateControls();
                if (!restoreBoardPosition()) restoreFocusStage();
            });
        });

        initBoardDragAndDrop(root);
    };

    const initCrmBulkActions = (root) => {
        if (root.dataset.crmBulkReady === 'true') return;

        const toolbar = root.querySelector('[data-crm-bulk-actions]');
        const getCheckboxes = () => Array.from(root.querySelectorAll('[data-crm-card-select]'));
        const forms = Array.from(root.querySelectorAll('[data-crm-bulk-form]'));
        const modal = root.querySelector('[data-crm-bulk-modal]');
        const selectVisible = root.querySelector('[data-crm-select-visible]');
        const stageSelectionButtons = Array.from(root.querySelectorAll('[data-crm-select-stage]'));
        if (!toolbar || (getCheckboxes().length === 0 && stageSelectionButtons.length === 0) || !modal) return;

        // CRM POST navigation swaps only .orbita-content. If a previous bulk
        // modal was replaced while open, its body scroll lock can survive the
        // swap even though the new modal starts hidden.
        if (modal.hidden) document.body.classList.remove('orbita-modal-open');

        root.dataset.crmBulkReady = 'true';
        const countLabels = Array.from(root.querySelectorAll('[data-crm-bulk-count], [data-crm-bulk-modal-count]'));
        const operationField = modal.querySelector('[data-crm-bulk-operation]');
        const stageField = modal.querySelector('[data-crm-bulk-stage-field]');
        const stageSelect = modal.querySelector('[data-crm-bulk-stage-select]');
        const closeField = modal.querySelector('[data-crm-bulk-close-field]');
        const closeSelect = modal.querySelector('[data-crm-bulk-close-select]');
        const stageTrigger = toolbar.querySelector('[data-crm-bulk-stage-trigger]');
        const assigneeSelect = toolbar.querySelector('[data-crm-bulk-assignee]');
        const modalTitle = modal.querySelector('[data-crm-bulk-modal-title]');
        const commentLabel = modal.querySelector('[data-crm-bulk-comment-label]');
        const comment = modal.querySelector('[data-crm-bulk-comment]');
        const submit = modal.querySelector('[data-crm-bulk-submit]');
        const stageSelectionLabel = toolbar.querySelector('[data-crm-bulk-stage-label]');
        const reasonRequired = root.dataset.crmBulkReasonRequired === 'true';
        const selectionContext = normalizeBoardUrl();

        const readPersistedSelection = () => {
            try {
                const state = JSON.parse(sessionStorage.getItem(bulkSelectionKey) || 'null');
                if (!state || ![1, 2].includes(state.version) || state.context !== selectionContext || !Array.isArray(state.ids)) {
                    return { ids: [], stage: '' };
                }
                if (!Number.isFinite(state.savedAt) || Date.now() - state.savedAt > 8 * 60 * 60 * 1000) {
                    return { ids: [], stage: '' };
                }
                return {
                    ids: state.ids.filter((id) => typeof id === 'string' && id),
                    stage: state.version === 2 && typeof state.stage === 'string' ? state.stage : ''
                };
            } catch {
                return { ids: [], stage: '' };
            }
        };

        const persistSelection = (ids, stage) => {
            try {
                if (ids.length === 0 && !stage) {
                    sessionStorage.removeItem(bulkSelectionKey);
                    return;
                }
                sessionStorage.setItem(bulkSelectionKey, JSON.stringify({
                    version: 2,
                    context: selectionContext,
                    ids,
                    stage,
                    savedAt: Date.now()
                }));
            } catch { /* ignore */ }
        };

        const initialCheckboxes = getCheckboxes();
        const availableIds = new Set(initialCheckboxes.map((checkbox) => checkbox.value).filter(Boolean));
        const persistedSelection = readPersistedSelection();
        const restoredIds = new Set(persistedSelection.ids.filter((id) => availableIds.has(id)));
        let selectedWholeStage = stageSelectionButtons.some(
            (button) => button.dataset.stageName === persistedSelection.stage
                      && Number.parseInt(button.dataset.totalCount || '0', 10) > 0)
            ? persistedSelection.stage
            : '';
        initialCheckboxes.forEach((checkbox) => { checkbox.checked = restoredIds.has(checkbox.value); });

        const selectedIds = () => selectedWholeStage ? [] : getCheckboxes()
            .filter((checkbox) => checkbox.checked)
            .map((checkbox) => checkbox.value)
            .filter(Boolean);

        const selectedWholeStageCount = () => {
            if (!selectedWholeStage) return 0;
            const button = stageSelectionButtons.find((item) => item.dataset.stageName === selectedWholeStage);
            return Math.max(0, Number.parseInt(button?.dataset.totalCount || '0', 10) || 0);
        };

        const syncFormCardIds = () => {
            const ids = selectedIds();
            forms.forEach((form) => {
                const container = form.querySelector('[data-crm-bulk-card-fields]');
                if (!container) return;
                const fields = ids.map((id) => {
                    const input = document.createElement('input');
                    input.type = 'hidden';
                    input.name = 'cardIds';
                    input.value = id;
                    return input;
                });
                if (selectedWholeStage) {
                    const input = document.createElement('input');
                    input.type = 'hidden';
                    input.name = 'allCardsInStage';
                    input.value = selectedWholeStage;
                    fields.push(input);
                }
                container.replaceChildren(...fields);
            });
            return ids;
        };

        const updateSelection = () => {
            const checkboxes = getCheckboxes();
            const ids = syncFormCardIds();
            const wholeStageCount = selectedWholeStageCount();
            const selectedCount = selectedWholeStage ? wholeStageCount : ids.length;
            countLabels.forEach((label) => { label.textContent = String(selectedCount); });
            toolbar.hidden = selectedCount === 0;
            if (stageSelectionLabel) {
                stageSelectionLabel.hidden = !selectedWholeStage;
                stageSelectionLabel.textContent = selectedWholeStage ? `весь этап «${selectedWholeStage}»` : '';
            }
            checkboxes.forEach((checkbox) => {
                if (selectedWholeStage) {
                    checkbox.checked = checkbox.closest('[data-card-stage]')?.dataset.cardStage === selectedWholeStage;
                }
                checkbox.closest('.crm-tile')?.classList.toggle('is-bulk-selected', checkbox.checked);
                checkbox.closest('tr')?.classList.toggle('is-bulk-selected', checkbox.checked);
            });
            stageSelectionButtons.forEach((button) => {
                const active = button.dataset.stageName === selectedWholeStage;
                button.classList.toggle('is-active', active);
                button.setAttribute('aria-pressed', active ? 'true' : 'false');
            });
            if (selectVisible) {
                selectVisible.checked = ids.length > 0 && ids.length === checkboxes.length;
                selectVisible.indeterminate = ids.length > 0 && ids.length < checkboxes.length;
            }
            persistSelection(ids, selectedWholeStage);

            if (assigneeSelect) {
                const selectedAssignees = selectedWholeStage ? [] : checkboxes
                    .filter((checkbox) => checkbox.checked)
                    .map((checkbox) => checkbox.closest('[data-card-manager-user-id]')?.dataset.cardManagerUserId || '');
                const commonAssignee = selectedAssignees.length > 0
                    && selectedAssignees[0]
                    && selectedAssignees.every((userId) => userId === selectedAssignees[0])
                    ? selectedAssignees[0]
                    : '';

                Array.from(assigneeSelect.options).forEach((option) => {
                    if (!option.value) return;
                    const isCurrentCommonAssignee = option.value === commonAssignee;
                    option.hidden = isCurrentCommonAssignee;
                    option.disabled = isCurrentCommonAssignee;
                });
                if (assigneeSelect.value === commonAssignee) assigneeSelect.value = '';
            }
            return selectedCount;
        };

        const closeModal = () => {
            modal.hidden = true;
            document.body.classList.remove('orbita-modal-open');
            if (stageTrigger) stageTrigger.value = '';
        };

        const openModal = (operation, selectedStage = '') => {
            const selectedCount = updateSelection();
            if (selectedCount === 0) return;

            const isClose = operation === 'close';
            if (operationField) operationField.value = isClose ? 'close' : 'move';
            if (stageField) stageField.hidden = isClose;
            if (stageSelect) {
                stageSelect.disabled = isClose;
                stageSelect.required = !isClose;
                if (!isClose && selectedStage) stageSelect.value = selectedStage;
            }
            if (closeField) closeField.hidden = !isClose;
            if (closeSelect) {
                closeSelect.disabled = !isClose;
                closeSelect.required = isClose;
            }
            if (modalTitle) modalTitle.textContent = isClose ? 'Закрыть выбранные карточки' : 'Сменить этап карточек';
            if (commentLabel) {
                commentLabel.textContent = (isClose ? 'Причина закрытия' : 'Причина смены этапа') + (reasonRequired ? ' *' : '');
            }
            if (comment) {
                comment.placeholder = reasonRequired ? 'Обязательно укажите причину' : 'Можно оставить пустым';
                comment.required = reasonRequired;
            }
            if (submit) {
                submit.innerHTML = isClose
                    ? '<i class="fa-solid fa-box-archive" aria-hidden="true"></i>Закрыть карточки'
                    : '<i class="fa-solid fa-check" aria-hidden="true"></i>Сменить этап';
                submit.classList.toggle('is-danger', isClose);
            }

            modal.hidden = false;
            document.body.classList.add('orbita-modal-open');
            window.setTimeout(() => (isClose ? closeSelect : stageSelect)?.focus(), 0);
        };

        root.addEventListener('change', (event) => {
            if (event.target.closest('[data-crm-card-select]')) {
                selectedWholeStage = '';
                updateSelection();
            }
        });
        root.addEventListener('click', (event) => {
            if (event.target.closest('[data-crm-card-select]')) event.stopPropagation();
        });
        root.addEventListener('dragstart', (event) => {
            if (event.target.closest('.crm-tile__select')) {
                event.preventDefault();
                event.stopPropagation();
            }
        });
        selectVisible?.addEventListener('change', () => {
            selectedWholeStage = '';
            getCheckboxes().forEach((checkbox) => { checkbox.checked = selectVisible.checked; });
            updateSelection();
        });
        selectVisible?.addEventListener('click', (event) => event.stopPropagation());

        toolbar.querySelector('[data-crm-bulk-clear]')?.addEventListener('click', () => {
            selectedWholeStage = '';
            getCheckboxes().forEach((checkbox) => { checkbox.checked = false; });
            updateSelection();
        });
        stageSelectionButtons.forEach((button) => {
            button.addEventListener('click', () => {
                const stage = button.dataset.stageName || '';
                selectedWholeStage = selectedWholeStage === stage ? '' : stage;
                if (!selectedWholeStage) {
                    getCheckboxes().forEach((checkbox) => { checkbox.checked = false; });
                }
                updateSelection();
            });
        });
        root.addEventListener('orbita:crm-stage-appended', updateSelection);
        root.querySelectorAll('[data-crm-bulk-transition-open]').forEach((button) => {
            button.addEventListener('click', () => openModal(button.dataset.crmBulkTransitionOpen || 'move'));
        });
        stageTrigger?.addEventListener('change', () => {
            if (!stageTrigger.value) return;
            openModal('move', stageTrigger.value);
        });
        modal.querySelectorAll('[data-crm-bulk-modal-close]').forEach((button) => {
            button.addEventListener('click', closeModal);
        });
        modal.addEventListener('keydown', (event) => {
            if (event.key === 'Escape') closeModal();
        });
        forms.forEach((form) => {
            form.addEventListener('submit', (event) => {
                if (updateSelection() === 0) {
                    event.preventDefault();
                    if (window.Orbita && typeof window.Orbita.toast === 'function') {
                        window.Orbita.toast('Выберите хотя бы одну карточку.', { variant: 'error' });
                    }
                    return;
                }

                if (form.hasAttribute('data-crm-bulk-transition-form')) closeModal();
            });
        });

        updateSelection();
    };

    const initCrmListControls = (form) => {
        if (form.dataset.crmPageSizeReady === 'true') return;
        const select = form.querySelector('[data-crm-page-size]');
        if (!select) return;
        form.dataset.crmPageSizeReady = 'true';
        select.addEventListener('change', () => form.requestSubmit());
    };

    const initCrmDateFilter = (input) => {
        if (input.dataset.crmDateFilterReady === 'true') return;
        input.dataset.crmDateFilterReady = 'true';
        input.addEventListener('click', () => {
            if (typeof input.showPicker !== 'function') return;
            try { input.showPicker(); } catch { /* preserve native date input behaviour */ }
        });
    };

    const initCrmFunnelEditor = (form) => {
        if (form.dataset.crmFunnelReady === 'true') return;

        const stages = form.querySelector('[data-crm-funnel-stages]');
        const valueField = form.querySelector('[data-crm-funnel-value]');
        const countField = form.querySelector('[data-crm-funnel-count]');
        const addButton = form.querySelector('[data-crm-funnel-action="add"]');
        if (!stages || !valueField || !addButton) return;

        form.dataset.crmFunnelReady = 'true';
        const minStages = Number.parseInt(form.dataset.minStages || '1', 10);
        const maxStages = Number.parseInt(form.dataset.maxStages || '20', 10);
        let draggedStage = null;

        const getStages = () => Array.from(stages.querySelectorAll('[data-crm-funnel-stage]'));
        const stageWord = (count) => {
            const mod100 = count % 100;
            const mod10 = count % 10;
            if (mod100 >= 11 && mod100 <= 14) return 'этапов';
            if (mod10 === 1) return 'этап';
            if (mod10 >= 2 && mod10 <= 4) return 'этапа';
            return 'этапов';
        };
        const makeControl = (action, label, icon, className) => {
            const button = document.createElement('button');
            button.type = 'button';
            button.dataset.crmFunnelAction = action;
            button.setAttribute('aria-label', label);
            button.title = label;
            if (className) button.className = className;
            button.innerHTML = '<i class="fa-solid ' + icon + '" aria-hidden="true"></i>';
            return button;
        };
        const createStage = (name) => {
            const stage = document.createElement('li');
            stage.className = 'crm-funnel-editor__stage';
            stage.dataset.crmFunnelStage = '';

            const number = document.createElement('span');
            number.className = 'crm-funnel-editor__number';
            number.dataset.crmFunnelNumber = '';

            const drag = document.createElement('button');
            drag.type = 'button';
            drag.className = 'crm-funnel-editor__drag';
            drag.draggable = true;
            drag.dataset.crmFunnelDrag = '';
            drag.setAttribute('aria-label', 'Перетащить этап');
            drag.title = 'Перетащить этап';
            drag.innerHTML = '<i class="fa-solid fa-grip-vertical" aria-hidden="true"></i>';

            const input = document.createElement('input');
            input.type = 'text';
            input.className = 'crm-funnel-editor__input';
            input.value = name;
            input.maxLength = 64;
            input.dataset.crmFunnelStageInput = '';

            const controls = document.createElement('div');
            controls.className = 'crm-funnel-editor__controls';
            controls.append(
                makeControl('move-up', 'Поднять этап', 'fa-arrow-up'),
                makeControl('move-down', 'Опустить этап', 'fa-arrow-down'),
                makeControl('remove', 'Удалить этап', 'fa-trash-can', 'is-danger')
            );

            stage.append(number, drag, input, controls);
            return stage;
        };
        const suggestedStageName = () => {
            const existing = new Set(getStages().map((stage) =>
                (stage.querySelector('[data-crm-funnel-stage-input]')?.value || '').trim().toLocaleLowerCase()));
            let suffix = 1;
            let candidate = 'Новый этап';
            while (existing.has(candidate.toLocaleLowerCase())) {
                suffix += 1;
                candidate = 'Новый этап ' + suffix;
            }
            return candidate;
        };
        const sync = () => {
            const stageRows = getStages();
            valueField.value = stageRows
                .map((stage) => stage.querySelector('[data-crm-funnel-stage-input]')?.value.trim() || '')
                .filter(Boolean)
                .join('\n');

            stageRows.forEach((stage, index) => {
                const position = index + 1;
                const input = stage.querySelector('[data-crm-funnel-stage-input]');
                const number = stage.querySelector('[data-crm-funnel-number]');
                const drag = stage.querySelector('[data-crm-funnel-drag]');
                const up = stage.querySelector('[data-crm-funnel-action="move-up"]');
                const down = stage.querySelector('[data-crm-funnel-action="move-down"]');
                const remove = stage.querySelector('[data-crm-funnel-action="remove"]');
                if (number) number.textContent = String(position).padStart(2, '0');
                if (input) input.setAttribute('aria-label', 'Название этапа ' + position);
                if (drag) {
                    drag.setAttribute('aria-label', 'Перетащить этап ' + position);
                    drag.title = 'Перетащить этап ' + position;
                }
                if (up) up.disabled = index === 0;
                if (down) down.disabled = index === stageRows.length - 1;
                if (remove) remove.disabled = stageRows.length <= minStages;
            });

            addButton.disabled = stageRows.length >= maxStages;
            if (countField) countField.textContent = stageRows.length + ' ' + stageWord(stageRows.length);
        };

        form.addEventListener('input', (event) => {
            if (event.target.matches('[data-crm-funnel-stage-input]')) sync();
        });

        form.addEventListener('click', (event) => {
            const button = event.target.closest('[data-crm-funnel-action]');
            if (!button) return;
            const action = button.dataset.crmFunnelAction;
            if (action === 'add') {
                if (getStages().length >= maxStages) return;
                const stage = createStage(suggestedStageName());
                stages.append(stage);
                sync();
                stage.querySelector('[data-crm-funnel-stage-input]')?.focus();
                return;
            }

            const stage = button.closest('[data-crm-funnel-stage]');
            if (!stage) return;
            if (action === 'remove' && getStages().length > minStages) {
                stage.remove();
            } else if (action === 'move-up' && stage.previousElementSibling) {
                stage.previousElementSibling.before(stage);
            } else if (action === 'move-down' && stage.nextElementSibling) {
                stage.nextElementSibling.after(stage);
            }
            sync();
        });

        form.addEventListener('dragstart', (event) => {
            const handle = event.target.closest('[data-crm-funnel-drag]');
            if (!handle) return;
            draggedStage = handle.closest('[data-crm-funnel-stage]');
            if (!draggedStage) return;
            draggedStage.classList.add('is-dragging');
            event.dataTransfer?.setData('text/plain', 'crm-funnel-stage');
            if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move';
        });
        form.addEventListener('dragend', () => {
            if (!draggedStage) return;
            draggedStage.classList.remove('is-dragging');
            draggedStage = null;
            sync();
        });
        stages.addEventListener('dragover', (event) => {
            if (!draggedStage) return;
            const target = event.target.closest('[data-crm-funnel-stage]');
            if (!target || target === draggedStage) return;
            event.preventDefault();
            const bounds = target.getBoundingClientRect();
            target.insertAdjacentElement(event.clientX < bounds.left + bounds.width / 2 ? 'beforebegin' : 'afterend', draggedStage);
        });
        stages.addEventListener('drop', (event) => {
            if (!draggedStage) return;
            event.preventDefault();
            sync();
        });

        sync();
    };

    const initManualCreateModal = () => {
        document.querySelectorAll('[data-crm-manual-create-modal]').forEach((modal) => {
            if (modal.dataset.manualCreateBound === 'true') return;
            modal.dataset.manualCreateBound = 'true';

            const closeModal = () => {
                modal.hidden = true;
                document.body.classList.remove('orbita-modal-open');
            };
            const openModal = () => {
                modal.hidden = false;
                document.body.classList.add('orbita-modal-open');
                window.setTimeout(() => modal.querySelector('input[name="fullName"]')?.focus(), 0);
            };

            document.querySelectorAll('[data-crm-manual-create-open]').forEach((trigger) => {
                trigger.addEventListener('click', openModal);
            });
            modal.querySelectorAll('[data-crm-manual-create-close]').forEach((trigger) => {
                trigger.addEventListener('click', closeModal);
            });
            modal.addEventListener('keydown', (event) => {
                if (event.key === 'Escape') closeModal();
            });
        });
    };

    const initLeadImportModal = () => {
        document.querySelectorAll('[data-crm-lead-import-modal]').forEach((modal) => {
            if (modal.dataset.leadImportBound === 'true') return;
            modal.dataset.leadImportBound = 'true';

            const form = modal.querySelector('[data-crm-lead-import-form]');
            const fileInput = modal.querySelector('[data-crm-lead-import-file]');
            const status = modal.querySelector('[data-crm-lead-import-status]');
            const submit = modal.querySelector('[data-crm-lead-import-submit]');
            if (!form || !fileInput || !status || !submit) return;

            const initialStatus = status.textContent.trim();
            let validatingFile = 0;
            let submitting = false;

            const setStatus = (message, variant) => {
                status.textContent = message;
                status.classList.toggle('is-success', variant === 'success');
                status.classList.toggle('is-error', variant === 'error');
            };
            const resetValidation = () => {
                validatingFile += 1;
                submit.disabled = true;
                submit.removeAttribute('aria-busy');
                setStatus(initialStatus, 'default');
            };
            const closeModal = () => {
                if (submitting) return;
                modal.hidden = true;
                document.body.classList.remove('orbita-modal-open');
                form.reset();
                resetValidation();
            };
            const openModal = () => {
                modal.hidden = false;
                document.body.classList.add('orbita-modal-open');
                window.setTimeout(() => fileInput.focus(), 0);
            };

            const normalizePhone = (value) => {
                const raw = String(value || '').trim();
                if (!/^\+?[\d\s()\-]+$/.test(raw)) return '';
                let digits = raw.replace(/\D/g, '');
                if (digits.length === 10 && digits[0] === '9') digits = `7${digits}`;
                if (digits.length === 11 && digits[0] === '8') digits = `7${digits.slice(1)}`;
                return digits.length === 11 && digits[0] === '7' ? digits : '';
            };
            const inspectText = (text) => {
                const tokens = String(text || '')
                    .split(/\r?\n/)
                    .map((line) => line.trim())
                    .filter(Boolean);
                const phones = new Set();
                let duplicates = 0;
                for (let index = 0; index < tokens.length - 1; index += 1) {
                    const phone = normalizePhone(tokens[index + 1]);
                    if (!phone || normalizePhone(tokens[index])) continue;
                    if (phones.has(phone)) duplicates += 1;
                    else phones.add(phone);
                    index += 1;
                }
                return { count: phones.size, duplicates };
            };
            const validateFile = async () => {
                const validationId = ++validatingFile;
                submit.disabled = true;
                const file = fileInput.files && fileInput.files[0];
                if (!file) {
                    setStatus(initialStatus, 'default');
                    return;
                }
                if (file.size > 2 * 1024 * 1024) {
                    setStatus('Файл больше 2 МБ. Выберите файл меньшего размера.', 'error');
                    return;
                }
                if (!file.name.toLowerCase().endsWith('.txt')) {
                    setStatus('Поддерживаются только текстовые файлы .txt.', 'error');
                    return;
                }

                setStatus('Проверяем файл…', 'default');
                try {
                    const result = inspectText(await file.text());
                    if (validationId !== validatingFile) return;
                    if (result.count === 0) {
                        setStatus('Лиды не распознаны. Проверьте, что после ФИО указан телефон.', 'error');
                        return;
                    }
                    if (result.count > 1000) {
                        setStatus(`Распознано ${result.count} лидов. Максимум за одну загрузку — 1000.`, 'error');
                        return;
                    }
                    const duplicateText = result.duplicates > 0
                        ? ` Дублей внутри файла: ${result.duplicates} — они будут пропущены.`
                        : '';
                    setStatus(`Распознано уникальных лидов: ${result.count}.${duplicateText}`, 'success');
                    submit.disabled = false;
                } catch {
                    if (validationId === validatingFile) {
                        setStatus('Не удалось прочитать файл. Сохраните его как обычный UTF-8 .txt.', 'error');
                    }
                }
            };

            document.querySelectorAll('[data-crm-lead-import-open]').forEach((trigger) => {
                trigger.addEventListener('click', openModal);
            });
            modal.querySelectorAll('[data-crm-lead-import-close]').forEach((trigger) => {
                trigger.addEventListener('click', closeModal);
            });
            modal.addEventListener('keydown', (event) => {
                if (event.key === 'Escape') closeModal();
            });
            fileInput.addEventListener('change', validateFile);
            form.addEventListener('submit', (event) => {
                if (submit.disabled || submitting) {
                    event.preventDefault();
                    return;
                }
                submitting = true;
                submit.disabled = true;
                submit.setAttribute('aria-busy', 'true');
                submit.innerHTML = '<i class="fa-solid fa-spinner fa-spin" aria-hidden="true"></i>Импортируем…';
            });
        });
    };

    const initResponsibleAutoFilter = () => {
        document.querySelectorAll('[data-crm-responsible-auto-filter]').forEach((select) => {
            if (select.dataset.crmResponsibleAutoFilterReady === 'true') return;
            select.dataset.crmResponsibleAutoFilterReady = 'true';

            select.addEventListener('change', () => {
                const url = new URL(window.location.href);
                url.searchParams.set('managerUserId', select.value.trim());
                rememberResponsibleFilterScroll(url.toString());
                select.disabled = true;
                select.setAttribute('aria-busy', 'true');
                window.location.assign(url.toString());
            });
        });
    };

    const initStageAutoFilter = () => {
        document.querySelectorAll('[data-crm-stage-auto-filter]').forEach((select) => {
            if (select.dataset.crmStageAutoFilterReady === 'true') return;
            select.dataset.crmStageAutoFilterReady = 'true';

            select.addEventListener('change', () => {
                const form = select.closest('form');
                if (!form) return;
                select.setAttribute('aria-busy', 'true');
                form.requestSubmit();
            });
        });
    };

    const initScopeTabPositionReset = () => {
        const boardScopes = new Set(['mine', 'team', 'unassigned']);

        document.querySelectorAll('.crm-scope-tabs a[href]').forEach((link) => {
            if (link.dataset.crmScopePositionResetReady === 'true') return;

            let scope = '';
            try {
                scope = (new URL(link.href, window.location.origin).searchParams.get('scope') || '').toLowerCase();
            } catch {
                return;
            }
            if (!boardScopes.has(scope)) return;

            link.dataset.crmScopePositionResetReady = 'true';
            link.addEventListener('click', () => {
                try {
                    sessionStorage.removeItem(boardStateKey);
                    sessionStorage.removeItem(focusStageKey);
                } catch { /* ignore */ }
            });
        });
    };

    const initCrmCardPage = () => {
        const page = document.querySelector('.crm-card-page');
        if (!page || page.dataset.crmCardReady === 'true') return;
        page.dataset.crmCardReady = 'true';

        if (consumeCardOpenAtTop()) {
            const scrollToCardTop = () => window.scrollTo({ left: 0, top: 0, behavior: 'auto' });
            scrollToCardTop();
            window.requestAnimationFrame(() => {
                scrollToCardTop();
                window.requestAnimationFrame(scrollToCardTop);
            });
        }

        const cardId = (page.getAttribute('data-crm-card-id') || '').trim();
        const taskBackLink = page.querySelector('[data-crm-back-to-tasks]');
        const boardBackLink = page.querySelector('[data-crm-back-to-board]');
        if (taskBackLink) {
            rememberCardTaskReturn(cardId, taskBackLink.getAttribute('href'));
        } else if (boardBackLink) {
            const storedReturn = readCardTaskReturn(cardId);
            if (storedReturn) {
                boardBackLink.setAttribute('href', storedReturn.url);
                boardBackLink.removeAttribute('data-crm-back-to-board');
                boardBackLink.setAttribute('data-crm-back-to-tasks', '');
                boardBackLink.innerHTML = '<i class="fa-solid fa-arrow-left" aria-hidden="true"></i>К задачам';
            }
        }

        const stageCommentInput = page.querySelector('#stageComment');
        if (stageCommentInput) {
            page.querySelectorAll('[data-stage-comment]').forEach((hidden) => {
                const form = hidden.closest('form');
                if (!form) return;
                form.addEventListener('submit', (event) => {
                    const comment = stageCommentInput.value.trim();
                    if (!comment) {
                        event.preventDefault();
                        stageCommentInput.focus();
                        if (window.Orbita && typeof window.Orbita.toast === 'function') {
                            window.Orbita.toast('Для смены этапа нужен комментарий.', { variant: 'error' });
                        }
                        return;
                    }
                    hidden.value = comment;
                });
            });
        }

        const cardEditForm = page.querySelector('[data-crm-card-inline-edit]');
        const cardEditToggle = page.querySelector('[data-crm-card-inline-edit-toggle]');
        const cardEditCancel = page.querySelector('[data-crm-card-inline-edit-cancel]');
        if (cardEditForm && cardEditToggle) {
            const setCardEditing = (editing) => {
                cardEditForm.classList.toggle('is-editing', editing);
                cardEditToggle.setAttribute('aria-expanded', editing ? 'true' : 'false');
                if (editing) {
                    const nameInput = cardEditForm.querySelector('[name="fullName"]');
                    if (nameInput) {
                        nameInput.focus();
                        nameInput.select();
                    }
                } else {
                    cardEditForm.reset();
                }
            };

            cardEditToggle.addEventListener('click', () => setCardEditing(true));
            if (cardEditCancel) {
                cardEditCancel.addEventListener('click', () => setCardEditing(false));
            }
        }

        const returnStage = (page.querySelector('[data-crm-back-to-board]')?.getAttribute('data-crm-stage') || '').trim();
        if (returnStage) {
            try { sessionStorage.setItem(focusStageKey, returnStage); } catch { /* ignore */ }
        }

        const thread = page.querySelector('[data-crm-chat-thread][data-mark-read="1"]');
        if (thread) {
            thread.setAttribute('data-mark-read', '0');
            const cardId = thread.getAttribute('data-card-id');
            if (cardId && window.Orbita && typeof window.Orbita.postForm === 'function') {
                window.Orbita.postForm('/Crm/MarkChatRead', { id: cardId }).catch(function () { });
            }
        }
    };

    const initCrmBoardPage = () => {
        // Drop refreshers for boards removed by content swap
        refreshers.forEach((fn) => {
            try { fn(); } catch { refreshers.delete(fn); }
        });

        document.querySelectorAll('[data-crm-board-carousel]').forEach(initBoardNavigation);
        document.querySelectorAll('[data-crm-board-carousel]').forEach(initCrmStagePaging);
        document.querySelectorAll('[data-crm-bulk-board="true"]').forEach(initCrmBulkActions);
        document.querySelectorAll('[data-crm-page-size-form]').forEach(initCrmListControls);
        document.querySelectorAll('[data-crm-date-filter]').forEach(initCrmDateFilter);
        document.querySelectorAll('[data-crm-funnel-editor]').forEach(initCrmFunnelEditor);
        initClosedArchiveNavigation();
        initResponsibleAutoFilter();
        initStageAutoFilter();
        initScopeTabPositionReset();
        restoreResponsibleFilterScroll();
        initManualCreateModal();
        initLeadImportModal();
        initCrmCardPage();
        restoreBoardBackLinks();

        if (window.OrbitaLiveShared && window.OrbitaLiveShared.registerLivePage) {
            window.OrbitaLiveShared.registerLivePage('crm', crmSnapshotFetcher);
        } else if (window.OrbitaLive && typeof window.OrbitaLive.register === 'function'
            && document.querySelector('[data-orbita-live][data-orbita-live-page="crm"]')) {
            window.OrbitaLive.register('crm', { fetchSnapshot: refreshBoard });
        }
    };

    window.OrbitaCrmBoard = window.OrbitaCrmBoard || {};
    window.OrbitaCrmBoard.init = initCrmBoardPage;

    initCrmBoardPage();
    document.addEventListener('orbita:content-updated', initCrmBoardPage);
})();
