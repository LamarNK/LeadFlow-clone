(() => {
    'use strict';
    if (window.orbitaRecordingsBound) return;
    window.orbitaRecordingsBound = true;
    // Capture media events: they do not bubble. Delegation also covers fast navigation.
    document.addEventListener('play', event => {
        if (!event.target.matches?.('.crm-recordings__player audio')) return;
        for (const other of document.querySelectorAll('.crm-recordings__player audio')) {
            if (other !== event.target) other.pause();
        }
    }, true);
    for (const name of ['error', 'loadedmetadata']) {
        document.addEventListener(name, event => {
            if (!event.target.matches?.('.crm-recordings__player audio')) return;
            const error = event.target.parentElement.querySelector('.crm-recordings__error');
            if (error) error.hidden = name !== 'error';
        }, true);
    }
})();
