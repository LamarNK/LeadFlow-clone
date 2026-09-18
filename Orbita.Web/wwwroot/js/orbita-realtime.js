(function () {
    if (window.__orbitaRealtimeBootstrapped) return;
    window.__orbitaRealtimeBootstrapped = true;

    var PAGE_KINDS = {
        dashboard: ['Dashboard', 'NavBadges'],
        responses: ['Responses', 'NavBadges', 'Accounts'],
        workers: ['Workers', 'NavBadges'],
        worker: ['Workers', 'Dashboard', 'Events', 'Accounts'],
        events: ['Events', 'NavBadges'],
        errors: ['Errors', 'NavBadges'],
        journal: ['Events', 'Errors', 'NavBadges'],
        accounts: ['Accounts', 'NavBadges', 'Dashboard'],
        listings: ['Listings', 'Accounts'],
        statistics: ['Statistics', 'Dashboard', 'Accounts', 'NavBadges'],
        schedule: ['Schedule', 'Workers', 'Dashboard'],
        crm: ['Crm']
    };

    var POLL_INTERVAL_MS = 60000;
    var RESPONSES_FALLBACK_POLL_INTERVAL_MS = 5000;

    var handlers = {};
    var connection = null;
    var connectPromise = null;
    var debounceTimer = null;
    var fetchInFlight = false;
    var refreshQueuedWhileInFlight = false;
    var pendingKinds = [];
    var accessToken = null;
    var pollTimer = null;

    function normalizeKind(kind) {
        if (typeof kind === 'number') {
            var names = ['Dashboard', 'Responses', 'Workers', 'Events', 'Errors', 'Accounts', 'Statistics', 'NavBadges', 'Crm', 'WorkerDetails', 'Reference', 'Listings', 'Schedule'];
            return names[kind] || null;
        }
        return kind;
    }

    function normalizeKinds(kinds) {
        return (kinds || []).map(normalizeKind).filter(Boolean);
    }

    function getActivePage() {
        var root = document.querySelector('[data-orbita-live]');
        return root ? root.getAttribute('data-orbita-live-page') : null;
    }

    function getSelectedOfficeId() {
        return document.body.getAttribute('data-orbita-selected-office') || '';
    }

    function matchesOfficeScope(notification) {
        var selected = getSelectedOfficeId();
        if (!selected) return true;
        var officeId = notification.officeId || notification.OfficeId;
        if (!officeId) return true;
        return String(officeId).toLowerCase() === selected.toLowerCase();
    }

    function shouldHandle(notification) {
        if (!matchesOfficeScope(notification)) return false;
        var page = getActivePage();
        if (!page || !handlers[page]) return false;
        var allowed = PAGE_KINDS[page] || [];
        var kinds = normalizeKinds(notification.kinds || notification.Kinds);
        return kinds.some(function (k) { return allowed.indexOf(k) >= 0; });
    }

    function fetchAccessToken() {
        return fetch('/Realtime/AccessToken', { credentials: 'same-origin' })
            .then(function (res) {
                if (!res.ok) throw new Error('Realtime access token failed: ' + res.status);
                return res.json();
            })
            .then(function (payload) {
                accessToken = payload.accessToken;
                return payload;
            });
    }

    function fetchNavBadges() {
        fetch('/Nav/Badges', { credentials: 'same-origin' })
            .then(function (res) { return res.ok ? res.json() : null; })
            .then(function (payload) {
                if (!payload || !window.Orbita || !window.Orbita.updateNavBadges) return;
                window.Orbita.updateNavBadges(payload);
            })
            .catch(function () { });
    }

    function flushPending() {
        debounceTimer = null;

        var page = getActivePage();
        if (!page || !handlers[page]) return;

        var handler = handlers[page];
        if (typeof handler.fetchSnapshot !== 'function') return;
        if (fetchInFlight) {
            refreshQueuedWhileInFlight = true;
            return;
        }

        pendingKinds = [];
        fetchNavBadges();

        fetchInFlight = true;
        if (window.OrbitaLiveShared) {
            window.OrbitaLiveShared.setRefreshBusy(true);
        }

        handler.fetchSnapshot()
            .catch(function (err) {
                if (err && err.name === 'AbortError') return;
                console.warn('Live snapshot refresh:', err);
            })
            .finally(function () {
                fetchInFlight = false;
                if (window.OrbitaLiveShared) {
                    window.OrbitaLiveShared.setRefreshBusy(false);
                }
                if (refreshQueuedWhileInFlight) {
                    refreshQueuedWhileInFlight = false;
                    if (debounceTimer) window.clearTimeout(debounceTimer);
                    debounceTimer = window.setTimeout(flushPending, 0);
                }
            });
    }

    function scheduleBadgeRefresh() {
        if (debounceTimer) window.clearTimeout(debounceTimer);
        debounceTimer = window.setTimeout(function () {
            debounceTimer = null;
            fetchNavBadges();
        }, 300);
    }

    function showOperatorMessage(notification) {
        var message = notification.operatorMessage || notification.OperatorMessage;
        if (!message || !window.Orbita || typeof window.Orbita.toast !== 'function') return;
        var variant = notification.operatorMessageVariant || notification.OperatorMessageVariant || 'error';
        window.Orbita.toast(message, { variant: variant });
    }

    function scheduleRefresh(notification) {
        var kinds = normalizeKinds(notification.kinds || notification.Kinds);
        if (window.OrbitaRuntime && typeof window.OrbitaRuntime.invalidateNavCacheForKinds === 'function') {
            window.OrbitaRuntime.invalidateNavCacheForKinds(kinds);
        }
        kinds.forEach(function (k) {
            if (pendingKinds.indexOf(k) < 0) pendingKinds.push(k);
        });

        if (matchesOfficeScope(notification)) {
            showOperatorMessage(notification);
        }

        if (!matchesOfficeScope(notification)) {
            return;
        }

        if (!shouldHandle(notification)) {
            scheduleBadgeRefresh();
            return;
        }

        if (debounceTimer) window.clearTimeout(debounceTimer);
        debounceTimer = window.setTimeout(flushPending, 300);
    }

    function bindRefreshButtons() {
        document.querySelectorAll('[data-orbita-refresh]').forEach(function (btn) {
            if (btn.hasAttribute('data-orbita-live-refresh-bound')) return;
            btn.setAttribute('data-orbita-live-refresh-bound', '1');
            btn.addEventListener('click', function (e) {
                var page = getActivePage();
                if (!page || !handlers[page]) return;
                e.preventDefault();
                scheduleRefresh({ kinds: PAGE_KINDS[page] || [] });
            });
        });
    }

    function isConnected() {
        return !!(connection
            && typeof signalR !== 'undefined'
            && connection.state === signalR.HubConnectionState.Connected);
    }

    function refreshActivePage() {
        var page = getActivePage();
        if (page && handlers[page]) {
            scheduleRefresh({ kinds: PAGE_KINDS[page] || [] });
        }
        fetchNavBadges();
    }

    function startPollingFallback() {
        if (pollTimer) return;
        var lastDefaultRefreshAt = 0;
        pollTimer = window.setInterval(function () {
            if (isConnected()) {
                stopPollingFallback();
                return;
            }

            var page = getActivePage();
            var now = Date.now();
            if (page === 'responses' || now - lastDefaultRefreshAt >= POLL_INTERVAL_MS) {
                lastDefaultRefreshAt = now;
                refreshActivePage();
            }
        }, RESPONSES_FALLBACK_POLL_INTERVAL_MS);
    }

    function stopPollingFallback() {
        if (!pollTimer) return;
        window.clearInterval(pollTimer);
        pollTimer = null;
    }

    function buildConnection(hubUrl) {
        var useWebSockets = /^https?:\/\//i.test(hubUrl);
        var transports = useWebSockets
            ? (signalR.HttpTransportType.WebSockets
                | signalR.HttpTransportType.ServerSentEvents
                | signalR.HttpTransportType.LongPolling)
            : (signalR.HttpTransportType.ServerSentEvents
                | signalR.HttpTransportType.LongPolling);

        var hub = new signalR.HubConnectionBuilder()
            .withUrl(hubUrl, {
                accessTokenFactory: function () { return accessToken || ''; },
                transport: transports
            })
            .configureLogging(signalR.LogLevel.Warning)
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        hub.on('PanelChanged', scheduleRefresh);
        hub.on('CrmNotificationChanged', function (notification) {
            if (window.OrbitaNotifications && typeof window.OrbitaNotifications.handleRealtime === 'function') {
                window.OrbitaNotifications.handleRealtime(notification);
            } else {
                fetchNavBadges();
            }
        });
        hub.onreconnecting(function () {
            fetchAccessToken().catch(function () { });
            startPollingFallback();
        });
        hub.onreconnected(function () {
            stopPollingFallback();
            fetchAccessToken()
                .then(function () {
                    refreshActivePage();
                })
                .catch(function () { });
        });
        hub.onclose(function () {
            startPollingFallback();
        });

        return hub;
    }

    function resetConnection() {
        connectPromise = null;
        if (!connection) return Promise.resolve();
        var existing = connection;
        connection = null;
        return existing.stop().catch(function () { });
    }

    function forceReconnect() {
        return resetConnection().then(function () {
            return connect();
        });
    }

    function connect() {
        if (connectPromise) return connectPromise;
        if (typeof signalR === 'undefined') {
            startPollingFallback();
            return Promise.reject(new Error('SignalR client is not loaded'));
        }

        connectPromise = fetchAccessToken()
            .then(function (payload) {
                if (connection) {
                    if (connection.state === signalR.HubConnectionState.Connected) {
                        return connection;
                    }
                    if (connection.state === signalR.HubConnectionState.Connecting
                        || connection.state === signalR.HubConnectionState.Reconnecting) {
                        return connection;
                    }
                    return connection.start().then(function () {
                        stopPollingFallback();
                        return connection;
                    });
                }

                var hubUrl = payload.hubUrl || '/hubs/panel';
                connection = buildConnection(hubUrl);

                return connection.start().then(function () {
                    stopPollingFallback();
                    return connection;
                });
            })
            .catch(function (err) {
                connectPromise = null;
                startPollingFallback();
                console.warn('Orbita realtime connect:', err);
                throw err;
            });

        return connectPromise;
    }

    window.OrbitaLive = {
        register: function (page, handler) {
            handlers[page] = handler || {};
            bindRefreshButtons();
            connect().catch(function () { });
        },
        unregister: function (page) {
            delete handlers[page];
        },
        getActivePage: getActivePage,
        scheduleRefresh: scheduleRefresh,
        connect: connect
    };

    document.addEventListener('orbita:content-updated', function (event) {
        bindRefreshButtons();
        fetchNavBadges();
        if (event && event.detail && event.detail.skipLiveRefresh) return;
        var page = getActivePage();
        if (page && handlers[page]) {
            scheduleRefresh({ kinds: PAGE_KINDS[page] || [] });
        }
    });

    window.addEventListener('online', function () {
        forceReconnect().catch(function () { });
    });

    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState !== 'visible') return;
        if (isConnected()) {
            refreshActivePage();
            return;
        }
        forceReconnect().catch(function () { });
    });

    connect().then(function () {
        fetchNavBadges();
    }).catch(function () { });

    window.setInterval(fetchNavBadges, POLL_INTERVAL_MS);
})();
