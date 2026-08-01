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

    const initBoardNavigation = (root) => {
        if (root.dataset.navigationReady === 'true') return;

        const viewport = root.querySelector('[data-crm-board-scroll]');
        const boardViewport = root.querySelector('[data-crm-board-viewport]');
        const prevButtons = [...root.querySelectorAll('[data-crm-board-prev]')];
        const nextButtons = [...root.querySelectorAll('[data-crm-board-next]')];
        const jumps = [...root.querySelectorAll('[data-crm-board-jump]')];
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

            list.forEach((stage, index) => {
                const left = stage.offsetLeft;
                const right = left + stage.offsetWidth;
                const visible = Math.max(0, Math.min(right, viewRight) - Math.max(left, viewLeft));
                const ratio = visible / Math.max(1, stage.offsetWidth);
                if (ratio > 0.4) visibleIndexes.push(index);
                if (ratio > bestVisibility) {
                    bestVisibility = ratio;
                    activeIndex = index;
                }
            });

            const firstVisible = visibleIndexes.length > 0 ? visibleIndexes[0] : activeIndex;
            const lastVisible = visibleIndexes.length > 0 ? visibleIndexes[visibleIndexes.length - 1] : activeIndex;

            jumps.forEach((jump, index) => {
                const isActive = index >= firstVisible && index <= lastVisible;
                jump.classList.toggle('is-active', isActive);
                jump.setAttribute('aria-selected', isActive ? 'true' : 'false');
            });

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

        const jumpsStrip = root.querySelector('[data-crm-board-jumps]');
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
    };

    const initCrmBoardPage = () => {
        // Drop refreshers for boards removed by content swap
        refreshers.forEach((fn) => {
            try { fn(); } catch { refreshers.delete(fn); }
        });

        document.querySelectorAll('[data-crm-board-carousel]').forEach(initBoardNavigation);
    };

    window.OrbitaCrmBoard = window.OrbitaCrmBoard || {};
    window.OrbitaCrmBoard.init = initCrmBoardPage;

    initCrmBoardPage();
    document.addEventListener('orbita:content-updated', initCrmBoardPage);
})();
