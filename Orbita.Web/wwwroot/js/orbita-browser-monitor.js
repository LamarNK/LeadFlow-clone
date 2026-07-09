(function () {
    var connection = null;
    var accessToken = null;
    var browserMonitorHubUrl = null;
    var activeSessionId = null;
    var grid = null;
    var stopping = false;

    function getRoot() {
        return document.querySelector('[data-browser-monitor-page]');
    }

    function getCsrfToken() {
        var token = document.querySelector('input[name="__RequestVerificationToken"]');
        return token ? token.value : '';
    }

    function setStatus(text) {
        var el = document.querySelector('[data-browser-monitor-status]');
        if (el) {
            el.textContent = text;
        }
    }

    async function refreshAccessToken() {
        var tokenResp = await fetch('/Realtime/AccessToken', { credentials: 'same-origin' });
        if (!tokenResp.ok) {
            throw new Error('Не удалось получить токен realtime.');
        }
        var tokenPayload = await tokenResp.json();
        accessToken = tokenPayload.accessToken;
        browserMonitorHubUrl = tokenPayload.browserMonitorHubUrl
            || (tokenPayload.hubUrl ? tokenPayload.hubUrl.replace('/hubs/panel', '/hubs/browser-monitor') : null);
        if (!browserMonitorHubUrl) {
            throw new Error('Не настроен URL browser-monitor hub.');
        }
    }

    function buildConnection(hubUrl) {
        var hub = new signalR.HubConnectionBuilder()
            .withUrl(hubUrl, {
                accessTokenFactory: function () { return accessToken || ''; }
            })
            .configureLogging(signalR.LogLevel.Warning)
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        hub.on('Catalog', function (message) {
            if (!grid) return;
            var browsers = message.browsers || message.Browsers || [];
            grid.setCatalog(browsers);
        });

        hub.on('Frame', function (message) {
            if (!grid) return;
            grid.applyFrame(message);
        });

        hub.onreconnected(function () {
            if (!activeSessionId) {
                return;
            }
            return connection.invoke('JoinAsOperator', activeSessionId).catch(function () {
                setStatus('Сессия просмотра завершена. Обновите страницу.');
            });
        });

        return hub;
    }

    async function ensureConnection() {
        if (connection && connection.state === signalR.HubConnectionState.Connected) {
            return connection;
        }

        await refreshAccessToken();
        if (connection) {
            try {
                await connection.stop();
            } catch (e) { }
        }

        connection = buildConnection(browserMonitorHubUrl);
        await connection.start();
        return connection;
    }

    async function startSession(workerId) {
        var form = new FormData();
        form.append('workerId', workerId);
        form.append('__RequestVerificationToken', getCsrfToken());
        var resp = await fetch('/BrowserMonitor/Start?workerId=' + encodeURIComponent(workerId), {
            method: 'POST',
            body: form,
            credentials: 'same-origin'
        });
        if (!resp.ok) {
            var err = await resp.json().catch(function () { return {}; });
            throw new Error(err.error || 'Не удалось создать сессию просмотра.');
        }
        return resp.json();
    }

    async function stopSession(sessionId) {
        if (!sessionId || stopping) {
            return;
        }
        stopping = true;
        var form = new FormData();
        form.append('__RequestVerificationToken', getCsrfToken());
        try {
            await fetch('/BrowserMonitor/Stop/' + sessionId, {
                method: 'POST',
                body: form,
                credentials: 'same-origin',
                keepalive: true
            });
        } catch (e) { }
    }

    async function cleanup() {
        var sessionId = activeSessionId;
        activeSessionId = null;
        if (grid) {
            grid.destroy();
            grid = null;
        }
        if (connection) {
            try {
                await connection.stop();
            } catch (e) { }
            connection = null;
        }
        if (sessionId) {
            await stopSession(sessionId);
        }
    }

    async function init() {
        var root = getRoot();
        if (!root || !window.OrbitaBrowserGrid) {
            return;
        }

        var workerId = root.getAttribute('data-worker-id');
        if (!workerId) {
            setStatus('Не указан воркер.');
            return;
        }

        var gridRoot = root.querySelector('[data-browser-monitor-grid]');
        grid = window.OrbitaBrowserGrid.create({
            root: gridRoot,
            interactive: false
        });

        window.addEventListener('pagehide', function () {
            cleanup();
        });
        window.addEventListener('beforeunload', function () {
            if (activeSessionId) {
                stopSession(activeSessionId);
            }
        });

        try {
            setStatus('Создание сессии просмотра…');
            var session = await startSession(workerId);
            activeSessionId = session.id || session.Id;
            if (session.browsers || session.Browsers) {
                grid.setCatalog(session.browsers || session.Browsers);
            }

            setStatus('Подключение к воркеру…');
            await ensureConnection();
            await connection.invoke('JoinAsOperator', activeSessionId);
            setStatus('Только просмотр. Управление браузерами отключено.');
        } catch (err) {
            setStatus(err.message || String(err));
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }
})();