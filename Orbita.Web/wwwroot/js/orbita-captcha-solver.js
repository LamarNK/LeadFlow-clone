(function () {
    var modal = null;
    var viewport = null;
    var previewImg = null;
    var previewObjectUrls = [];
    var statusEl = null;
    var loadingEl = null;
    var loadingTextEl = null;
    var connection = null;
    var accessToken = null;
    var captchaHubUrl = null;
    var activeSession = null;
    var activeSessionCanCancel = false;
    var activeSourceEventId = '';
    var activeSourceDismissUrl = '/Errors/Dismiss';
    var activeSourceRowSelector = '.errors-row';
    var autoDismissPromise = null;
    var resolutionHandled = false;
    var closeTimer = 0;
    var workerLocks = {};
    var snapshotCount = 0;
    var liveFrameCount = 0;
    var firstLiveFrameShown = false;
    var joinPromise = null;
    var pendingFrame = null;
    var frameRenderPromise = null;
    var pendingSnapshot = null;
    var snapshotRenderPromise = null;
    var lastLiveFrameAt = 0;
    var activePointerId = null;
    var pendingMoveMessage = null;
    var moveFlushTimer = 0;
    var lastMoveSentAt = 0;
    var mouseMoveThrottleMs = 16;
    var sessionFlowSeq = 0;
    var currentFlow = null;

    function ensureModal() {
        if (modal) return;
        modal = document.createElement('div');
        modal.className = 'orbita-captcha-modal';
        modal.setAttribute('hidden', '');
        modal.innerHTML =
            '<div class="orbita-captcha-modal__backdrop" data-captcha-close></div>' +
            '<div class="orbita-captcha-modal__dialog" role="dialog" aria-modal="true">' +
            '<header class="orbita-captcha-modal__head">' +
            '<div><h2 class="orbita-captcha-modal__title">Пройти капчу</h2>' +
            '<p class="orbita-captcha-modal__subtitle" data-captcha-subtitle></p></div>' +
            '<button type="button" class="orbita-captcha-modal__close" data-captcha-close aria-label="Закрыть">' +
            '<i class="fa-solid fa-xmark" aria-hidden="true"></i></button></header>' +
            '<div class="orbita-captcha-modal__status" data-captcha-status>Ожидание подключения…</div>' +
            '<div class="orbita-captcha-modal__viewport" data-captcha-viewport>' +
            '<div class="orbita-captcha-modal__loading" data-captcha-loading>' +
            '<i class="fa-solid fa-spinner fa-spin" aria-hidden="true"></i>' +
            '<span data-captcha-loading-text>Загружаем изображение из браузера…</span>' +
            '</div>' +
            '<img class="orbita-captcha-modal__frame" alt="Снимок страницы капчи" draggable="false" />' +
            '</div>' +
            '<footer class="orbita-captcha-modal__foot">' +
            '<button type="button" class="orbita-detail-modal__action orbita-detail-modal__action--ghost" data-captcha-close>Отмена</button>' +
            '</footer></div>';
        document.body.appendChild(modal);
        viewport = modal.querySelector('[data-captcha-viewport]');
        viewport.setAttribute('tabindex', '0');
        viewport.setAttribute('role', 'application');
        viewport.setAttribute('aria-label', 'Область управления капчей');
        previewImg = modal.querySelector('.orbita-captcha-modal__frame');
        statusEl = modal.querySelector('[data-captcha-status]');
        loadingEl = modal.querySelector('[data-captcha-loading]');
        loadingTextEl = modal.querySelector('[data-captcha-loading-text]');
        modal.querySelectorAll('[data-captcha-close]').forEach(function (btn) {
            btn.addEventListener('click', close);
        });
        bindInputEvents();
    }

    function bindInputEvents() {
        if (!viewport) return;
        if (window.PointerEvent) {
            viewport.addEventListener('pointerdown', handlePointerDown);
            viewport.addEventListener('pointermove', handlePointerMove);
            viewport.addEventListener('pointerup', handlePointerUp);
            viewport.addEventListener('pointercancel', handlePointerCancel);
        } else {
            bindLegacyMouseEvents();
        }
        ['dragstart', 'selectstart', 'contextmenu'].forEach(function (type) {
            viewport.addEventListener(type, function (e) {
                e.preventDefault();
            });
        });
        ['keydown', 'keyup'].forEach(function (type) {
            viewport.addEventListener(type, function (e) {
                if (!canRelayKeyboard() || e.isComposing) return;
                e.preventDefault();
                e.stopPropagation();
                sendKeyboardInput(type, e);
            });
        });
    }

    function canSendInput() {
        return !!activeSession
            && !!connection
            && connection.state === signalR.HubConnectionState.Connected;
    }

    function canRelayKeyboard() {
        return canSendInput() && firstLiveFrameShown;
    }

    function focusViewport() {
        if (viewport && !modal.hasAttribute('hidden')) {
            viewport.focus({ preventScroll: true });
        }
    }

    function bindLegacyMouseEvents() {
        ['mousedown', 'mousemove', 'mouseup'].forEach(function (type) {
            viewport.addEventListener(type, function (e) {
                if (!canSendInput()) return;
                if (type === 'mousemove' && !e.buttons) return;
                e.preventDefault();
                e.stopPropagation();
                focusViewport();
                if (type === 'mousemove') {
                    queueMouseMove(buildMouseInputMessage(type, e));
                    return;
                }
                if (type === 'mouseup') {
                    flushPendingMouseMove(true);
                }
                sendInputMessage(buildMouseInputMessage(type, e));
            });
        });
    }

    function isRelayPointer(e) {
        return !e.pointerType || e.pointerType === 'mouse' || e.pointerType === 'pen';
    }

    function handlePointerDown(e) {
        if (!isRelayPointer(e) || !canSendInput()) return;
        e.preventDefault();
        e.stopPropagation();
        focusViewport();
        resetMoveQueue();
        activePointerId = e.pointerId;
        if (viewport.setPointerCapture) {
            try {
                viewport.setPointerCapture(e.pointerId);
            } catch (err) { }
        }
        sendInputMessage(buildMouseInputMessage('mousedown', e));
    }

    function handlePointerMove(e) {
        if (!isRelayPointer(e) || !canSendInput()) return;
        if (activePointerId !== null && e.pointerId !== activePointerId) return;
        if (!e.buttons) return;
        e.preventDefault();
        e.stopPropagation();
        focusViewport();
        queueMouseMove(buildMouseInputMessage('mousemove', e));
    }

    function handlePointerUp(e) {
        if (!isRelayPointer(e)) return;
        if (activePointerId !== null && e.pointerId !== activePointerId) return;
        e.preventDefault();
        e.stopPropagation();
        focusViewport();
        flushPendingMouseMove(true);
        if (canSendInput()) {
            sendInputMessage(buildMouseInputMessage('mouseup', e));
        }
        releasePointer(e.pointerId);
        activePointerId = null;
    }

    function handlePointerCancel(e) {
        if (!isRelayPointer(e)) return;
        if (activePointerId !== null && e.pointerId !== activePointerId) return;
        e.preventDefault();
        e.stopPropagation();
        flushPendingMouseMove(true);
        if (canSendInput()) {
            sendInputMessage(buildMouseInputMessage('mouseup', e));
        }
        releasePointer(e.pointerId);
        activePointerId = null;
    }

    function releasePointer(pointerId) {
        if (!viewport || !viewport.releasePointerCapture) return;
        try {
            viewport.releasePointerCapture(pointerId);
        } catch (err) { }
    }

    function buildMouseInputMessage(type, e) {
        var rect = viewport.getBoundingClientRect();
        return {
            sessionId: getSessionId(activeSession),
            eventType: type,
            x: e.clientX - rect.left,
            y: e.clientY - rect.top,
            timestampMs: Date.now(),
            panelWidth: rect.width,
            panelHeight: rect.height,
            button: typeof e.button === 'number' ? e.button : 0,
            buttons: typeof e.buttons === 'number' ? e.buttons : 0
        };
    }

    function queueMouseMove(message) {
        pendingMoveMessage = message;
        schedulePendingMouseMove();
    }

    function schedulePendingMouseMove() {
        if (!pendingMoveMessage) return;
        if (moveFlushTimer) return;
        var delay = Math.max(0, mouseMoveThrottleMs - (Date.now() - lastMoveSentAt));
        moveFlushTimer = window.setTimeout(function () {
            moveFlushTimer = 0;
            flushPendingMouseMove(true);
        }, delay);
    }

    function flushPendingMouseMove(force) {
        if (moveFlushTimer) {
            window.clearTimeout(moveFlushTimer);
            moveFlushTimer = 0;
        }
        if (!pendingMoveMessage) return;
        if (!canSendInput()) {
            pendingMoveMessage = null;
            return;
        }
        if (!force) {
            schedulePendingMouseMove();
            return;
        }
        var message = pendingMoveMessage;
        pendingMoveMessage = null;
        lastMoveSentAt = Date.now();
        sendInputMessage(message);
    }

    function resetMoveQueue() {
        activePointerId = null;
        pendingMoveMessage = null;
        lastMoveSentAt = 0;
        if (moveFlushTimer) {
            window.clearTimeout(moveFlushTimer);
            moveFlushTimer = 0;
        }
    }

    function sendInputMessage(message) {
        if (!canSendInput()) {
            return Promise.resolve();
        }
        return connection.send('SendInput', message).catch(function () { });
    }

    function sendKeyboardInput(type, e) {
        var rect = viewport.getBoundingClientRect();
        sendInputMessage({
            sessionId: getSessionId(activeSession),
            eventType: type,
            x: 0,
            y: 0,
            timestampMs: Date.now(),
            panelWidth: rect.width,
            panelHeight: rect.height,
            button: 0,
            buttons: 0,
            key: e.key,
            code: e.code,
            altKey: !!e.altKey,
            ctrlKey: !!e.ctrlKey,
            shiftKey: !!e.shiftKey,
            metaKey: !!e.metaKey,
            repeat: !!e.repeat
        });
    }

    function getSessionId(session) {
        return session && (session.id || session.Id || session.sessionId || session.SessionId);
    }

    function normalizeGuid(value) {
        return String(value || '').trim().toLowerCase();
    }

    function readSnapshotMessage(message) {
        return {
            sessionId: message.sessionId || message.SessionId,
            payloadBase64: message.mhtmlGzipBase64 || message.MhtmlGzipBase64,
            contentType: message.contentType || message.ContentType || 'image/jpeg'
        };
    }

    function readFrameMessage(message) {
        return {
            sessionId: message.sessionId || message.SessionId,
            imageBase64: message.imageBase64 || message.ImageBase64,
            contentType: message.contentType || message.ContentType || 'image/jpeg'
        };
    }

    function base64ToBytes(b64) {
        var binary = atob(b64);
        var bytes = new Uint8Array(binary.length);
        for (var i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
        return bytes;
    }

    async function gunzipBytes(bytes) {
        if (typeof DecompressionStream === 'undefined') {
            throw new Error('Браузер не поддерживает распаковку gzip.');
        }
        var stream = new DecompressionStream('gzip');
        var writer = stream.writable.getWriter();
        await writer.write(bytes);
        await writer.close();
        return new Uint8Array(await new Response(stream.readable).arrayBuffer());
    }

    function looksLikeJpeg(bytes) {
        return bytes.length >= 3 && bytes[0] === 0xFF && bytes[1] === 0xD8 && bytes[2] === 0xFF;
    }

    function looksLikePng(bytes) {
        return bytes.length >= 8
            && bytes[0] === 0x89
            && bytes[1] === 0x50
            && bytes[2] === 0x4E
            && bytes[3] === 0x47
            && bytes[4] === 0x0D
            && bytes[5] === 0x0A
            && bytes[6] === 0x1A
            && bytes[7] === 0x0A;
    }

    function looksLikeGif(bytes) {
        return bytes.length >= 6
            && bytes[0] === 0x47
            && bytes[1] === 0x49
            && bytes[2] === 0x46
            && bytes[3] === 0x38
            && (bytes[4] === 0x37 || bytes[4] === 0x39)
            && bytes[5] === 0x61;
    }

    function looksLikeWebp(bytes) {
        return bytes.length >= 12
            && bytes[0] === 0x52
            && bytes[1] === 0x49
            && bytes[2] === 0x46
            && bytes[3] === 0x46
            && bytes[8] === 0x57
            && bytes[9] === 0x45
            && bytes[10] === 0x42
            && bytes[11] === 0x50;
    }

    function detectImageContentType(bytes) {
        if (looksLikeJpeg(bytes)) return 'image/jpeg';
        if (looksLikePng(bytes)) return 'image/png';
        if (looksLikeGif(bytes)) return 'image/gif';
        if (looksLikeWebp(bytes)) return 'image/webp';
        return null;
    }

    function decodeUtf8(bytes) {
        return new TextDecoder().decode(bytes);
    }

    function looksLikeHtml(text) {
        var normalized = String(text || '').trimStart().toLowerCase();
        return normalized.indexOf('<!doctype html') === 0
            || normalized.indexOf('<html') === 0
            || normalized.indexOf('<body') === 0;
    }

    function looksLikeMhtml(text) {
        var normalized = String(text || '').trimStart().toLowerCase();
        return normalized.indexOf('mime-version:') === 0
            || normalized.indexOf('content-type: multipart/related') === 0;
    }

    async function decodeSnapshotBytes(payloadBase64) {
        var rawBytes = base64ToBytes(payloadBase64);
        try {
            var decompressed = await gunzipBytes(rawBytes);
            if (detectImageContentType(decompressed)) {
                return decompressed;
            }
            var decompressedText = decodeUtf8(decompressed);
            if (looksLikeHtml(decompressedText) || looksLikeMhtml(decompressedText)) {
                return decompressed;
            }
        } catch (e) {
            // Fall through to raw payload attempt.
        }

        if (detectImageContentType(rawBytes)) {
            return rawBytes;
        }

        var rawText = decodeUtf8(rawBytes);
        if (looksLikeHtml(rawText) || looksLikeMhtml(rawText)) {
            return rawBytes;
        }

        throw new Error('Не удалось распознать снимок страницы.');
    }

    function revokePreviewUrls() {
        if (!previewObjectUrls.length) return;
        previewObjectUrls.forEach(function (url) {
            URL.revokeObjectURL(url);
        });
        previewObjectUrls = [];
    }

    function rememberPreviewUrl(url) {
        if (url) {
            previewObjectUrls.push(url);
        }
    }

    function ensureImagePreviewFrame() {
        if (!viewport) return null;
        var current = viewport.querySelector('.orbita-captcha-modal__frame');
        if (current && current.tagName === 'IMG') {
            previewImg = current;
            return current;
        }

        if (current) {
            current.remove();
        }

        var img = document.createElement('img');
        img.className = 'orbita-captcha-modal__frame';
        img.alt = 'Снимок страницы капчи';
        img.draggable = false;
        viewport.appendChild(img);
        previewImg = img;
        return img;
    }

    function resetPreviewFrame() {
        if (!viewport) return;
        resetMoveQueue();
        clearCloseTimer();
        revokePreviewUrls();
        pendingFrame = null;
        pendingSnapshot = null;
        lastLiveFrameAt = 0;
        firstLiveFrameShown = false;
        activeSessionCanCancel = false;
        resolutionHandled = false;
        autoDismissPromise = null;
        setLoading(true, 'Загружаем изображение из браузера…');
        var current = viewport.querySelector('.orbita-captcha-modal__frame');
        if (current && current.tagName === 'IMG') {
            current.onload = null;
            current.onerror = null;
            current.removeAttribute('src');
            previewImg = current;
            return;
        }
        if (current) {
            current.remove();
        }
        var img = document.createElement('img');
        img.className = 'orbita-captcha-modal__frame';
        img.alt = 'Снимок страницы капчи';
        img.draggable = false;
        viewport.appendChild(img);
        previewImg = img;
    }

    function createSessionFlow() {
        var flow = {
            id: ++sessionFlowSeq,
            closeRequested: false,
            cancelIssued: false,
            canCancel: false,
            sessionId: ''
        };
        currentFlow = flow;
        return flow;
    }

    function isCurrentFlow(flow) {
        return !!flow && !!currentFlow && flow.id === currentFlow.id;
    }

    async function cancelSessionById(sessionId) {
        if (!sessionId) {
            return;
        }

        var form = new FormData();
        form.append('id', sessionId);
        form.append('__RequestVerificationToken', getCsrfToken());
        await fetch('/Captcha/Cancel/' + sessionId, { method: 'POST', body: form, credentials: 'same-origin' });
    }

    async function cancelFlowSession(flow) {
        if (!flow || flow.cancelIssued || !flow.canCancel || !flow.sessionId) {
            return;
        }

        flow.cancelIssued = true;
        flow.canCancel = false;
        try {
            await cancelSessionById(flow.sessionId);
        } catch (e) { /* ignore */ }
    }

    function isSnapshotForActiveSession(sessionId) {
        return !!activeSession
            && normalizeGuid(sessionId) === normalizeGuid(getSessionId(activeSession));
    }

    function queueSnapshot(snapshot) {
        pendingSnapshot = snapshot;
        if (!snapshotRenderPromise) {
            snapshotRenderPromise = processPendingSnapshots();
        }
    }

    function queueFrame(frame) {
        pendingFrame = frame;
        if (!frameRenderPromise) {
            frameRenderPromise = processPendingFrames();
        }
    }

    async function processPendingFrames() {
        try {
            while (pendingFrame) {
                var frame = pendingFrame;
                pendingFrame = null;

                if (!frame.imageBase64 || !isSnapshotForActiveSession(frame.sessionId)) {
                    continue;
                }

                try {
                    await renderFrame(frame);
                } catch (err) {
                    if (isSnapshotForActiveSession(frame.sessionId)) {
                        setLoading(false);
                        setStatus('Ошибка отображения live кадра: ' + (err.message || String(err)));
                    }
                }
            }
        } finally {
            frameRenderPromise = null;
            if (pendingFrame && activeSession) {
                frameRenderPromise = processPendingFrames();
            }
        }
    }

    async function processPendingSnapshots() {
        try {
            while (pendingSnapshot) {
                var snapshot = pendingSnapshot;
                pendingSnapshot = null;

                if (!snapshot.payloadBase64 || !isSnapshotForActiveSession(snapshot.sessionId)) {
                    continue;
                }

                try {
                    await renderSnapshot(snapshot);
                } catch (err) {
                    if (isSnapshotForActiveSession(snapshot.sessionId)) {
                        setLoading(false);
                        setStatus('Ошибка отображения: ' + (err.message || String(err)));
                    }
                }
            }
        } finally {
            snapshotRenderPromise = null;
            if (pendingSnapshot && activeSession) {
                snapshotRenderPromise = processPendingSnapshots();
            }
        }
    }

    async function renderSnapshot(snapshot) {
        var bytes = await decodeSnapshotBytes(snapshot.payloadBase64);
        var detectedImageType = detectImageContentType(bytes);

        if (detectedImageType) {
            await renderImageSnapshot(bytes, detectedImageType, snapshot.sessionId);
            return;
        }

        var html = decodeUtf8(bytes);
        if (looksLikeHtml(html)) {
            renderHtmlSnapshot(html, snapshot.sessionId);
            return;
        }

        if (looksLikeMhtml(html)) {
            throw new Error('Воркер прислал MHTML-архив вместо скриншота. Обновите воркер до версии со снимками JPEG.');
        }

        throw new Error('Не удалось распознать снимок страницы.');
    }

    async function renderFrame(frame) {
        await renderLiveFrame(frame.imageBase64, frame.contentType, frame.sessionId);
    }

    function renderImageSnapshot(bytes, contentType, sessionId) {
        var frame = ensureImagePreviewFrame();
        if (!frame) {
            return Promise.resolve();
        }

        var objectUrl = URL.createObjectURL(new Blob([bytes], { type: contentType || 'image/jpeg' }));
        rememberPreviewUrl(objectUrl);

        return loadImageFrame(frame, objectUrl, sessionId, function () {
            setLoading(false);
            snapshotCount++;
            if (snapshotCount === 1) {
                setStatus('Потяните ползунок GeeTest в области ниже.');
            }
        }, 'Снимок получен, но браузер не смог его отобразить.');
    }

    function renderLiveFrame(imageBase64, contentType, sessionId) {
        var frame = ensureImagePreviewFrame();
        if (!frame) {
            return Promise.resolve();
        }

        return loadImageFrame(
            frame,
            'data:' + (contentType || 'image/jpeg') + ';base64,' + imageBase64,
            sessionId,
            function () {
                setLoading(false);
                liveFrameCount++;
                lastLiveFrameAt = Date.now();
                if (!firstLiveFrameShown) {
                    firstLiveFrameShown = true;
                    focusViewport();
                    setStatus('Потяните ползунок GeeTest в области ниже.');
                }
            },
            'Live кадр получен, но браузер не смог его отобразить.');
    }

    function loadImageFrame(frame, source, sessionId, onReady, errorStatus) {
        var render = window.OrbitaBrowserFrame && window.OrbitaBrowserFrame.loadImageFrame;
        if (!render) {
            render = function (target, src, ready, error) {
                return new Promise(function (resolve) {
                    var settled = false;
                    function finish(success) {
                        if (settled) return;
                        settled = true;
                        target.onload = null;
                        target.onerror = null;
                        if (success) {
                            if (ready) ready();
                        } else if (error) {
                            error();
                        }
                        resolve();
                    }
                    target.onload = function () { finish(true); };
                    target.onerror = function () { finish(false); };
                    target.src = src;
                    window.requestAnimationFrame(function () {
                        if (target.complete && target.naturalWidth > 0) finish(true);
                    });
                });
            };
        }

        return render(
            frame,
            source,
            function () {
                if (!isSnapshotForActiveSession(sessionId)) {
                    return;
                }
                onReady();
            },
            function () {
                if (!isSnapshotForActiveSession(sessionId)) {
                    return;
                }
                setStatus(errorStatus);
            });
    }

    function renderHtmlSnapshot(html, sessionId) {
        if (!isSnapshotForActiveSession(sessionId) || !previewImg) {
            return;
        }

        revokePreviewUrls();
        var frame = document.createElement('iframe');
        frame.className = 'orbita-captcha-modal__frame';
        frame.title = 'Страница капчи';
        frame.sandbox = 'allow-same-origin allow-scripts';
        frame.srcdoc = html;
        previewImg.replaceWith(frame);
        previewImg = frame;
        setLoading(false);
        snapshotCount++;
        setStatus('Потяните ползунок GeeTest в области ниже.');
    }

    async function refreshAccessToken() {
        var tokenResp = await fetch('/Realtime/AccessToken', { credentials: 'same-origin' });
        if (!tokenResp.ok) throw new Error('Не удалось получить токен realtime.');
        var tokenPayload = await tokenResp.json();
        accessToken = tokenPayload.accessToken;
        captchaHubUrl = tokenPayload.captchaHubUrl
            || (tokenPayload.hubUrl ? tokenPayload.hubUrl.replace('/hubs/panel', '/hubs/captcha') : null);
        if (!captchaHubUrl) {
            throw new Error('Не настроен URL captcha hub.');
        }
        return tokenPayload;
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

        hub.on('Snapshot', function (message) {
            handleSnapshot(message);
        });
        hub.on('Frame', function (message) {
            handleFrame(message);
        });
        hub.on('StateChanged', function (message) {
            handleStateChanged(message);
        });
        hub.on('WorkerCaptchaLockChanged', function (message) {
            workerLocks[message.workerId || message.WorkerId] = message.lock || message.Lock;
        });
        hub.onreconnected(function () {
            rejoinActiveSession().catch(function (err) {
                setStatus('Переподключение: ' + (err.message || String(err)));
            });
        });
        hub.onclose(function (err) {
            if (activeSession) {
                setStatus('Соединение с captcha hub закрыто' + (err ? ': ' + err.message : '.'));
            }
        });

        return hub;
    }

    async function rejoinActiveSession() {
        if (!activeSession || !connection || connection.state !== signalR.HubConnectionState.Connected) return;
        await refreshAccessToken();
        var sessionId = getSessionId(activeSession);
        if (!sessionId) return;
        await connection.invoke('JoinAsOperator', sessionId);
    }

    async function joinActiveSession() {
        if (!activeSession || !connection || connection.state !== signalR.HubConnectionState.Connected) return;
        if (joinPromise) return joinPromise;
        joinPromise = rejoinActiveSession().finally(function () {
            joinPromise = null;
        });
        return joinPromise;
    }

    function handleSnapshot(message) {
        var snapshot = readSnapshotMessage(message);
        if (!activeSession
            || normalizeGuid(snapshot.sessionId) !== normalizeGuid(getSessionId(activeSession))
            || !snapshot.payloadBase64) {
            return;
        }

        if (lastLiveFrameAt && Date.now() - lastLiveFrameAt < 1500) {
            return;
        }

        queueSnapshot(snapshot);
    }

    function handleFrame(message) {
        var frame = readFrameMessage(message);
        if (!activeSession
            || normalizeGuid(frame.sessionId) !== normalizeGuid(getSessionId(activeSession))
            || !frame.imageBase64) {
            return;
        }

        queueFrame(frame);
    }

    function handleStateChanged(message) {
        var sessionId = message.sessionId || message.SessionId;
        var status = message.status || message.Status;
        var stateMessage = message.message || message.Message;
        if (!activeSession || normalizeGuid(sessionId) !== normalizeGuid(getSessionId(activeSession))) return;
        if (status === 'completed') {
            resolveCurrentSession(stateMessage || 'Капча пройдена.');
        } else if (status === 'failed' || status === 'expired' || status === 'cancelled') {
            activeSessionCanCancel = false;
            if (currentFlow) {
                currentFlow.canCancel = false;
            }
            setLoading(false);
            if (status === 'failed' && isNoCaptchaMessage(stateMessage)) {
                resolveCurrentSession(stateMessage);
                return;
            }
            setStatus(stateMessage || 'Сессия завершена: ' + status);
        } else {
            setStatus(stateMessage || status);
        }
    }

    async function waitForHubState(targetState, timeoutMs) {
        var deadline = Date.now() + (timeoutMs || 30000);
        while (connection && Date.now() < deadline) {
            if (connection.state === targetState) {
                return true;
            }
            if (connection.state === signalR.HubConnectionState.Disconnected) {
                return false;
            }
            await new Promise(function (resolve) { setTimeout(resolve, 100); });
        }
        return false;
    }

    async function ensureConnection() {
        if (connection) {
            if (connection.state === signalR.HubConnectionState.Connected) {
                return connection;
            }
            if (connection.state === signalR.HubConnectionState.Connecting
                || connection.state === signalR.HubConnectionState.Reconnecting) {
                var connected = await waitForHubState(signalR.HubConnectionState.Connected);
                if (connected) {
                    return connection;
                }
            }
            try {
                await connection.stop();
            } catch (e) { /* ignore */ }
            connection = null;
        }

        await refreshAccessToken();
        connection = buildConnection(captchaHubUrl);
        await connection.start();
        return connection;
    }

    function setStatus(text) {
        if (statusEl) statusEl.textContent = text;
    }

    function setLoading(visible, text) {
        if (loadingTextEl && text) {
            loadingTextEl.textContent = text;
        }
        if (!loadingEl) {
            return;
        }
        if (visible) {
            loadingEl.removeAttribute('hidden');
        } else {
            loadingEl.setAttribute('hidden', '');
        }
    }

    function clearCloseTimer() {
        if (closeTimer) {
            window.clearTimeout(closeTimer);
            closeTimer = 0;
        }
    }

    function scheduleClose(delayMs) {
        clearCloseTimer();
        closeTimer = window.setTimeout(function () {
            close();
        }, delayMs || 0);
    }

    function isNoCaptchaMessage(message) {
        var text = String(message || '').trim().toLowerCase();
        return text.indexOf('капча не обнаружена') >= 0
            || text.indexOf('captcha not found') >= 0;
    }

    function removeDismissedRows(eventId, rowSelector) {
        if (!eventId) {
            return;
        }

        document.querySelectorAll(rowSelector || '.errors-row').forEach(function (row) {
            if (normalizeGuid(row.getAttribute('data-event-id')) !== normalizeGuid(eventId)) {
                return;
            }
            if (row.parentNode) {
                row.parentNode.removeChild(row);
            }
        });
    }

    function scheduleRelatedRefresh() {
        if (!window.OrbitaLive || typeof window.OrbitaLive.scheduleRefresh !== 'function') {
            return;
        }
        window.OrbitaLive.scheduleRefresh({ kinds: ['Errors', 'Events', 'Accounts', 'Dashboard', 'NavBadges'] });
    }

    function dismissSourceError() {
        if (!activeSourceEventId) {
            return Promise.resolve(true);
        }
        if (autoDismissPromise) {
            return autoDismissPromise;
        }
        if (!window.Orbita || typeof window.Orbita.postForm !== 'function') {
            return Promise.resolve(false);
        }

        var eventId = activeSourceEventId;
        var dismissUrl = activeSourceDismissUrl || '/Errors/Dismiss';
        var rowSelector = activeSourceRowSelector || '.errors-row';

        autoDismissPromise = window.Orbita.postForm(dismissUrl, { eventId: eventId })
            .then(function (result) {
                if (!result || !result.ok) {
                    if (window.Orbita && typeof window.Orbita.toast === 'function') {
                        window.Orbita.toast(
                            (result && result.payload && result.payload.error) || 'Не удалось закрыть запись автоматически.',
                            { variant: 'error' });
                    }
                    return false;
                }

                removeDismissedRows(eventId, rowSelector);
                scheduleRelatedRefresh();
                return true;
            })
            .finally(function () {
                autoDismissPromise = null;
            });

        return autoDismissPromise;
    }

    function resolveCurrentSession(statusText) {
        if (resolutionHandled) {
            scheduleClose(1200);
            return;
        }

        resolutionHandled = true;
        activeSessionCanCancel = false;
        if (currentFlow) {
            currentFlow.canCancel = false;
        }
        setLoading(false);
        setStatus(statusText);

        dismissSourceError()
            .finally(function () {
                scheduleClose(1200);
            });
    }

    function getCsrfToken() {
        var token = document.querySelector('input[name="__RequestVerificationToken"]');
        return token ? token.value : '';
    }

    async function startSession(payload) {
        ensureModal();
        var form = new FormData();
        form.append('AccountId', payload.accountId);
        form.append('WorkerId', payload.workerId);
        form.append('PageUrl', payload.pageUrl);
        form.append('CaptchaKind', payload.captchaKind || 'captcha');
        if (payload.subProfileId) form.append('SubProfileId', payload.subProfileId);
        form.append('__RequestVerificationToken', getCsrfToken());

        var resp = await fetch('/Captcha/Start', { method: 'POST', body: form, credentials: 'same-origin' });
        if (resp.status === 409) {
            var conflict = await resp.json();
            throw new Error(conflict.error + (conflict.activeOperatorDisplayName ? ' (' + conflict.activeOperatorDisplayName + ')' : ''));
        }
        if (!resp.ok) {
            var err = await resp.json().catch(function () { return {}; });
            throw new Error(err.error || 'Не удалось создать сессию.');
        }
        return resp.json();
    }

    async function open(payload) {
        ensureModal();
        if (currentFlow || activeSession || (modal && !modal.hasAttribute('hidden'))) {
            await close();
        }
        var flow = createSessionFlow();
        resetPreviewFrame();
        snapshotCount = 0;
        liveFrameCount = 0;
        activeSourceEventId = payload.eventId || '';
        activeSourceDismissUrl = payload.dismissUrl || '/Errors/Dismiss';
        activeSourceRowSelector = payload.rowSelector || '.errors-row';
        modal.removeAttribute('hidden');
        modal.querySelector('[data-captcha-subtitle]').textContent =
            (payload.accountName || 'Аккаунт') + ' · ' + (payload.captchaKind || 'captcha');
        setStatus('Создание сессии…');
        setLoading(true, 'Запускаем браузер и ждём изображение…');
        try {
            var session = await startSession(payload);
            flow.sessionId = getSessionId(session);
            flow.canCancel = !!flow.sessionId;
            if (flow.closeRequested || !isCurrentFlow(flow)) {
                await cancelFlowSession(flow);
                return;
            }

            activeSession = session;
            activeSessionCanCancel = flow.canCancel;
            setStatus('Подключение к воркеру…');
            await ensureConnection();
            if (flow.closeRequested || !isCurrentFlow(flow)) {
                await cancelFlowSession(flow);
                return;
            }
            await joinActiveSession();
            if (flow.closeRequested || !isCurrentFlow(flow)) {
                await cancelFlowSession(flow);
                return;
            }
            if (snapshotCount === 0 && liveFrameCount === 0) {
                setStatus('Ожидание live кадра или снимка страницы с воркера…');
            }
        } catch (err) {
            if (!isCurrentFlow(flow)) {
                return;
            }
            activeSessionCanCancel = !!activeSession;
            setLoading(false);
            setStatus(err.message || String(err));
        }
    }

    async function close() {
        clearCloseTimer();
        var flow = currentFlow;
        var sessionId = getSessionId(activeSession) || (flow ? flow.sessionId : '');
        var shouldCancel = !!sessionId && ((flow && flow.canCancel) || activeSessionCanCancel);
        if (flow) {
            flow.closeRequested = true;
            if (sessionId && !flow.sessionId) {
                flow.sessionId = sessionId;
            }
            if (shouldCancel) {
                flow.canCancel = true;
            }
        }
        currentFlow = null;
        activeSession = null;
        activeSessionCanCancel = false;
        activeSourceEventId = '';
        activeSourceDismissUrl = '/Errors/Dismiss';
        activeSourceRowSelector = '.errors-row';
        snapshotCount = 0;
        liveFrameCount = 0;
        resetPreviewFrame();
        setLoading(false);
        if (modal) modal.setAttribute('hidden', '');
        if (shouldCancel) {
            if (flow) {
                await cancelFlowSession(flow);
            } else {
                try {
                    await cancelSessionById(sessionId);
                } catch (e) { /* ignore */ }
            }
        }
    }

    async function isWorkerLocked(workerId) {
        try {
            var resp = await fetch('/Captcha/WorkerLock?workerId=' + encodeURIComponent(workerId), { credentials: 'same-origin' });
            if (!resp.ok) return null;
            return resp.json();
        } catch (e) {
            return null;
        }
    }

    window.OrbitaCaptchaSolver = {
        open: open,
        close: close,
        isWorkerLocked: isWorkerLocked,
        canSolveRow: function (row) {
            return row.getAttribute('data-captcha-can-solve') === '1'
                && !!row.getAttribute('data-captcha-url')
                && !!row.getAttribute('data-captcha-account-id')
                && !!row.getAttribute('data-captcha-worker-id');
        },
        payloadFromRow: function (row) {
            var rowSelector = '.errors-row';
            var dismissUrl = '/Errors/Dismiss';
            if (row.classList.contains('events-row')) {
                rowSelector = '.events-row';
                dismissUrl = '/Events/Dismiss';
            } else if (row.classList.contains('dash-event-row--detail')) {
                rowSelector = '.dash-event-row--detail';
                dismissUrl = row.getAttribute('data-is-error') === 'true'
                    ? '/Errors/Dismiss'
                    : '/Events/Dismiss';
            }
            return {
                eventId: row.getAttribute('data-event-id') || '',
                dismissUrl: dismissUrl,
                rowSelector: rowSelector,
                accountId: row.getAttribute('data-captcha-account-id'),
                workerId: row.getAttribute('data-captcha-worker-id'),
                pageUrl: row.getAttribute('data-captcha-url'),
                captchaKind: row.getAttribute('data-captcha-kind') || 'captcha',
                subProfileId: row.getAttribute('data-captcha-subprofile-id') || '',
                accountName: row.getAttribute('data-captcha-account-name') || ''
            };
        }
    };
})();
