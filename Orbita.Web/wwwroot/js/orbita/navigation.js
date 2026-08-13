(function (runtime) {
    runtime.initAccountCombobox = function initAccountCombobox() {
        document.querySelectorAll('[data-orbita-account-combobox]').forEach(function (root) {
            if (root.hasAttribute('data-orbita-combobox-bound')) return;
            root.setAttribute('data-orbita-combobox-bound', '1');

            var hidden = root.querySelector('[data-orbita-account-value]');
            var input = root.querySelector('.orbita-account-combobox__input');
            var list = root.querySelector('.orbita-account-combobox__list');
            var clearBtn = root.querySelector('[data-orbita-account-clear]');
            var options = Array.prototype.slice.call(root.querySelectorAll('.orbita-account-combobox__option'));
            if (!hidden || !input || !list) return;

            function setValue(value, label, submit) {
                hidden.value = value || '';
                input.value = label || '';
                options.forEach(function (opt) {
                    var selected = opt.getAttribute('data-value') === (value || '');
                    opt.classList.toggle('is-selected', selected);
                    opt.setAttribute('aria-selected', selected ? 'true' : 'false');
                });
                if (clearBtn) {
                    if (value) clearBtn.removeAttribute('hidden');
                    else clearBtn.setAttribute('hidden', '');
                }
                if (submit) {
                    var form = root.closest('form');
                    if (form) runtime.submitFilterForm(form);
                }
            }

            function filteredOptions(query) {
                var q = (query || '').trim().toLowerCase();
                if (!q) return options;
                return options.filter(function (opt) {
                    return (opt.getAttribute('data-label') || opt.textContent || '').toLowerCase().indexOf(q) >= 0;
                });
            }

            function openList() {
                list.removeAttribute('hidden');
                input.setAttribute('aria-expanded', 'true');
            }

            function closeList() {
                list.setAttribute('hidden', '');
                input.setAttribute('aria-expanded', 'false');
            }

            input.addEventListener('focus', function () {
                openList();
            });

            input.addEventListener('input', function () {
                var visible = filteredOptions(input.value);
                options.forEach(function (opt) {
                    opt.hidden = visible.indexOf(opt) < 0;
                });
                openList();
            });

            options.forEach(function (opt) {
                opt.addEventListener('mousedown', function (e) {
                    e.preventDefault();
                    setValue(opt.getAttribute('data-value') || '', opt.getAttribute('data-label') || opt.textContent, true);
                    closeList();
                });
            });

            if (clearBtn) {
                clearBtn.addEventListener('click', function () {
                    setValue('', '', true);
                    closeList();
                });
            }

            document.addEventListener('click', function (e) {
                if (!root.contains(e.target)) closeList();
            });
        });
    }

    runtime.initDetailOpenButtons = function initDetailOpenButtons() {
        document.querySelectorAll('[data-orbita-detail-open]').forEach(function (btn) {
            if (btn.hasAttribute('data-orbita-detail-bound')) return;
            btn.setAttribute('data-orbita-detail-bound', '1');

            btn.addEventListener('click', function (e) {
                e.stopPropagation();
                var row = btn.closest('tr');
                if (!row) return;
                runtime.openDetailFromRow(row);
            });
        });
    }

    runtime.initFilterPanels();
    runtime.initDebouncedSearch();
    runtime.initAutoFilterSubmit();
    runtime.initAccountCombobox();
    runtime.initDetailModal();
    runtime.initDetailOpenButtons();
    runtime.initCrmTaskCreateModal();
    runtime.initCrmTaskResponsibleFilter();
    runtime.initLiveRowActions();

    // --- Fast page switching (client-side, no full reload) + loading spinner ---

    runtime.getNavKey = function getNavKey(pathname) {
        var p = (pathname || '').split('?')[0].replace(/\/+$/, '').toLowerCase();
        if (!p || p === '/' || p === '/dashboard' || p === '/dashboard/index') return 'dashboard';
        var seg = p.split('/')[1] || '';
        return seg;
    }

    runtime.updateActiveNav = function updateActiveNav(currentPath) {
        var curKey = runtime.getNavKey(currentPath);
        document.querySelectorAll('.orbita-nav .nav-item').forEach(function (a) {
            a.classList.remove('active');
            try {
                var linkKey = runtime.getNavKey(a.getAttribute('href') || a.href);
                if (linkKey === curKey) {
                    a.classList.add('active');
                }
            } catch (e) { }
        });
    }

    var currentLoadingOverlay = null;

    runtime.showPageLoading = function showPageLoading() {
        // Remove any previous overlay first
        if (currentLoadingOverlay && currentLoadingOverlay.parentNode) {
            currentLoadingOverlay.parentNode.removeChild(currentLoadingOverlay);
        }

        var overlay = document.createElement('div');
        overlay.className = 'orbita-nav-loading';
        overlay.innerHTML =
            '<div class="orbita-spinner">' +
            '<i class="fa-solid fa-spinner fa-spin" aria-hidden="true"></i>' +
            '<span>Загрузка...</span>' +
            '</div>';

        // Append to .orbita-app so it is not destroyed when we replace .orbita-content innerHTML
        var app = document.querySelector('.orbita-app') || document.body;
        app.appendChild(overlay);

        currentLoadingOverlay = overlay;
    }

    runtime.hidePageLoading = function hidePageLoading() {
        if (currentLoadingOverlay) {
            if (currentLoadingOverlay.parentNode) {
                currentLoadingOverlay.parentNode.removeChild(currentLoadingOverlay);
            }
            currentLoadingOverlay = null;
        }
    }

    runtime.reinitAfterContentSwap = function reinitAfterContentSwap() {
        // Re-run inits for swapped .orbita-content only (layout/sidebar handlers are one-time)
        runtime.initUpdatedClock();
        runtime.initUserMenu();
        runtime.initPeriodPicker();
        runtime.initConfirmDialog();
        runtime.initRowMenus();
        runtime.initSubProfilesToggles();
        runtime.initSubProfileEnableToggles();
        runtime.initWorkerAccountEnableToggles();
        runtime.initAvitoCredentialsButtons();
        runtime.initSubProfilesRefreshButtons();
        runtime.initSubProfileScreenshotLinks();
        runtime.initOfficeSwitcher();
        runtime.initWorkerRestartButtons();
        runtime.initFilterPanels();
        runtime.initDebouncedSearch();
        runtime.initAutoFilterSubmit();
        runtime.initAccountCombobox();
        runtime.initDetailModal();
        runtime.initDetailOpenButtons();
        runtime.initCrmTaskCreateModal();
        runtime.initCrmTaskResponsibleFilter();
        runtime.initCrmTaskAttachments?.();
        runtime.initCrmTaskEditButtons?.();
        runtime.initCrmClientTimes?.();

        // Re-localize any new time elements
        if (window.OrbitaTime && window.OrbitaTime.localizeAll) {
            window.OrbitaTime.localizeAll();
        }
    }

    var __loadedPageScripts = window.__orbitaLoadedScripts || (window.__orbitaLoadedScripts = new Set());

    runtime.loadScriptOnce = function loadScriptOnce(src) {
        if (__loadedPageScripts.has(src)) return Promise.resolve();
        // crude check if similar script tag already present
        var base = src.split('/').pop().split('?')[0];
        if (document.querySelector('script[src*="' + base + '"]')) {
            __loadedPageScripts.add(src);
            return Promise.resolve();
        }
        return new Promise(function (resolve, reject) {
            var s = document.createElement('script');
            s.src = src;
            s.async = false;
            s.onload = function () { __loadedPageScripts.add(src); resolve(); };
            s.onerror = function () { reject(new Error('Failed to load ' + src)); };
            document.head.appendChild(s);
        });
    }

    runtime.getScriptsForPath = function getScriptsForPath(fullPath, controllerKey) {
        var p = (fullPath || '').toLowerCase();
        var key = (controllerKey || '').toLowerCase();
        var scripts = [];

        if (key === 'dashboard' || p === '/' || p.startsWith('/dashboard')) {
            scripts = ['/lib/chart.js/dist/chart.umd.js', '/js/orbita-dashboard.js'];
        } else if (key === 'workers') {
            if (p.indexOf('/details') > -1 || /\/workers\/[0-9a-f]{8}/.test(p)) {
                scripts = ['/lib/chart.js/dist/chart.umd.js', '/js/orbita-worker.js'];
            } else {
                scripts = ['/js/orbita-workers.js'];
            }
        } else if (key === 'accounts') {
            scripts = ['/js/orbita-accounts.js'];
        } else if (key === 'statistics') {
            scripts = ['/lib/chart.js/dist/chart.umd.js', '/js/orbita-statistics.js'];
        } else if (key === 'events' || key === 'errors') {
            scripts = ['/js/orbita-journal.js'];
        } else if (key === 'responses') {
            scripts = ['/js/orbita-responses.js'];
        } else if (key === 'crm' || p.startsWith('/crm')) {
            scripts = ['/js/orbita-crm-board.js', '/js/orbita-crm-card.js', '/js/orbita-crm-team.js'];
        } else if (key === 'mysettings') {
            scripts = [
                '/js/orbita-bitrix-instances.js',
                '/js/orbita-bitrix-settings.js',
                '/lib/drawflow/dist/drawflow.min.js',
                '/js/orbita-distribution-editor.js'
            ];
        } else if (key === 'settings') {
            scripts = [
                '/js/orbita-settings.js',
                '/js/orbita-bitrix-settings.js',
                '/js/orbita-worker-releases.js'
            ];
        }
        return scripts;
    }

    async function ensurePageScripts(fullPath, controllerKey) {
        var scripts = runtime.getScriptsForPath(fullPath, controllerKey);
        for (var i = 0; i < scripts.length; i++) {
            try {
                await runtime.loadScriptOnce(scripts[i]);
            } catch (e) {
                console.warn('Page script load:', e);
            }
        }
    }

    runtime.navigateTo = async function navigateTo(targetPath, push) {
        push = push !== false;
        var content = document.querySelector('.orbita-content');
        if (!content) {
            window.location.href = targetPath;
            return;
        }

        if (window.OrbitaDashboard && typeof window.OrbitaDashboard.destroyCharts === 'function') {
            try { window.OrbitaDashboard.destroyCharts(); } catch (e) { }
        }
        if (window.OrbitaStatistics && typeof window.OrbitaStatistics.destroyCharts === 'function') {
            try { window.OrbitaStatistics.destroyCharts(); } catch (e) { }
        }
        if (window.OrbitaWorker && typeof window.OrbitaWorker.destroyCharts === 'function') {
            try { window.OrbitaWorker.destroyCharts(); } catch (e) { }
        }
        if (window.OrbitaLive && typeof window.OrbitaLive.unregister === 'function') {
            try { window.OrbitaLive.unregister(getActiveLivePage()); } catch (e) { }
        }

        function getActiveLivePage() {
            var root = document.querySelector('[data-orbita-live]');
            return root ? root.getAttribute('data-orbita-live-page') : null;
        }

        // Show spinner immediately
        runtime.showPageLoading();

        // Ensure the browser actually paints the spinner before we start fetching
        // (important for very fast demo responses)
        await new Promise(function (resolve) {
            requestAnimationFrame(function () {
                requestAnimationFrame(resolve);
            });
        });

        var fetchStart = Date.now();

        try {
            var res = await fetch(targetPath, {
                method: 'GET',
                credentials: 'same-origin',
                headers: { 'X-Orbita-Content-Only': '1' }
            });

            if (!res.ok) {
                runtime.hidePageLoading();
                window.location.href = targetPath;
                return;
            }

            var html = await res.text();

            // Make sure the loading spinner is visible for at least this long
            // even in super-fast demo mode. This is why you asked for a spinner.
            var MIN_LOADING_MS = 300;
            var elapsed = Date.now() - fetchStart;
            var remaining = Math.max(0, MIN_LOADING_MS - elapsed);

            if (remaining > 0) {
                await new Promise(function (r) { setTimeout(r, remaining); });
            }

            runtime.hidePageLoading();

            // Swap content first, then run inits — must complete before page scripts / events
            content.style.transition = 'opacity 0.15s ease';
            content.style.opacity = '0.15';

            await new Promise(function (resolve) {
                requestAnimationFrame(function () {
                    content.innerHTML = html;
                    content.style.opacity = '1';
                    setTimeout(function () {
                        content.style.transition = '';
                    }, 200);
                    resolve();
                });
            });

            // meta for title + controller (read from new content)
            var meta = content.querySelector('.orbita-page-meta');
            var pageTitle = null;
            var pageKey = null;
            if (meta) {
                pageTitle = meta.getAttribute('data-orbita-page-title');
                pageKey = meta.getAttribute('data-orbita-controller');
                meta.parentNode.removeChild(meta);
            }
            if (pageTitle) {
                document.title = pageTitle;
            }

            if (push) {
                try { history.pushState({ orbitaNav: true }, '', targetPath); } catch (e) { }
            }

            runtime.updateActiveNav(targetPath);
            runtime.hidePageLoading();
            runtime.reinitAfterContentSwap();
            runtime.restoreSearchFocus();

            await ensurePageScripts(targetPath, pageKey);

            // Let page-specific scripts re-init their elements (they listen to this)
            document.dispatchEvent(new CustomEvent('orbita:content-updated', {
                detail: { path: targetPath, key: pageKey }
            }));
            runtime.restoreSearchFocus();

            if (pageKey && pageKey.toLowerCase() === 'dashboard'
                && window.OrbitaDashboard
                && typeof window.OrbitaDashboard.reinit === 'function') {
                window.OrbitaDashboard.reinit();
            }
            if (pageKey && pageKey.toLowerCase() === 'statistics'
                && window.OrbitaStatistics
                && typeof window.OrbitaStatistics.reinit === 'function') {
                window.OrbitaStatistics.reinit();
            }
            if (pageKey && pageKey.toLowerCase() === 'crm'
                && window.OrbitaCrmBoard
                && typeof window.OrbitaCrmBoard.init === 'function') {
                window.OrbitaCrmBoard.init();
            }
        } catch (err) {
            console.warn('Orbita fast nav failed, falling back', err);
            runtime.hidePageLoading();
            window.location.href = targetPath;
        }
    }

    runtime.initClientNavigation = function initClientNavigation() {
        var nav = document.querySelector('.orbita-nav');
        if (!nav) return;

        nav.addEventListener('click', function (e) {
            var link = e.target.closest('a.nav-item');
            if (!link) return;

            var href = link.getAttribute('href');
            if (!href || href[0] === '#' || link.getAttribute('target') === '_blank') return;

            // Only intercept same-origin http(s) links
            try {
                var u = new URL(href, window.location.origin);
                if (u.origin !== window.location.origin) return;

                // Skip auth pages (login etc). Note: /accounts (plural) must NOT be skipped
                var p = u.pathname.toLowerCase();
                if (p === '/account' || p.startsWith('/account/')) return;

                e.preventDefault();
                runtime.navigateTo(u.pathname + u.search, true);
            } catch (ex) {
                // let browser handle
            }
        });

        window.addEventListener('popstate', function (ev) {
            // Only react to our history entries or always try
            var path = window.location.pathname + window.location.search;
            runtime.navigateTo(path, false);
        });

        // Initial sync (in case)
        runtime.updateActiveNav(window.location.pathname + window.location.search);
    }

    runtime.initGeneralInternalNav = function initGeneralInternalNav() {
        // Use document-level delegation so it survives content replacements.
        // Intercept internal links (tabs like active/inactive, breadcrumbs, some cards) for fast nav + spinner.
        document.addEventListener('click', function (e) {
            var link = e.target.closest('a[href]');
            if (!link) return;

            // Already handled by sidebar nav
            if (link.closest('.orbita-nav')) return;

            // Skip special buttons that open modals or have other behavior
            if (link.classList.contains('workers-add-btn') ||
                link.hasAttribute('data-no-fast-nav') ||
                link.getAttribute('target') === '_blank' ||
                (link.getAttribute('href') || '').startsWith('#')) {
                return;
            }

            var href = link.getAttribute('href');
            if (!href) return;

            try {
                var u = new URL(href, window.location.origin);
                if (u.origin !== window.location.origin) return;

                var p = u.pathname.toLowerCase();
                if (p === '/account' || p.startsWith('/account/')) return;

                // Protect downloads and special links
                if (p.includes('/download') || 
                    link.classList.contains('workers-download-btn') ||
                    link.hasAttribute('download')) {
                    return;
                }

                e.preventDefault();
                runtime.navigateTo(u.pathname + u.search, true);
            } catch (ex) { }
        });

        // Intercept GET forms (search + filters) → fast nav + spinner instead of full reload
        document.addEventListener('submit', function (e) {
            var form = e.target.closest('form');
            if (!form || !form.closest('.orbita-content')) return;

            var method = (form.getAttribute('method') || 'get').toLowerCase();
            if (method !== 'get') return;

            var action = (form.getAttribute('action') || window.location.pathname).toLowerCase();
            if (action.includes('/account')) return;

            e.preventDefault();

            try {
                var formData = new FormData(form);
                var url = new URL(form.action || window.location.href, window.location.origin);

                // Clear existing and apply form values (so empty values are removed)
                // Keep existing non-form params if needed, but for our filters it's usually clean
                formData.forEach((value, key) => {
                    if (value !== '' && value != null) {
                        url.searchParams.set(key, value);
                    } else {
                        url.searchParams.delete(key);
                    }
                });

                runtime.navigateTo(url.pathname + url.search, true);
            } catch (ex) {
                form.submit(); // fallback
            }
        }, true);

        // CRM mutations stay inside the shell: submit the form, follow its redirect,
        // and replace only the page content instead of reloading the whole document.
        document.addEventListener('submit', async function (e) {
            if (e.defaultPrevented) return;
            var form = e.target.closest('form');
            if (!form || !form.closest('.orbita-content') || form.hasAttribute('data-orbita-full-submit')) return;

            var method = (form.getAttribute('method') || 'get').toLowerCase();
            if (method !== 'post') return;

            var actionUrl;
            try {
                actionUrl = new URL(form.action || window.location.href, window.location.origin);
            } catch (ex) {
                return;
            }
            if (actionUrl.origin !== window.location.origin || !actionUrl.pathname.toLowerCase().startsWith('/crm')) return;
            if (form.hasAttribute('data-orbita-post-pending')) {
                e.preventDefault();
                return;
            }

            e.preventDefault();
            var postData;
            try {
                postData = new FormData(form, e.submitter || undefined);
            } catch (formDataError) {
                postData = new FormData(form);
                if (e.submitter && e.submitter.name) {
                    postData.set(e.submitter.name, e.submitter.value || '');
                }
            }
            form.setAttribute('data-orbita-post-pending', '1');
            var submitters = Array.prototype.slice.call(form.querySelectorAll('button[type="submit"], input[type="submit"]'));
            submitters.forEach(function (button) { button.disabled = true; });
            runtime.showPageLoading();
            var recoveryPath = window.location.pathname + window.location.search;

            try {
                var response = await fetch(actionUrl.pathname + actionUrl.search, {
                    method: 'POST',
                    credentials: 'same-origin',
                    headers: { 'X-Orbita-Content-Only': '1' },
                    body: postData
                });
                if (!response.ok) {
                    throw new Error('CRM form failed: ' + response.status);
                }

                var html = await response.text();
                var finalUrl = new URL(response.url || window.location.href, window.location.origin);
                recoveryPath = finalUrl.pathname + finalUrl.search;
                var nextHtml = html;
                if (/<!doctype|<html[\s>]/i.test(html)) {
                    var parsed = new DOMParser().parseFromString(html, 'text/html');
                    var parsedContent = parsed.querySelector('.orbita-content');
                    if (!parsedContent) throw new Error('CRM response has no content container');
                    nextHtml = parsedContent.innerHTML;
                }

                var content = document.querySelector('.orbita-content');
                if (!content) throw new Error('Current content container is missing');
                content.innerHTML = nextHtml;

                var meta = content.querySelector('.orbita-page-meta');
                var pageTitle = meta && meta.getAttribute('data-orbita-page-title');
                var pageKey = meta && meta.getAttribute('data-orbita-controller');
                if (meta) meta.remove();
                if (pageTitle) document.title = pageTitle;

                var finalPath = finalUrl.pathname + finalUrl.search;
                history.replaceState({ orbitaNav: true }, '', finalPath);
                runtime.updateActiveNav(finalPath);
                runtime.reinitAfterContentSwap();
                await ensurePageScripts(finalPath, pageKey);
                document.dispatchEvent(new CustomEvent('orbita:content-updated', {
                    detail: { path: finalPath, key: pageKey }
                }));
                runtime.restoreSearchFocus();
            } catch (error) {
                console.warn('CRM mutation navigation failed', error);
                // The form may already have been accepted, so recover with a safe GET.
                runtime.navigateTo(recoveryPath, false);
            } finally {
                form.removeAttribute('data-orbita-post-pending');
                submitters.forEach(function (button) { button.disabled = false; });
                runtime.hidePageLoading();
            }
        }, false);
    }

    runtime.initClientNavigation();
    runtime.initGeneralInternalNav();

    // Prefetch on hover for snappier feel (optional, light)
})(window.OrbitaRuntime = window.OrbitaRuntime || {});
