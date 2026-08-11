(() => {
    const reducedMotion = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const scrollBehavior = () => (reducedMotion() ? 'auto' : 'smooth');

    const refreshers = new Set();
    const boardStateKey = 'orbita.crm.board.position.v1';
    const cardTaskReturnKey = 'orbita.crm.card.taskReturn.v1';

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

    const clearCardTaskReturn = () => {
        try { sessionStorage.removeItem(cardTaskReturnKey); } catch { /* ignore */ }
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
            });
        } catch { /* ignore */ }
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
        var root = document.querySelector('[data-orbita-live]');
        var liveWorkspace = document.querySelector('[data-crm-live-workspace]');
        if (!root || !liveWorkspace) return Promise.resolve();
        var url = root.getAttribute('data-orbita-snapshot');
        if (!url) return Promise.resolve();
        return fetch(url, { credentials: 'same-origin', headers: { 'X-Orbita-Content-Only': '1' } })
            .then(function (res) {
                if (res.status === 204) return null;
                if (!res.ok) throw new Error('Crm board snapshot failed: ' + res.status);
                return res.text();
            })
            .then(function (html) {
                if (!html || !html.trim()) return;
                liveWorkspace.innerHTML = html;
                initCrmBoardPage();
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

        const focusStageKey = 'orbita.crm.board.focusStage';
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

    const initCrmCardPage = () => {
        const page = document.querySelector('.crm-card-page');
        if (!page || page.dataset.crmCardReady === 'true') return;
        page.dataset.crmCardReady = 'true';

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
            try { sessionStorage.setItem('orbita.crm.board.focusStage', returnStage); } catch { /* ignore */ }
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
        document.querySelectorAll('[data-crm-funnel-editor]').forEach(initCrmFunnelEditor);
        initManualCreateModal();
        initCrmCardPage();
        restoreBoardBackLinks();

        if (window.OrbitaLive && typeof window.OrbitaLive.register === 'function'
            && document.querySelector('[data-orbita-live][data-orbita-live-page="crm"]')) {
            window.OrbitaLive.register('crm', { fetchSnapshot: refreshBoard });
        }
    };

    window.OrbitaCrmBoard = window.OrbitaCrmBoard || {};
    window.OrbitaCrmBoard.init = initCrmBoardPage;

    initCrmBoardPage();
    document.addEventListener('orbita:content-updated', initCrmBoardPage);
})();
