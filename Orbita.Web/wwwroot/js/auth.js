(function () {
    var background = document.querySelector('.auth-bg');
    if (background) {
        var sky = document.createElement('div');
        sky.className = 'auth-sky';
        sky.setAttribute('aria-hidden', 'true');
        // Stable scattered positions; only opacity and transform animate in CSS.
        for (var starIndex = 0; starIndex < 42; starIndex++) {
            var star = document.createElement('span');
            star.className = 'auth-sky-star';
            star.style.left = ((starIndex * 37 + 11) % 100) + '%';
            star.style.top = ((starIndex * 53 + 7) % 100) + '%';
            star.style.setProperty('--star-size', (starIndex % 5 === 0 ? 3 : 2) + 'px');
            star.style.animationDelay = (-starIndex * 0.73) + 's';
            star.style.animationDuration = (6 + starIndex % 7) + 's';
            sky.appendChild(star);
        }
        for (var meteorIndex = 0; meteorIndex < 2; meteorIndex++) {
            var meteor = document.createElement('span');
            meteor.className = 'auth-sky-meteor';
            meteor.style.left = (18 + meteorIndex * 24) + '%';
            meteor.style.top = (12 + meteorIndex * 18) + '%';
            meteor.style.animationDelay = (4 + meteorIndex * 11) + 's';
            sky.appendChild(meteor);
        }
        background.appendChild(sky);

        var motionPreference = window.matchMedia('(prefers-reduced-motion: reduce)');
        var pausedByUser = false;
        var motionButton = document.createElement('button');
        motionButton.type = 'button';
        motionButton.className = 'auth-motion-toggle';
        var syncSkyMotion = function () {
            var paused = pausedByUser || motionPreference.matches;
            sky.classList.toggle('is-paused', paused || document.hidden);
            motionButton.hidden = motionPreference.matches;
            motionButton.textContent = paused ? 'Включить звёзды' : 'Пауза звёзд';
            motionButton.setAttribute('aria-label', paused ? 'Включить анимацию звёзд' : 'Приостановить анимацию звёзд');
        };
        motionButton.addEventListener('click', function () {
            pausedByUser = !pausedByUser;
            syncSkyMotion();
        });
        motionPreference.addEventListener('change', syncSkyMotion);
        document.addEventListener('visibilitychange', syncSkyMotion);
        document.body.appendChild(motionButton);
        syncSkyMotion();
    }

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
