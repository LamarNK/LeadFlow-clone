(function () {
    var connection = null;
    var accessToken = null;
    var browserMonitorHubUrl = null;
    var activeSessionId = null;
    var grid = null;
    var stopping = false;
    var sessionPollTimer = null;
    var joinRetryTimer = null;
    var joinRetryAttempt = 0;

    var SESSION_POLL_MS = 30000;
    var JOIN_RETRY_BASE_MS = 2000;
    var JOIN_RETRY_MAX_ATTEMPTS = 6;

    function normalizeGuid(value) {
        if (window.OrbitaLive && typeof window.OrbitaLive.normalizeGuid === 'function') {
            return window.OrbitaLive.normalizeGuid(value);
        }
        return String(value || '').trim().toLowerCase();
    }

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

    var workerNotified = true;
    var initialBrowserCount = 0;
    var catalogEvents = 0;
    var frameEvents = 0;
    var lastCatalogCount = null;
    var hubState = 'init';

    function logDiag(message, extra) {
        if (typeof console !== 'undefined' && typeof console.warn === 'function') {
            console.warn('[browser-monitor] ' + message, extra || '');
        }
    }

    function updateMonitorStatus() {
        if (!workerNotified) {
            setStatus('Сессия создана. Воркер пока не подключён — ожидание…');
            return;
        }

        if (!grid || typeof grid.getActiveCount !== 'function') {
            return;
        }

        var count = grid.getActiveCount();
        var diag = ' · hub: ' + hubState
            + ' · каталог: ' + catalogEvents
            + (lastCatalogCount !== null ? ' (' + lastCatalogCount + ')' : '')
            + ' · кадры: ' + frameEvents;

        if (count === 0) {
            if (initialBrowserCount > 0) {
                setStatus('Ожидание кадров от воркера… (' + initialBrowserCount + ' браузер(ов) в сессии)' + diag);
            } else {
                setStatus('Ожидание активных браузеров от воркера… Только просмотр.' + diag);
            }
            return;
        }

        var suffix = count === 1 ? 'браузер в работе' : 'браузеров в работе';
        setStatus(count + ' ' + suffix + '. Только просмотр.' + diag);
    }

    function clearJoinRetry() {
        if (joinRetryTimer) {
            clearTimeout(joinRetryTimer);
            joinRetryTimer = null;
        }
    }

    function clearSessionPoll() {
        if (sessionPollTimer) {
            clearInterval(sessionPollTimer);
            sessionPollTimer = null;
        }
    }

    function handleSessionExpired(message) {
        clearSessionPoll();
        clearJoinRetry();
        activeSessionId = null;
        setStatus(message || 'Сессия просмотра завершена. Обновите страницу.');
        if (connection) {
            connection.stop().catch(function () { });
        }
    }

    async function pollSessionAlive() {
        if (!activeSessionId) {
            return;
        }

        try {
            var resp = await fetch('/BrowserMonitor/Status/' + encodeURIComponent(activeSessionId), {
                credentials: 'same-origin'
            });
            if (resp.status === 404) {
                handleSessionExpired('Сессия просмотра завершена (истекла или API перезапущен). Обновите страницу.');
            }
        } catch (e) { }
    }

    function startSessionPoll() {
        clearSessionPoll();
        sessionPollTimer = setInterval(function () {
            pollSessionAlive();
        }, SESSION_POLL_MS);
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

    async function joinAsOperator() {
        if (!connection || !activeSessionId) {
            return;
        }

        if (connection.state !== signalR.HubConnectionState.Connected) {
            throw new Error('Нет подключения к hub.');
        }

        await connection.invoke('JoinAsOperator', activeSessionId);
        joinRetryAttempt = 0;
        updateMonitorStatus();
    }

    function scheduleJoinRetry(reason) {
        if (!activeSessionId || joinRetryAttempt >= JOIN_RETRY_MAX_ATTEMPTS) {
            setStatus('Не удалось подключиться к сессии просмотра: ' + (reason || 'неизвестная ошибка'));
            return;
        }

        joinRetryAttempt += 1;
        var delay = JOIN_RETRY_BASE_MS * joinRetryAttempt;
        setStatus('Повторное подключение к сессии (' + joinRetryAttempt + '/' + JOIN_RETRY_MAX_ATTEMPTS + ')…');
        clearJoinRetry();
        joinRetryTimer = setTimeout(function () {
            joinRetryTimer = null;
            ensureOperatorJoin().catch(function (err) {
                scheduleJoinRetry(err.message || String(err));
            });
        }, delay);
    }

    async function ensureOperatorJoin() {
        await ensureConnection();
        try {
            await joinAsOperator();
        } catch (err) {
            if (String(err.message || err).indexOf('Нет доступа') >= 0
                || String(err.message || err).indexOf('не найдена') >= 0) {
                handleSessionExpired('Сессия просмотра недоступна. Обновите страницу.');
                return;
            }
            throw err;
        }
    }

    function buildConnection(hubUrl) {
        var useWebSockets = /^https?:\/\//i.test(hubUrl);
        var transports = useWebSockets && typeof signalR !== 'undefined'
            ? (signalR.HttpTransportType.WebSockets
                | signalR.HttpTransportType.ServerSentEvents
                | signalR.HttpTransportType.LongPolling)
            : undefined;

        var hub = new signalR.HubConnectionBuilder()
            .withUrl(hubUrl, {
                accessTokenFactory: function () { return accessToken || ''; },
                transport: transports
            })
            .configureLogging(signalR.LogLevel.Warning)
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        hub.on('Catalog', function (message) {
            if (!grid || !activeSessionId) return;
            var sessionId = message.sessionId || message.SessionId;
            if (sessionId && normalizeGuid(sessionId) !== normalizeGuid(activeSessionId)) {
                return;
            }

            var catalog = message.browsers || message.Browsers || [];
            catalogEvents += 1;
            lastCatalogCount = catalog.length;

            if (catalog.length === 0 && grid.getActiveCount() > 0) {
                logDiag('Пустой каталог от воркера проигнорирован, оставляем ' + grid.getActiveCount() + ' плиток.');
                updateMonitorStatus();
                return;
            }

            if (catalog.length === 0 && initialBrowserCount > 0) {
                logDiag('Пустой каталог от воркера, ожидаем регистрации браузеров (в сессии было ' + initialBrowserCount + ').');
                updateMonitorStatus();
                return;
            }

            logDiag('Каталог получен: ' + catalog.length + ' браузер(ов).');
            grid.setCatalog(catalog);
            updateMonitorStatus();
        });

        hub.on('Frame', function (message) {
            if (!grid || !activeSessionId) return;
            var sessionId = message.sessionId || message.SessionId;
            if (sessionId && normalizeGuid(sessionId) !== normalizeGuid(activeSessionId)) {
                return;
            }

            frameEvents += 1;
            if (frameEvents === 1 || frameEvents % 30 === 0) {
                logDiag('Кадр #' + frameEvents + ' для ' + (message.accountId || message.AccountId || '?'));
            }

            grid.applyFrame(message);
            updateMonitorStatus();
        });

        hub.onreconnecting(function () {
            hubState = 'reconnecting';
            setStatus('Переподключение к воркеру…');
        });

        hub.onreconnected(function () {
            if (!activeSessionId) {
                return;
            }

            hubState = 'connected';
            refreshAccessToken()
                .then(function () {
                    return joinAsOperator();
                })
                .catch(function (err) {
                    scheduleJoinRetry(err.message || String(err));
                });
        });

        hub.onclose(function (err) {
            if (!activeSessionId) {
                return;
            }

            if (stopping) {
                return;
            }

            hubState = 'closed';
            logDiag('Hub закрыт', err);
            setStatus('Соединение с воркером закрыто' + (err ? ': ' + err.message : '.') + ' Переподключение…');
            ensureOperatorJoin().catch(function (joinErr) {
                scheduleJoinRetry(joinErr.message || String(joinErr));
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
        hubState = 'connected';
        logDiag('Hub подключён: ' + browserMonitorHubUrl);
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
        clearSessionPoll();
        clearJoinRetry();
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
            workerNotified = session.workerNotified !== false && session.WorkerNotified !== false;
            var initialBrowsers = session.browsers || session.Browsers || [];
            initialBrowserCount = initialBrowsers.length;
            logDiag('Сессия создана', {
                sessionId: activeSessionId,
                workerNotified: workerNotified,
                initialBrowsers: initialBrowserCount
            });

            if (initialBrowsers.length > 0) {
                grid.setCatalog(initialBrowsers);
            }

            if (!workerNotified) {
                setStatus('Сессия создана. Воркер оффлайн — ожидание подключения…');
            } else {
                setStatus('Подключение к воркеру…');
            }
            await ensureOperatorJoin();
            startSessionPoll();
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