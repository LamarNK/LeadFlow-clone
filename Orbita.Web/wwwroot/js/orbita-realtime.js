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
        accounts: ['Accounts', 'NavBadges', 'Dashboard']
    };

    var handlers = {};
    var connection = null;
    var connectPromise = null;
    var debounceTimer = null;
    var fetchInFlight = false;
    var pendingKinds = [];
    var accessToken = null;

    function normalizeKind(kind) {
        if (typeof kind === 'number') {
            var names = ['Dashboard', 'Responses', 'Workers', 'Events', 'Errors', 'Accounts', 'NavBadges'];
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
        var kinds = pendingKinds.slice();
        pendingKinds = [];

        if (kinds.indexOf('NavBadges') >= 0) {
            fetchNavBadges();
        }

        var page = getActivePage();
        if (!page || !handlers[page]) return;

        var handler = handlers[page];
        if (typeof handler.fetchSnapshot !== 'function') return;
        if (fetchInFlight) return;

        fetchInFlight = true;
        if (window.OrbitaLiveShared) {
            window.OrbitaLiveShared.setRefreshBusy(true);
        }

        handler.fetchSnapshot()
            .catch(function (err) {
                console.warn('Live snapshot refresh:', err);
            })
            .finally(function () {
                fetchInFlight = false;
                if (window.OrbitaLiveShared) {
                    window.OrbitaLiveShared.setRefreshBusy(false);
                }
            });
    }

    function scheduleRefresh(notification) {
        var kinds = normalizeKinds(notification.kinds || notification.Kinds);
        kinds.forEach(function (k) {
            if (pendingKinds.indexOf(k) < 0) pendingKinds.push(k);
        });

        if (!matchesOfficeScope(notification) && kinds.indexOf('NavBadges') < 0) {
            return;
        }

        if (!shouldHandle(notification) && kinds.indexOf('NavBadges') < 0) {
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

    function connect() {
        if (connectPromise) return connectPromise;
        if (typeof signalR === 'undefined') {
            return Promise.reject(new Error('SignalR client is not loaded'));
        }

        connectPromise = fetchAccessToken()
            .then(function (payload) {
                if (connection) {
                    return connection;
                }

                var hubUrl = payload.hubUrl || '/hubs/panel';
                // Same-origin hub goes through Web→YARP→API; WebSocket upgrade often fails there.
                // Direct api.* URL (absolute) supports WebSocket through a single reverse proxy hop.
                var useWebSockets = /^https?:\/\//i.test(hubUrl);
                var transports = useWebSockets
                    ? (signalR.HttpTransportType.WebSockets
                        | signalR.HttpTransportType.ServerSentEvents
                        | signalR.HttpTransportType.LongPolling)
                    : (signalR.HttpTransportType.ServerSentEvents
                        | signalR.HttpTransportType.LongPolling);

                connection = new signalR.HubConnectionBuilder()
                    .withUrl(hubUrl, {
                        accessTokenFactory: function () { return accessToken || ''; },
                        transport: transports
                    })
                    .configureLogging(signalR.LogLevel.Warning)
                    .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
                    .build();

                connection.on('PanelChanged', scheduleRefresh);
                connection.onreconnecting(function () {
                    fetchAccessToken().catch(function () { });
                });
                connection.onreconnected(function () {
                    fetchAccessToken()
                        .then(function () {
                            var page = getActivePage();
                            if (page && handlers[page]) {
                                scheduleRefresh({ kinds: PAGE_KINDS[page] || [] });
                            }
                            fetchNavBadges();
                        })
                        .catch(function () { });
                });

                return connection.start().then(function () { return connection; });
            })
            .catch(function (err) {
                connectPromise = null;
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

    document.addEventListener('orbita:content-updated', function () {
        bindRefreshButtons();
        fetchNavBadges();
        var page = getActivePage();
        if (page && handlers[page]) {
            scheduleRefresh({ kinds: PAGE_KINDS[page] || [] });
        }
    });

    connect().then(function () {
        fetchNavBadges();
    }).catch(function () { });
})();