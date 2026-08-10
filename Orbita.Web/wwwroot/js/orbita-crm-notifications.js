(function () {
    var seenRealtimeIds = new Set();
    var loadStates = new WeakMap();

    function roots() {
        return Array.prototype.slice.call(document.querySelectorAll('[data-crm-notifications]'));
    }

    function csrf(root) {
        var input = root.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    }

    function formatWhen(value) {
        var date = new Date(value);
        if (Number.isNaN(date.getTime())) return '';
        return new Intl.DateTimeFormat('ru-RU', {
            day: '2-digit',
            month: 'short',
            hour: '2-digit',
            minute: '2-digit'
        }).format(date);
    }

    function kindLabel(kind) {
        if (kind === 'overdue') return 'Просрочено';
        if (kind === 'due_1h') return 'Срок через час';
        if (kind === 'phone_changed') return 'Смена телефона';
        return 'Срок через 24 часа';
    }

    function setBadge(root, unreadCount) {
        var badge = root.querySelector('[data-crm-notifications-badge]');
        var caption = root.querySelector('[data-crm-notifications-caption]');
        var readAll = root.querySelector('[data-crm-notifications-read-all]');
        var count = parseInt(unreadCount, 10) || 0;
        if (badge) {
            badge.textContent = count > 999 ? '999+' : String(count);
            badge.toggleAttribute('hidden', count <= 0);
        }
        if (caption) {
            caption.textContent = count > 0 ? count + ' непрочитанных' : 'CRM-уведомления';
        }
        if (readAll) readAll.toggleAttribute('hidden', count <= 0);
    }

    function invalidateLoad(root) {
        var previous = loadStates.get(root);
        if (previous && previous.controller) previous.controller.abort();
        loadStates.set(root, {
            sequence: previous ? previous.sequence + 1 : 1,
            controller: null
        });
    }

    function setEnabled(root, enabled, invalidatePending) {
        root.toggleAttribute('hidden', !enabled);
        if (enabled) return;
        if (invalidatePending === true) invalidateLoad(root);
        setBadge(root, 0);
        var dropdown = root.querySelector('[data-crm-notifications-dropdown]');
        var toggle = root.querySelector('[data-crm-notifications-toggle]');
        if (dropdown) dropdown.hidden = true;
        if (toggle) toggle.setAttribute('aria-expanded', 'false');
    }

    function render(root, payload) {
        var list = root.querySelector('[data-crm-notifications-list]');
        if (!list) return;
        var enabled = payload && payload.enabled === true;
        setEnabled(root, enabled, false);
        if (!enabled) {
            list.textContent = '';
            return;
        }

        setBadge(root, payload.unreadCount);
        list.textContent = '';

        var items = Array.isArray(payload.items) ? payload.items : [];
        if (items.length === 0) {
            var empty = document.createElement('p');
            empty.className = 'orbita-notifications__empty';
            empty.textContent = 'Новых уведомлений нет.';
            list.appendChild(empty);
            return;
        }

        items.forEach(function (item) {
            var button = document.createElement('button');
            button.type = 'button';
            button.className = 'orbita-notification-item' + (item.readAtUtc ? ' is-read' : ' is-unread');
            button.setAttribute('data-notification-id', item.id);
            button.setAttribute('data-task-id', item.taskId || '');
            if (item.cardId) button.setAttribute('data-card-id', item.cardId);
            button.setAttribute('data-is-read', item.readAtUtc ? '1' : '0');

            var icon = document.createElement('span');
            icon.className = 'orbita-notification-item__icon ' + (item.kind === 'overdue' ? 'is-overdue' : item.kind === 'phone_changed' ? 'is-phone' : 'is-upcoming');
            var iconGlyph = document.createElement('i');
            iconGlyph.className = item.kind === 'overdue'
                ? 'fa-solid fa-triangle-exclamation'
                : item.kind === 'phone_changed'
                    ? 'fa-solid fa-phone'
                    : 'fa-regular fa-clock';
            icon.appendChild(iconGlyph);

            var body = document.createElement('span');
            body.className = 'orbita-notification-item__body';
            var meta = document.createElement('span');
            meta.className = 'orbita-notification-item__meta';
            meta.textContent = kindLabel(item.kind) + ' · ' + formatWhen(item.createdAtUtc);
            var title = document.createElement('strong');
            title.textContent = item.taskTitle || 'CRM-задача';
            var message = document.createElement('span');
            message.className = 'orbita-notification-item__message';
            message.textContent = item.message || '';
            body.appendChild(meta);
            body.appendChild(title);
            body.appendChild(message);

            button.appendChild(icon);
            button.appendChild(body);
            button.addEventListener('click', function () {
                openTask(root, item);
            });
            list.appendChild(button);
        });
    }

    function load(root) {
        var previous = loadStates.get(root);
        if (previous && previous.controller) previous.controller.abort();
        var state = {
            sequence: previous ? previous.sequence + 1 : 1,
            controller: typeof AbortController === 'function' ? new AbortController() : null
        };
        loadStates.set(root, state);

        return fetch('/Crm/Notifications?limit=10', {
            credentials: 'same-origin',
            signal: state.controller ? state.controller.signal : undefined
        })
            .then(function (response) {
                if (!response.ok) throw new Error('CRM notifications: ' + response.status);
                return response.json();
            })
            .then(function (payload) {
                if (loadStates.get(root) !== state) return;
                render(root, payload);
            })
            .catch(function (error) {
                if (error && error.name === 'AbortError') return;
                if (loadStates.get(root) !== state) return;
                var list = root.querySelector('[data-crm-notifications-list]');
                if (list) list.innerHTML = '<p class="orbita-notifications__empty">Не удалось загрузить уведомления.</p>';
            });
    }

    function post(root, url) {
        return fetch(url, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { 'RequestVerificationToken': csrf(root) }
        });
    }

    function refreshBadges() {
        if (window.Orbita && typeof window.Orbita.fetchNavBadges === 'function') {
            window.Orbita.fetchNavBadges();
        }
    }

    function openTask(root, item) {
        var navigate = function () {
            if (item.cardId && (!item.taskId || item.taskId === '00000000-0000-0000-0000-000000000000')) {
                window.location.href = '/Crm/Card/' + encodeURIComponent(item.cardId);
                return;
            }
            window.location.href = '/Crm/TaskDetails/' + encodeURIComponent(item.taskId);
        };
        if (item.readAtUtc) {
            navigate();
            return;
        }

        post(root, '/Crm/Notifications/' + encodeURIComponent(item.id) + '/read')
            .finally(function () {
                refreshBadges();
                navigate();
            });
    }

    function closeAll(except) {
        roots().forEach(function (root) {
            if (root === except) return;
            var dropdown = root.querySelector('[data-crm-notifications-dropdown]');
            var toggle = root.querySelector('[data-crm-notifications-toggle]');
            if (dropdown) dropdown.hidden = true;
            if (toggle) toggle.setAttribute('aria-expanded', 'false');
        });
    }

    function bind(root) {
        if (root.hasAttribute('data-crm-notifications-bound')) return;
        root.setAttribute('data-crm-notifications-bound', '1');
        var toggle = root.querySelector('[data-crm-notifications-toggle]');
        var dropdown = root.querySelector('[data-crm-notifications-dropdown]');
        var readAll = root.querySelector('[data-crm-notifications-read-all]');

        if (toggle && dropdown) {
            toggle.addEventListener('click', function (event) {
                event.stopPropagation();
                var opening = dropdown.hidden;
                closeAll(opening ? root : null);
                dropdown.hidden = !opening;
                toggle.setAttribute('aria-expanded', opening ? 'true' : 'false');
                if (opening) load(root);
            });
            dropdown.addEventListener('click', function (event) { event.stopPropagation(); });
        }

        if (readAll) {
            readAll.addEventListener('click', function () {
                readAll.disabled = true;
                post(root, '/Crm/Notifications/read-all')
                    .then(function (response) {
                        if (!response.ok) throw new Error('CRM notifications read-all: ' + response.status);
                        return load(root);
                    })
                    .then(refreshBadges)
                    .catch(function () {
                        if (window.Orbita && typeof window.Orbita.toast === 'function') {
                            window.Orbita.toast('Не удалось отметить уведомления прочитанными.', {
                                variant: 'error'
                            });
                        }
                    })
                    .finally(function () { readAll.disabled = false; });
            });
        }

        load(root);
    }

    function init() {
        roots().forEach(bind);
    }

    function setUnreadCount(unreadCount) {
        roots().forEach(function (root) { setBadge(root, unreadCount); });
    }

    function setNotificationsEnabled(enabled) {
        roots().forEach(function (root) { setEnabled(root, enabled === true, enabled !== true); });
    }

    function handleRealtime(notification) {
        if (!notification) return;
        var id = notification.id || notification.Id;
        if (id && seenRealtimeIds.has(String(id))) return;
        if (id) {
            seenRealtimeIds.add(String(id));
            if (seenRealtimeIds.size > 500) {
                seenRealtimeIds.delete(seenRealtimeIds.values().next().value);
            }
        }

        init();
        roots().forEach(load);
        refreshBadges();

        var kind = notification.kind || notification.Kind;
        var message = notification.message || notification.Message || 'Новый срок CRM-задачи.';
        var taskTitle = notification.taskTitle || notification.TaskTitle;
        if (taskTitle) message += ' «' + taskTitle + '»';
        if (window.Orbita && typeof window.Orbita.toast === 'function') {
            window.Orbita.toast(message, {
                variant: kind === 'overdue' ? 'error' : 'info',
                duration: 9000
            });
        }
    }

    document.addEventListener('click', function () { closeAll(null); });
    document.addEventListener('orbita:content-updated', init);
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    window.OrbitaNotifications = {
        refresh: function () { init(); roots().forEach(load); },
        handleRealtime: handleRealtime,
        setUnreadCount: setUnreadCount,
        setEnabled: setNotificationsEnabled
    };
})();
