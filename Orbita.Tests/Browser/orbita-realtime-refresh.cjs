const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const callbacks = [];
const hubHandlers = {};
let resolveFirstRefresh;
let refreshCount = 0;

const root = {
    getAttribute(name) {
        return name === 'data-orbita-live-page' ? 'responses' : '';
    }
};

const document = {
    body: { getAttribute: () => '' },
    querySelector(selector) {
        return selector === '[data-orbita-live]' ? root : null;
    },
    querySelectorAll: () => [],
    addEventListener: () => {}
};

const connection = {
    state: 'Connected',
    start: () => Promise.resolve(),
    stop: () => Promise.resolve(),
    on(name, handler) { hubHandlers[name] = handler; },
    onreconnecting: () => {},
    onreconnected: () => {},
    onclose: () => {}
};

class HubConnectionBuilder {
    withUrl() { return this; }
    configureLogging() { return this; }
    withAutomaticReconnect() { return this; }
    build() { return connection; }
}

const window = {
    __orbitaRealtimeBootstrapped: false,
    OrbitaLiveShared: { setRefreshBusy: () => {} },
    setTimeout(callback) {
        callbacks.push(callback);
        return callbacks.length;
    },
    clearTimeout: () => {},
    setInterval: () => 1,
    clearInterval: () => {},
    addEventListener: () => {}
};

const context = {
    window,
    document,
    console,
    URL,
    Promise,
    signalR: {
        HubConnectionState: { Connected: 'Connected', Connecting: 'Connecting', Reconnecting: 'Reconnecting' },
        HttpTransportType: { WebSockets: 1, ServerSentEvents: 2, LongPolling: 4 },
        LogLevel: { Warning: 3 },
        HubConnectionBuilder
    },
    fetch(url) {
        if (url === '/Realtime/AccessToken') {
            return Promise.resolve({ ok: true, json: () => Promise.resolve({ accessToken: 'token', hubUrl: '/hubs/panel' }) });
        }
        return Promise.resolve({ ok: true, json: () => Promise.resolve({}) });
    }
};

function runNextTimer() {
    const callback = callbacks.shift();
    assert.ok(callback, 'expected a queued timer callback');
    callback();
}

async function drainMicrotasks() {
    await Promise.resolve();
    await Promise.resolve();
    await Promise.resolve();
}

async function main() {
    const scriptPath = path.resolve(__dirname, '../../Orbita.Web/wwwroot/js/orbita-realtime.js');
    vm.runInNewContext(fs.readFileSync(scriptPath, 'utf8'), context, { filename: scriptPath });
    await drainMicrotasks();

    window.OrbitaLive.register('responses', {
        fetchSnapshot() {
            refreshCount++;
            if (refreshCount === 1) {
                return new Promise(resolve => { resolveFirstRefresh = resolve; });
            }
            return Promise.resolve();
        }
    });

    window.OrbitaLive.scheduleRefresh({ kinds: ['Responses'] });
    runNextTimer();
    assert.equal(refreshCount, 1, 'first notification should start a snapshot refresh');

    window.OrbitaLive.scheduleRefresh({ kinds: ['Responses'] });
    runNextTimer();
    assert.equal(refreshCount, 1, 'second notification arrives while the first refresh is in flight');

    resolveFirstRefresh();
    await drainMicrotasks();
    runNextTimer();
    await drainMicrotasks();

    assert.equal(refreshCount, 2, 'notification received during an in-flight refresh must be replayed');

    const sharedScriptPath = path.resolve(__dirname, '../../Orbita.Web/wwwroot/js/orbita-live-shared.js');
    const sharedScript = fs.readFileSync(sharedScriptPath, 'utf8');
    assert.match(sharedScript, /cache:\s*['"]no-store['"]/, 'live snapshots must bypass browser and proxy caches');
}

main().catch(error => {
    console.error(error);
    process.exitCode = 1;
});
