(function () {
    var canvas = document.getElementById('authDots');
    if (!canvas) return;

    var ctx = canvas.getContext('2d');
    if (!ctx) return;

    var dots = [];
    var comets = [];
    var meteors = [];
    var lastTime = 0;
    var nextCometAt = 0;
    var nextMeteorAt = 0;

    function rand(min, max) {
        return min + Math.random() * (max - min);
    }

    function skyH() {
        return canvas.height * 0.68;
    }

    function resize() {
        canvas.width = window.innerWidth;
        canvas.height = window.innerHeight;

        dots = [];
        comets = [];
        meteors = [];

        var count = Math.min(90, Math.max(55, Math.floor((canvas.width * canvas.height) / 22000)));

        for (var i = 0; i < count; i++) {
            var roll = Math.random();
            var freq;

            if (roll < 0.25) {
                freq = rand(0.00035, 0.0009);
            } else if (roll < 0.6) {
                freq = rand(0.001, 0.0028);
            } else if (roll < 0.85) {
                freq = rand(0.003, 0.006);
            } else {
                freq = rand(0.006, 0.012);
            }

            dots.push({
                x: Math.random() * canvas.width,
                y: Math.random() * skyH(),
                r: Math.random() < 0.12 ? 1.5 : 1,
                phase: Math.random() * Math.PI * 2,
                phase2: Math.random() * Math.PI * 2,
                freq: freq,
                freq2: freq * rand(1.4, 2.8),
                mix: rand(0.55, 0.9),
                minA: rand(0.08, 0.3),
                maxA: rand(0.65, 1),
                twinkle: Math.random() < 0.55,
                base: rand(0.3, 0.55)
            });
        }
    }

    function scheduleComet(time) {
        nextCometAt = time + rand(9000, 20000);
    }

    function scheduleMeteor(time) {
        nextMeteorAt = time + rand(4000, 11000);
    }

    function spawnComet() {
        var fromLeft = Math.random() < 0.7;
        var angle = rand(0.12, 0.32);
        var speed = rand(0.18, 0.42);

        comets.push({
            x: fromLeft ? rand(-120, canvas.width * 0.15) : rand(canvas.width * 0.55, canvas.width + 80),
            y: fromLeft ? rand(0, skyH() * 0.55) : rand(0, skyH() * 0.35),
            vx: fromLeft ? Math.cos(angle) * speed : -Math.cos(angle) * speed,
            vy: Math.sin(angle) * speed,
            tail: rand(90, 170),
            head: rand(2, 3.2),
            fade: rand(0.45, 0.75)
        });
    }

    function spawnMeteor() {
        var angle = rand(0.55, 0.85);
        var speed = rand(7, 12);

        meteors.push({
            x: rand(-80, canvas.width * 0.75),
            y: rand(0, skyH() * 0.45),
            vx: Math.cos(angle) * speed,
            vy: Math.sin(angle) * speed,
            len: rand(55, 110),
            life: 0,
            maxLife: rand(500, 850)
        });
    }

    function twinkleAlpha(d, time) {
        var t1 = time * d.freq + d.phase;
        var t2 = time * d.freq2 + d.phase2;
        var wave = d.mix * (0.5 + 0.5 * Math.sin(t1)) + (1 - d.mix) * (0.5 + 0.5 * Math.sin(t2));
        return d.minA + wave * (d.maxA - d.minA);
    }

    function drawComet(c) {
        var len = Math.hypot(c.vx, c.vy) || 1;
        var nx = c.vx / len;
        var ny = c.vy / len;
        var tx = c.x - nx * c.tail;
        var ty = c.y - ny * c.tail;

        var grad = ctx.createLinearGradient(tx, ty, c.x, c.y);
        grad.addColorStop(0, 'rgba(100, 160, 255, 0)');
        grad.addColorStop(0.55, 'rgba(150, 200, 255, ' + (c.fade * 0.35).toFixed(2) + ')');
        grad.addColorStop(1, 'rgba(230, 245, 255, ' + c.fade.toFixed(2) + ')');

        ctx.strokeStyle = grad;
        ctx.lineWidth = 1.6;
        ctx.lineCap = 'round';
        ctx.beginPath();
        ctx.moveTo(tx, ty);
        ctx.lineTo(c.x, c.y);
        ctx.stroke();

        ctx.beginPath();
        ctx.arc(c.x, c.y, c.head, 0, Math.PI * 2);
        ctx.fillStyle = 'rgba(210, 235, 255, ' + c.fade.toFixed(2) + ')';
        ctx.fill();
    }

    function drawMeteor(m, progress) {
        var fade = 1 - progress;
        var len = Math.hypot(m.vx, m.vy) || 1;
        var nx = m.vx / len;
        var ny = m.vy / len;
        var tx = m.x - nx * m.len;
        var ty = m.y - ny * m.len;

        var grad = ctx.createLinearGradient(tx, ty, m.x, m.y);
        grad.addColorStop(0, 'rgba(255, 255, 255, 0)');
        grad.addColorStop(0.6, 'rgba(200, 225, 255, ' + (fade * 0.5).toFixed(2) + ')');
        grad.addColorStop(1, 'rgba(255, 255, 255, ' + fade.toFixed(2) + ')');

        ctx.strokeStyle = grad;
        ctx.lineWidth = 1.2;
        ctx.lineCap = 'round';
        ctx.beginPath();
        ctx.moveTo(tx, ty);
        ctx.lineTo(m.x, m.y);
        ctx.stroke();
    }

    function drawNebula(time) {
        var x = canvas.width * 0.18 + Math.sin(time * 0.00015) * 30;
        var y = canvas.height * 0.14 + Math.cos(time * 0.00012) * 20;
        var grad = ctx.createRadialGradient(x, y, 0, x, y, canvas.width * 0.22);
        grad.addColorStop(0, 'rgba(90, 140, 220, 0.07)');
        grad.addColorStop(0.45, 'rgba(60, 100, 180, 0.035)');
        grad.addColorStop(1, 'rgba(30, 50, 90, 0)');

        ctx.fillStyle = grad;
        ctx.fillRect(0, 0, canvas.width, skyH());
    }

    function updateComets(dt) {
        for (var i = comets.length - 1; i >= 0; i--) {
            var c = comets[i];
            c.x += c.vx * dt;
            c.y += c.vy * dt;

            if (c.y > skyH() + 40 || c.x < -200 || c.x > canvas.width + 200) {
                comets.splice(i, 1);
            }
        }
    }

    function updateMeteors(dt) {
        for (var i = meteors.length - 1; i >= 0; i--) {
            var m = meteors[i];
            m.life += dt;
            m.x += m.vx * dt * 0.06;
            m.y += m.vy * dt * 0.06;

            if (m.life > m.maxLife || m.y > skyH()) {
                meteors.splice(i, 1);
            }
        }
    }

    function draw(time) {
        var dt = lastTime ? Math.min(time - lastTime, 50) : 16;
        lastTime = time;

        if (!nextCometAt) {
            scheduleComet(time);
            scheduleMeteor(time);
        }

        if (time >= nextCometAt && comets.length < 2) {
            spawnComet();
            scheduleComet(time);
        }

        if (time >= nextMeteorAt && meteors.length < 1) {
            spawnMeteor();
            scheduleMeteor(time);
        }

        updateComets(dt);
        updateMeteors(dt);

        ctx.clearRect(0, 0, canvas.width, canvas.height);

        drawNebula(time);

        for (var i = 0; i < dots.length; i++) {
            var d = dots[i];
            var alpha = d.twinkle ? twinkleAlpha(d, time) : d.base;

            ctx.fillStyle = 'rgba(255, 255, 255, ' + alpha.toFixed(2) + ')';
            ctx.beginPath();
            ctx.arc(d.x, d.y, d.r, 0, Math.PI * 2);
            ctx.fill();
        }

        for (var j = 0; j < comets.length; j++) {
            drawComet(comets[j]);
        }

        for (var k = 0; k < meteors.length; k++) {
            var m = meteors[k];
            drawMeteor(m, m.life / m.maxLife);
        }

        requestAnimationFrame(draw);
    }

    resize();
    window.addEventListener('resize', resize);
    requestAnimationFrame(draw);
})();