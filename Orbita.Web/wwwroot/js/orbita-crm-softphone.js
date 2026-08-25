(function () {
    'use strict';

    var ua = null;
    var uaKey = '';
    var registrationPromise = null;
    var activeSession = null;
    var activeDirection = '';
    var closeTimer = null;
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
        document.body.classList.add('orbita-softphone-open');
    }

    function closeCall(delay) {
        if (closeTimer) window.clearTimeout(closeTimer);
        closeTimer = window.setTimeout(function () {
            closeTimer = null;
            if (modal) modal.setAttribute('hidden', '');
            document.body.classList.remove('orbita-softphone-open');
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
        return response.json();
    }

    function stopUserAgent() {
        registrationPromise = null;
        if (ua) {
            try { ua.stop(); } catch (e) { }
        }
        ua = null;
        uaKey = '';
    }

    function ensureRegistered(config) {
        var key = [config.webSocketUrl, config.sipUri, config.authorizationUsername].join('|');
        if (ua && uaKey === key && ua.isRegistered && ua.isRegistered()) {
            return Promise.resolve(ua);
        }
        if (ua && uaKey !== key) stopUserAgent();
        if (registrationPromise) return registrationPromise;
        if (!window.JsSIP || !window.JsSIP.WebSocketInterface || !window.JsSIP.UA) {
            return Promise.reject(new Error('SIP-клиент не загрузился. Обновите страницу.'));
        }

        registrationPromise = new Promise(function (resolve, reject) {
            var settled = false;
            var timeout = window.setTimeout(function () {
                if (settled) return;
                settled = true;
                registrationPromise = null;
                reject(new Error('SIP-линия не ответила за 15 секунд.'));
            }, 15000);

            function succeed() {
                if (settled) return;
                settled = true;
                window.clearTimeout(timeout);
                registrationPromise = null;
                resolve(ua);
            }

            function fail(event) {
                if (settled) return;
                settled = true;
                window.clearTimeout(timeout);
                registrationPromise = null;
                var cause = event && event.cause ? ': ' + event.cause : '';
                reject(new Error('Не удалось зарегистрировать SIP-линию' + cause + '.'));
            }

            try {
                var socket = new window.JsSIP.WebSocketInterface(config.webSocketUrl);
                ua = new window.JsSIP.UA({
                    sockets: [socket],
                    uri: config.sipUri,
                    authorization_user: config.authorizationUsername,
                    password: config.password,
                    register: true,
                    session_timers: false
                });
                uaKey = key;
                ua.on('registered', succeed);
                ua.on('registrationFailed', fail);
                ua.on('disconnected', fail);
                ua.on('newRTCSession', function (event) {
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
                ua.start();
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

        var attachedPeerConnection = null;
        var fallbackRemoteStream = typeof window.MediaStream === 'function'
            ? new window.MediaStream()
            : null;
        var playbackErrorShown = false;

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
            setStatus(activeDirection === 'incoming' ? 'Звонок принят' : 'Собеседник ответил');
            playRemoteAudio();
        });
        session.on('confirmed', function () {
            showActiveCallControls();
            setStatus('Разговор идёт');
            playRemoteAudio();
        });
        session.on('ended', function () {
            activeSession = null;
            setStatus('Звонок завершён');
            closeCall(1400);
        });
        session.on('failed', function (event) {
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
            var session = registeredUa.call(target, {
                mediaConstraints: { audio: true, video: false },
                pcConfig: { iceServers: [] },
                rtcOfferConstraints: { offerToReceiveAudio: true, offerToReceiveVideo: false }
            });
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
                    pcConfig: { iceServers: [] },
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
            await ensureRegistered(config);
        } catch (error) {
            // У части ролей и окружений браузерная телефония не настроена.
            // Исходящий звонок покажет ошибку пользователю при явном действии.
        }
    }

    initializeIncomingLine();
})();
