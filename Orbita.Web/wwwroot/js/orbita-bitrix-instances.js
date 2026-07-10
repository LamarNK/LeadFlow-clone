(function () {
    function init() {
        if (window.Orbita && typeof window.Orbita.initBitrixValidateButtons === 'function') {
            window.Orbita.initBitrixValidateButtons();
        }
    }

    init();
    document.addEventListener('orbita:content-updated', init);
})();