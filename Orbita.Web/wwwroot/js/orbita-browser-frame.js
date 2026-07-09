(function () {
    function loadImageFrame(frame, source, onReady, onError) {
        return new Promise(function (resolve) {
            var settled = false;

            function finish(success) {
                if (settled) {
                    return;
                }

                settled = true;
                frame.onload = null;
                frame.onerror = null;

                if (success) {
                    if (typeof onReady === 'function') {
                        onReady();
                    }
                } else if (typeof onError === 'function') {
                    onError();
                }

                resolve();
            }

            frame.onload = function () {
                finish(true);
            };

            frame.onerror = function () {
                finish(false);
            };

            frame.src = source;
            window.requestAnimationFrame(function () {
                if (frame.complete && frame.naturalWidth > 0) {
                    finish(true);
                }
            });
        });
    }

    function renderLiveFrame(frame, imageBase64, contentType, callbacks) {
        if (!frame || !imageBase64) {
            return Promise.resolve();
        }

        return loadImageFrame(
            frame,
            'data:' + (contentType || 'image/jpeg') + ';base64,' + imageBase64,
            callbacks && callbacks.onReady,
            callbacks && callbacks.onError);
    }

    window.OrbitaBrowserFrame = {
        loadImageFrame: loadImageFrame,
        renderLiveFrame: renderLiveFrame
    };
})();