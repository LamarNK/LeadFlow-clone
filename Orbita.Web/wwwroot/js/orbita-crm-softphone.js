(function () {
    'use strict';

    var ua = null;
    var uaKey = '';
    var registrationPromise = null;
    var currentConfig = null;
    var reconnectTimer = null;
    var healthTimer = null;
    var reconnectAttempt = 0;
    var shuttingDown = false;
    var activeSession = null;
    var activeDirection = '';
    var closeTimer = null;
    var peerConnectionConfig = { iceServers: [], iceTransportPolicy: 'all' };
    var iceGatheringTimeoutMs = 6000;
    var registrationExpiresSeconds = 120;
    var registrationHealthIntervalMs = 15000;
    var modal = document.querySelector('[data-orbita-softphone]');
    var targetLabel = document.querySelector('[data-orbita-softphone-target]');
    var statusLabel = document.querySelector('[data-orbita-softphone-status]');
    var answerButton = document.querySelector('[data-orbita-softphone-answer]');
    var hangupButton = document.querySelector('[data-orbita-softphone-hangup]');
    var hangupLabel = document.querySelector('[data-orbita-softphone-hangup-label]');
    var remoteAudio = document.querySelector('[data-orbita-softphone-audio]');

    function toast(message, variant) {
        if (window.Orbita && typeof window.Orbita.toast === 'function') {
            window.Orbita.toast(message, { variant: variant || 'info' });
        }
    }

    function setStatus(text) {
        if (statusLabel) statusLabel.textContent = text;
    }

    function showCall(phone) {
        if (closeTimer) {
            window.clearTimeout(closeTimer);
            closeTimer = null;
        }
        if (targetLabel) targetLabel.textContent = phone || 'Исходящий звонок';
        if (modal) modal.removeAttribute('hidden');
    }

    function closeCall(delay) {
        if (closeTimer) window.clearTimeout(closeTimer);
        closeTimer = window.setTimeout(function () {
            closeTimer = null;
            if (modal) modal.setAttribute('hidden', '');
            if (answerButton) answerButton.setAttribute('hidden', '');
            if (hangupButton) hangupButton.disabled = true;
            if (hangupLabel) hangupLabel.textContent = 'Завершить';
            if (remoteAudio) remoteAudio.srcObject = null;
            activeDirection = '';
        }, delay || 0);
    }

    function showIncomingControls() {
        if (answerButton) answerButton.removeAttribute('hidden');
        if (hangupButton) hangupButton.disabled = false;
        if (hangupLabel) hangupLabel.textContent = 'Отклонить';
    }

    function showActiveCallControls() {
        if (answerButton) answerButton.setAttribute('hidden', '');
        if (hangupButton) hangupButton.disabled = false;
        if (hangupLabel) hangupLabel.textContent = 'Завершить';
    }

    function normalizePhone(phone) {
        return String(phone || '').replace(/\D/g, '');
    }

    function buildPeerConnectionConfig(config) {
        var urls = Array.isArray(config && config.iceServerUrls)
            ? config.iceServerUrls.filter(function (url) {
                return typeof url === 'string' && url.trim().length > 0;
            })
            : [];
        if (!urls.length) {
            return { iceServers: [], iceTransportPolicy: 'all' };
        }

        var stunUrls = urls.filter(function (url) {
            return /^stuns?:/i.test(url);
        });
        var turnUrls = urls.filter(function (url) {
            return /^turns?:/i.test(url);
        });
        var iceServers = [];
        if (stunUrls.length) {
            iceServers.push({ urls: stunUrls });
        }
        if (turnUrls.length) {
            var turnServer = { urls: turnUrls };
            if (config.iceUsername && config.iceCredential) {
                turnServer.username = config.iceUsername;
                turnServer.credential = config.iceCredential;
            }
            iceServers.push(turnServer);
        }
        return { iceServers: iceServers, iceTransportPolicy: 'all' };
    }

    function createIceCandidateHandler() {
        var timeout = null;
        var latestReady = null;
        var completed = false;

        function finish(ready) {
            if (completed || typeof ready !== 'function') return;
            completed = true;
            if (timeout) window.clearTimeout(timeout);
            timeout = null;
            latestReady = null;
            ready();
        }

        function handler(event) {
            if (completed || !event || typeof event.ready !== 'function') return;
            latestReady = event.ready;

            var candidate = event.candidate;
            var candidateText = candidate && candidate.candidate ? candidate.candidate : '';
            var candidateType = candidate && candidate.type ? candidate.type : '';
            if (candidateType === 'relay' || /\btyp relay\b/i.test(candidateText)) {
                finish(event.ready);
                return;
            }

            if (!timeout) {
                timeout = window.setTimeout(function () {
                    finish(latestReady);
                }, iceGatheringTimeoutMs);
            }
        }

        handler.dispose = function () {
            completed = true;
            if (timeout) window.clearTimeout(timeout);
            timeout = null;
            latestReady = null;
        };
        return handler;
    }

    async function loadConfig() {
        var response = await fetch('/Crm/WebRtcConfig', {
            method: 'GET',
            credentials: 'same-origin',
            cache: 'no-store',
            headers: { Accept: 'application/json' }
        });
        if (!response.ok) {
            var error = null;
            try { error = await response.json(); } catch (e) { }
            throw new Error((error && error.error) || 'Для вашего аккаунта браузерная телефония не настроена.');
        }
        currentConfig = await response.json();
        return currentConfig;
    }

    function clearReconnectTimer() {
        if (!reconnectTimer) return;
        window.clearTimeout(reconnectTimer);
        reconnectTimer = null;
    }

    function isUserAgentHealthy(agent) {
        return !!(agent
            && agent.isConnected
            && agent.isConnected()
            && agent.isRegistered
            && agent.isRegistered());
    }

    function disposeUserAgent(agent) {
        if (!agent || ua !== agent) return;
        registrationPromise = null;
        ua = null;
        uaKey = '';
        try { agent.stop(); } catch (e) { }
    }

    function stopUserAgent() {
        clearReconnectTimer();
        reconnectAttempt = 0;
        if (ua) disposeUserAgent(ua);
    }

    function scheduleRegistrationRecovery(config, failedAgent) {
        if (shuttingDown || !config || reconnectTimer) return;
        if (failedAgent && ua !== failedAgent) return;

        var delay = Math.min(30000, 1000 * Math.pow(2, Math.min(reconnectAttempt, 5)));
        reconnectAttempt += 1;
        reconnectTimer = window.setTimeout(function () {
            reconnectTimer = null;
            if (shuttingDown) return;
            if (failedAgent && ua !== failedAgent) return;
            if (ua) disposeUserAgent(ua);
            ensureRegistered(config).catch(function () {
                scheduleRegistrationRecovery(config, ua);
            });
        }, delay);
    }

    function ensureRegistered(config) {
        currentConfig = config;
        peerConnectionConfig = buildPeerConnectionConfig(config);
        var key = [config.webSocketUrl, config.sipUri, config.authorizationUsername].join('|');
        if (ua && uaKey === key && isUserAgentHealthy(ua)) {
            return Promise.resolve(ua);
        }
        // Never leave an unregistered UA running in the background. JsSIP would keep
        // reconnecting it and multiple agents would replace the single AOR contact.
        if (ua) disposeUserAgent(ua);
        if (registrationPromise) return registrationPromise;
        if (!window.JsSIP || !window.JsSIP.WebSocketInterface || !window.JsSIP.UA) {
            return Promise.reject(new Error('SIP-клиент не загрузился. Обновите страницу.'));
        }

        registrationPromise = new Promise(function (resolve, reject) {
            var settled = false;
            var agent = null;
            var timeout = window.setTimeout(function () {
                if (settled) return;
                settled = true;
                registrationPromise = null;
                scheduleRegistrationRecovery(config, agent);
                reject(new Error('SIP-линия не ответила за 15 секунд.'));
            }, 15000);

            function succeed() {
                if (ua !== agent) return;
                reconnectAttempt = 0;
                clearReconnectTimer();
                if (settled) return;
                settled = true;
                window.clearTimeout(timeout);
                registrationPromise = null;
                resolve(ua);
            }

            function fail(event) {
                if (ua !== agent) return;
                scheduleRegistrationRecovery(config, agent);
                if (settled) return;
                settled = true;
                window.clearTimeout(timeout);
                registrationPromise = null;
                var cause = event && event.cause ? ': ' + event.cause : '';
                reject(new Error('Не удалось зарегистрировать SIP-линию' + cause + '.'));
            }

            try {
                var socket = new window.JsSIP.WebSocketInterface(config.webSocketUrl);
                agent = new window.JsSIP.UA({
                    sockets: [socket],
                    uri: config.sipUri,
                    authorization_user: config.authorizationUsername,
                    password: config.password,
                    register: true,
                    register_expires: registrationExpiresSeconds,
                    connection_recovery_min_interval: 1,
                    connection_recovery_max_interval: 10,
                    session_timers: false
                });
                ua = agent;
                uaKey = key;
                agent.on('registered', succeed);
                agent.on('registrationFailed', fail);
                agent.on('disconnected', fail);
                agent.on('unregistered', fail);
                agent.on('newRTCSession', function (event) {
                    if (event.originator !== 'remote') return;
                    if (activeSession) {
                        event.session.terminate({ status_code: 486, reason_phrase: 'Busy Here' });
                        return;
                    }

                    var session = event.session;
                    var remoteUri = session.remote_identity && session.remote_identity.uri;
                    var caller = remoteUri && remoteUri.user ? remoteUri.user : 'Неизвестный номер';
                    showCall(caller);
                    setStatus('Входящий звонок…');
                    showIncomingControls();
                    attachSession(session, 'incoming');
                });
                agent.start();
            } catch (error) {
                fail({ cause: error && error.message });
            }
        });
        return registrationPromise;
    }

    function attachSession(session, direction) {
        activeSession = session;
        activeDirection = direction || 'outgoing';
        if (hangupButton) hangupButton.disabled = false;

        var iceCandidateHandler = session._orbitaIceCandidateHandler;
        if (!iceCandidateHandler) {
            iceCandidateHandler = createIceCandidateHandler();
            session._orbitaIceCandidateHandler = iceCandidateHandler;
            session.on('icecandidate', iceCandidateHandler);
        }

        var attachedPeerConnection = null;
        var fallbackRemoteStream = typeof window.MediaStream === 'function'
            ? new window.MediaStream()
            : null;
        var playbackErrorShown = false;
        var sipEstablished = false;
        var iceConnected = false;
        var iceFailureShown = false;
        var iceTimer = null;

        function clearIceTimer() {
            if (!iceTimer) return;
            window.clearTimeout(iceTimer);
            iceTimer = null;
        }

        function failIceConnection() {
            if (iceFailureShown || activeSession !== session) return;
            iceFailureShown = true;
            clearIceTimer();
            var message = 'Не удалось установить аудиоканал. Проверьте STUN/TURN и сетевые правила.';
            setStatus(message);
            toast(message, 'error');
            try { session.terminate(); } catch (e) { closeCall(2600); }
        }

        function waitForIceConnection(delay) {
            clearIceTimer();
            iceTimer = window.setTimeout(failIceConnection, delay || 15000);
        }

        function updateEstablishedStatus() {
            if (!sipEstablished) return;
            if (iceConnected) {
                clearIceTimer();
                setStatus('Разговор идёт');
            } else {
                setStatus('Устанавливаем аудиоканал…');
                waitForIceConnection(15000);
            }
        }

        function playRemoteAudio() {
            if (!remoteAudio || !remoteAudio.srcObject) return;
            remoteAudio.muted = false;
            remoteAudio.volume = 1;
            var playPromise = remoteAudio.play();
            if (playPromise && typeof playPromise.catch === 'function') {
                playPromise.catch(function () {
                    if (playbackErrorShown) return;
                    playbackErrorShown = true;
                    setStatus('Браузер заблокировал звук. Нажмите на страницу и повторите звонок.');
                    toast('Браузер заблокировал воспроизведение звука звонка.', 'error');
                });
            }
        }

        function attachRemoteTrack(trackEvent) {
            if (!remoteAudio) return;

            if (trackEvent.streams && trackEvent.streams[0]) {
                remoteAudio.srcObject = trackEvent.streams[0];
            } else if (fallbackRemoteStream && trackEvent.track) {
                var alreadyAdded = fallbackRemoteStream.getTracks().some(function (track) {
                    return track.id === trackEvent.track.id;
                });
                if (!alreadyAdded) fallbackRemoteStream.addTrack(trackEvent.track);
                remoteAudio.srcObject = fallbackRemoteStream;
            }

            playRemoteAudio();
        }

        function attachPeerConnection(pc) {
            if (!pc || pc === attachedPeerConnection) return;
            attachedPeerConnection = pc;
            pc.addEventListener('track', attachRemoteTrack);
            pc.addEventListener('iceconnectionstatechange', function () {
                var state = pc.iceConnectionState;
                if (state === 'connected' || state === 'completed') {
                    iceConnected = true;
                    clearIceTimer();
                    updateEstablishedStatus();
                } else if (state === 'failed') {
                    iceConnected = false;
                    failIceConnection();
                } else if (state === 'disconnected') {
                    iceConnected = false;
                    if (sipEstablished) {
                        setStatus('Аудиоканал прерван, переподключаемся…');
                        waitForIceConnection(8000);
                    }
                }
            });

            if (pc.iceConnectionState === 'connected' || pc.iceConnectionState === 'completed') {
                iceConnected = true;
            }

            // For an outgoing JsSIP call the peer connection can already exist
            // by the time ua.call() returns, so its event may have fired early.
            if (typeof pc.getReceivers === 'function') {
                pc.getReceivers().forEach(function (receiver) {
                    if (receiver.track && receiver.track.kind === 'audio') {
                        attachRemoteTrack({ track: receiver.track, streams: [] });
                    }
                });
            }
        }

        session.on('peerconnection', function (event) {
            attachPeerConnection(event && event.peerconnection);
        });
        attachPeerConnection(session.connection);
        session.on('progress', function () {
            setStatus(activeDirection === 'incoming' ? 'Входящий звонок…' : 'Идёт вызов…');
        });
        session.on('accepted', function () {
            showActiveCallControls();
            sipEstablished = true;
            updateEstablishedStatus();
            playRemoteAudio();
        });
        session.on('confirmed', function () {
            showActiveCallControls();
            sipEstablished = true;
            updateEstablishedStatus();
            playRemoteAudio();
        });
        session.on('ended', function () {
            clearIceTimer();
            if (iceCandidateHandler.dispose) iceCandidateHandler.dispose();
            activeSession = null;
            setStatus('Звонок завершён');
            closeCall(1400);
        });
        session.on('failed', function (event) {
            clearIceTimer();
            if (iceCandidateHandler.dispose) iceCandidateHandler.dispose();
            var directionAtFailure = activeDirection;
            activeSession = null;
            var cause = event && event.cause ? ' (' + event.cause + ')' : '';
            var rejectedLocally = directionAtFailure === 'incoming' && event && event.originator === 'local';
            var message = rejectedLocally ? 'Входящий звонок отклонён' : 'Звонок не состоялся' + cause;
            setStatus(message);
            if (!rejectedLocally) toast(message, 'error');
            closeCall(2600);
        });
    }

    async function call(phone) {
        var digits = normalizePhone(phone);
        if (!digits) {
            toast('В карточке указан некорректный номер.', 'error');
            return;
        }
        if (activeSession) {
            toast('Сначала завершите текущий звонок.', 'error');
            return;
        }
        if (!navigator.mediaDevices || typeof navigator.mediaDevices.getUserMedia !== 'function') {
            toast('Браузер не разрешает доступ к микрофону. Откройте Орбиту по HTTPS или через localhost.', 'error');
            return;
        }

        showCall(phone);
        setStatus('Подключаем SIP-линию…');
        if (hangupButton) hangupButton.disabled = true;

        try {
            var config = await loadConfig();
            var registeredUa = await ensureRegistered(config);
            setStatus('Запрашиваем доступ к микрофону…');
            var target = 'sip:' + digits + '@' + config.sipDomain;
            var iceCandidateHandler = createIceCandidateHandler();
            var session = registeredUa.call(target, {
                mediaConstraints: { audio: true, video: false },
                pcConfig: peerConnectionConfig,
                rtcOfferConstraints: { offerToReceiveAudio: true, offerToReceiveVideo: false },
                eventHandlers: { icecandidate: iceCandidateHandler }
            });
            session._orbitaIceCandidateHandler = iceCandidateHandler;
            attachSession(session, 'outgoing');
            setStatus('Набираем номер…');
        } catch (error) {
            var message = error && error.message ? error.message : 'Не удалось начать звонок.';
            setStatus(message);
            toast(message, 'error');
            closeCall(2600);
        }
    }

    document.addEventListener('click', function (event) {
        var button = event.target.closest('[data-orbita-call]');
        if (!button) return;
        event.preventDefault();
        var details = button.closest('details');
        if (details) details.removeAttribute('open');
        call(button.dataset.phone);
    });

    if (hangupButton) {
        hangupButton.addEventListener('click', function () {
            if (activeSession) {
                try { activeSession.terminate(); } catch (e) { }
                return;
            }
            closeCall();
        });
    }

    if (answerButton) {
        answerButton.addEventListener('click', function () {
            if (!activeSession || activeDirection !== 'incoming') return;
            if (!navigator.mediaDevices || typeof navigator.mediaDevices.getUserMedia !== 'function') {
                toast('Браузер не разрешает доступ к микрофону. Откройте Орбиту по HTTPS или через localhost.', 'error');
                return;
            }

            try {
                setStatus('Подключаем микрофон…');
                activeSession.answer({
                    mediaConstraints: { audio: true, video: false },
                    pcConfig: peerConnectionConfig,
                    rtcOfferConstraints: { offerToReceiveAudio: true, offerToReceiveVideo: false }
                });
                showActiveCallControls();
            } catch (error) {
                var message = error && error.message ? error.message : 'Не удалось принять звонок.';
                setStatus(message);
                toast(message, 'error');
                try { activeSession.terminate(); } catch (e) { }
            }
        });
    }

    async function initializeIncomingLine() {
        try {
            var config = await loadConfig();
            startRegistrationWatchdog();
            await ensureRegistered(config);
        } catch (error) {
            // У части ролей и окружений браузерная телефония не настроена.
            // Исходящий звонок покажет ошибку пользователю при явном действии.
        }
    }

    async function checkRegistrationHealth() {
        if (shuttingDown || !currentConfig || registrationPromise || isUserAgentHealthy(ua)) return;
        try {
            await ensureRegistered(currentConfig);
        } catch (error) {
            scheduleRegistrationRecovery(currentConfig, ua);
        }
    }

    function startRegistrationWatchdog() {
        if (healthTimer) return;
        healthTimer = window.setInterval(checkRegistrationHealth, registrationHealthIntervalMs);
    }

    document.addEventListener('visibilitychange', function () {
        if (!document.hidden) checkRegistrationHealth();
    });
    window.addEventListener('focus', checkRegistrationHealth);
    window.addEventListener('online', checkRegistrationHealth);
    window.addEventListener('pageshow', checkRegistrationHealth);
    window.addEventListener('beforeunload', function () {
        shuttingDown = true;
        if (healthTimer) window.clearInterval(healthTimer);
        healthTimer = null;
        stopUserAgent();
    });

    initializeIncomingLine();
})();
