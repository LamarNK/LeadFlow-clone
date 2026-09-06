(function () {
    var card = document.querySelector('.auth-card');
    var clip = document.querySelector('#auth-main-sculpted-clip path');
    var glow = document.querySelector('.auth-sculpted-edge__glow');

    if (card && clip && glow) {
        // Match background-size: auto 100% and background-position: right center.
        // Both paths use the original image coordinates, even when the card changes aspect ratio.
        var alignPlanetEdge = function () {
            var width = card.clientWidth;
            var height = card.clientHeight;
            if (!width || !height) return;

            var scaleX = height / (1024 * width);
            var offsetX = 1 - 1536 * scaleX;
            clip.setAttribute('transform', 'matrix(' + scaleX + ' 0 0 ' + (1 / 1024) + ' ' + offsetX + ' 0)');
            glow.setAttribute('transform', 'matrix(' + (scaleX * 1000) + ' 0 0 ' + (1000 / 1024) + ' ' + (offsetX * 1000) + ' 0)');
        };

        alignPlanetEdge();
        new ResizeObserver(alignPlanetEdge).observe(card);
    }

    document.querySelectorAll('[data-toggle-password]').forEach(function (button) {
        button.addEventListener('click', function () {
            var input = document.getElementById(button.dataset.togglePassword || '');
            if (!input) return;

            var revealPassword = input.type === 'password';
            input.type = revealPassword ? 'text' : 'password';
            button.setAttribute('aria-label', revealPassword ? 'Скрыть пароль' : 'Показать пароль');

            var icon = button.querySelector('i');
            if (icon) {
                icon.className = revealPassword ? 'fa-regular fa-eye-slash' : 'fa-regular fa-eye';
            }
        });
    });
})();
