(() => {
    const reducedMotion = () => window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    const scrollBehavior = () => (reducedMotion() ? 'auto' : 'smooth');

    const refreshers = new Set();

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
            }

            if (window.Orbita && typeof window.Orbita.postForm === 'function') {
                window.Orbita.postForm('/Crm/MoveAjax', { id: cardId, stage: stageName, comment: comment })
                    .then(function (result) {
                        if (!result.ok || !result.payload || result.payload.ok !== true) {
                            var message = (result.payload && result.payload.error) || 'Не удалось сменить этап.';
                            if (window.Orbita && typeof window.Orbita.toast === 'function') {
                                window.Orbita.toast(message, { variant: 'error' });
                            }
                            return;
                        }
                        if (window.OrbitaLive && typeof window.OrbitaLive.scheduleRefresh === 'function') {
                            window.OrbitaLive.scheduleRefresh({ kinds: ['Crm'] });
                        } else {
                            refreshBoard();
                        }
                    })
                    .catch(function () { });
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

        // Layout may settle after SPA swap / fonts; refresh a couple of frames later
        updateControls();
        window.requestAnimationFrame(() => {
            updateControls();
            window.requestAnimationFrame(updateControls);
        });

        initBoardDragAndDrop(root);
    };

    const initCrmBoardPage = () => {
        // Drop refreshers for boards removed by content swap
        refreshers.forEach((fn) => {
            try { fn(); } catch { refreshers.delete(fn); }
        });

        document.querySelectorAll('[data-crm-board-carousel]').forEach(initBoardNavigation);

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
