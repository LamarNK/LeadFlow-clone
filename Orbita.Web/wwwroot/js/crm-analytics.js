(() => {
    'use strict';
    const root = document.querySelector('.crm-analytics-sales');
    if (!root) return;
    const states = new WeakMap();

    async function load(host, url, title, trigger) {
        const previous = states.get(host);
        previous?.controller.abort();
        previous?.trigger?.setAttribute('aria-expanded', 'false');
        const controller = new AbortController();
        states.set(host, { controller, trigger, title });
        host.hidden = false;
        host.setAttribute('aria-busy', 'true');
        host.replaceChildren();
        const heading = document.createElement('div');
        heading.className = 'sales-evidence-heading';
        const caption = document.createElement('h3');
        caption.textContent = title;
        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'sales-evidence-close';
        close.textContent = 'Свернуть';
        close.addEventListener('click', () => {
            controller.abort();
            host.hidden = true;
            host.removeAttribute('aria-busy');
            trigger?.setAttribute('aria-expanded', 'false');
            trigger?.focus({ preventScroll: true });
        });
        heading.append(caption, close);
        const content = document.createElement('div');
        content.setAttribute('aria-live', 'polite');
        content.textContent = 'Загружаем карточки и события…';
        host.append(heading, content);
        trigger?.setAttribute('aria-expanded', 'true');
        try {
            const target = new URL(url, location.href);
            if (target.origin !== location.origin) throw new Error('Unexpected origin');
            const response = await fetch(target, { signal: controller.signal, credentials: 'same-origin', headers: { 'X-Requested-With': 'XMLHttpRequest' } });
            if (!response.ok || response.redirected || !response.headers.get('content-type')?.includes('text/html')) throw new Error('Evidence unavailable');
            const html = await response.text();
            if (controller.signal.aborted) return;
            // This is a Razor-escaped partial from our same-origin endpoint.
            const fragment = document.createElement('template');
            fragment.innerHTML = html;
            fragment.content.querySelectorAll('script').forEach(node => node.remove());
            content.replaceChildren(fragment.content);
        } catch {
            if (controller.signal.aborted) return;
            content.textContent = 'Не удалось загрузить детали. Это не нулевой результат. Нажмите на показатель ещё раз.';
            content.className = 'sales-alert';
            content.setAttribute('role', 'alert');
        } finally {
            if (states.get(host)?.controller === controller) host.removeAttribute('aria-busy');
        }
    }

    root.addEventListener('click', event => {
        const page = event.target.closest('button[data-evidence-page]');
        if (page && !page.disabled) {
            const host = page.closest('[data-evidence-host]');
            const state = states.get(host);
            if (state) load(host, page.dataset.evidencePage, state.title, state.trigger);
            return;
        }
        const button = event.target.closest('button[data-evidence-url]');
        if (!button) return;
        const host = button.closest('[data-report-block]')?.querySelector('[data-evidence-host]');
        if (host) load(host, button.dataset.evidenceUrl, button.dataset.evidenceTitle || 'Карточки показателя', button);
    });
})();
