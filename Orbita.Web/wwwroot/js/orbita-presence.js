(function () {
    var token = document.querySelector('meta[name="orbita-antiforgery-token"]')?.getAttribute('content');
    if (!token) {
        return;
    }

    var intervalMs = 60 * 1000;
    var timerId;
    var inFlight = false;

    function sendHeartbeat() {
        if (inFlight || document.visibilityState === 'hidden') {
            return;
        }

        inFlight = true;
        fetch('/Account/Heartbeat', {
            method: 'POST',
            credentials: 'same-origin',
            headers: {
                'RequestVerificationToken': token,
                'X-Requested-With': 'XMLHttpRequest'
            }
        }).catch(function () {
            // Presence is advisory; the next interval will retry without disrupting the panel.
        }).finally(function () {
            inFlight = false;
        });
    }

    function beginHeartbeat() {
        if (timerId) {
            return;
        }

        sendHeartbeat();
        timerId = window.setInterval(sendHeartbeat, intervalMs);
    }

    beginHeartbeat();
    document.addEventListener('visibilitychange', function () {
        if (document.visibilityState === 'visible') {
            sendHeartbeat();
        }
    });
})();
