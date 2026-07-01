(function () {
    var highlightMs = 1800;

    function stableJson(value) {
        return JSON.stringify(value);
    }

    function escapeHtml(text) {
        return String(text)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    function highlightCard(el) {
        if (!el) return;
        el.classList.remove('orbita-live-updated');
        void el.offsetWidth;
        el.classList.add('orbita-live-updated');
        window.setTimeout(function () {
            el.classList.remove('orbita-live-updated');
        }, highlightMs);
    }

    function updateUpdatedClock(isoUtc) {
        if (!isoUtc) return;
        document.querySelectorAll('.orbita-updated-time').forEach(function (el) {
            el.setAttribute('data-orbita-utc', isoUtc);
            if (window.OrbitaTime) {
                window.OrbitaTime.localizeElement(el);
            }
        });
    }

    function setRefreshBusy(busy) {
        document.querySelectorAll('[data-orbita-refresh]').forEach(function (btn) {
            btn.classList.toggle('is-refreshing', busy);
            if (busy) {
                btn.setAttribute('aria-busy', 'true');
            } else {
                btn.removeAttribute('aria-busy');
            }
        });
    }

    function animateKpiValue(el, from, to, suffix, duration, delay) {
        var reduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        suffix = suffix || '';
        if (reduced) {
            el.textContent = Math.round(to) + suffix;
            el.setAttribute('data-kpi-count', String(to));
            el.setAttribute('data-kpi-suffix', suffix);
            return;
        }

        var startAt = 0;
        duration = duration || 720;
        delay = delay || 0;

        function easeOutCubic(t) {
            return 1 - Math.pow(1 - t, 3);
        }

        function frame(now) {
            if (!startAt) startAt = now;
            var elapsed = now - startAt;
            if (elapsed < delay) {
                requestAnimationFrame(frame);
                return;
            }

            var t = Math.min(1, (elapsed - delay) / duration);
            var value = Math.round(from + (to - from) * easeOutCubic(t));
            el.textContent = value + suffix;
            if (t < 1) {
                requestAnimationFrame(frame);
            } else {
                el.setAttribute('data-kpi-count', String(to));
                el.setAttribute('data-kpi-suffix', suffix);
            }
        }

        requestAnimationFrame(frame);
    }

    function updateKpiCards(kpiCards, highlightChanged) {
        (kpiCards || []).forEach(function (card, index) {
            var el = document.querySelector('[data-kpi-key="' + card.key + '"]');
            if (!el) return;

            var valueEl = el.querySelector('[data-kpi-count]');
            if (!valueEl) return;

            var suffix = card.valueSuffix || '';
            var prev = parseFloat(valueEl.getAttribute('data-kpi-count'));
            var next = card.countValue;
            if (isNaN(prev)) prev = 0;

            if (prev !== next || valueEl.getAttribute('data-kpi-suffix') !== suffix) {
                animateKpiValue(valueEl, prev, next, suffix, 720, 80 + index * 70);
            }

            var deltaEl = el.querySelector('.kpi-delta-pill');
            if (deltaEl && card.delta) {
                deltaEl.textContent = card.delta;
                deltaEl.className = 'kpi-delta-pill kpi-delta-' + (card.deltaTone || 'neutral');
            }

            if (highlightChanged && prev !== next) {
                highlightCard(el);
            }
        });
    }

    function getLiveRoot() {
        return document.querySelector('[data-orbita-live]');
    }

    function getLiveAttr(name) {
        var root = getLiveRoot();
        return root ? (root.getAttribute(name) || '') : '';
    }

    function urlFromTemplate(template, token, value) {
        if (!template) return '#';
        return template.split(token).join(encodeURIComponent(String(value)));
    }

    function getRequestVerificationToken() {
        var input = document.querySelector('input[name="__RequestVerificationToken"]');
        return input ? input.value : '';
    }

    function rowMenuShell(dropdownClass, itemsHtml) {
        return '<div class="row-menu" data-row-menu>' +
            '<button type="button" class="row-menu-btn" aria-label="Действия" aria-expanded="false" aria-haspopup="true">' +
            '<i class="fa-solid fa-ellipsis-vertical" aria-hidden="true"></i></button>' +
            '<div class="row-menu-dropdown ' + dropdownClass + '" hidden>' + itemsHtml + '</div></div>';
    }

    function formatPhone(phoneRaw, phoneNormalized) {
        var source = phoneNormalized || phoneRaw || '';
        var digits = String(source).replace(/\D/g, '');
        if (digits.length === 11 && digits.charAt(0) === '7') {
            return '+7 ' + digits.slice(1, 4) + ' ' + digits.slice(4, 7) + '-' + digits.slice(7, 9) + '-' + digits.slice(9, 11);
        }
        if (digits.length === 10) {
            return '+7 ' + digits.slice(0, 3) + ' ' + digits.slice(3, 6) + '-' + digits.slice(6, 8) + '-' + digits.slice(8, 10);
        }
        return String(phoneRaw || '').trim();
    }

    function isPhoneHidden(phoneRaw, phoneNormalized) {
        return !String(phoneRaw || '').trim() && !String(phoneNormalized || '').trim();
    }

    function displayAdId(sourceResponseId, vacancyUrl) {
        if (sourceResponseId && String(sourceResponseId).trim()) {
            return String(sourceResponseId).trim();
        }
        var match = String(vacancyUrl || '').match(/\/(\d{6,})(?:\?|$)/);
        return match ? match[1] : '—';
    }

    function displayAuthor(fullName) {
        return fullName && String(fullName).trim() ? String(fullName).trim() : 'Неизвестный пользователь';
    }

    function shouldShowMachineName(displayName, machineName) {
        if (!machineName || !String(machineName).trim()) return false;
        return String(displayName || '').trim().toLowerCase() !== String(machineName).trim().toLowerCase();
    }

    function formatBalance(value) {
        var n = Number(value);
        if (isNaN(n)) return '0 ₽';
        return Math.round(n).toLocaleString('ru-RU') + ' ₽';
    }

    function updatePaginationInfo(pagination) {
        if (!pagination) return;
        document.querySelectorAll('[data-orbita-live-pagination] .orbita-pagination__info').forEach(function (el) {
            if (!pagination.totalItems) {
                el.textContent = 'Показано 0 из 0';
            } else {
                var start = pagination.rangeStart;
                var end = pagination.rangeEnd;
                el.textContent = 'Показано ' + start + '–' + end + ' из ' + pagination.totalItems;
            }
        });
    }

    function renderSubProfilesList(workerId, accountId, items) {
        if (!items || !items.length) return '';
        return '<ul class="subprofiles-list">' + items.map(function (sub) {
            var classes = 'subprofiles-item';
            if (sub.isCurrent) classes += ' subprofiles-item--current';
            if (sub.hasIssue) classes += ' subprofiles-item--issue';
            if (!sub.isEnabledInPanel) classes += ' subprofiles-item--disabled';
            var currentBadge = sub.isCurrent ? '<span class="subprofiles-badge">текущий</span>' : '';
            var category = sub.category
                ? '<span class="subprofiles-category">' + escapeHtml(sub.category) + '</span>'
                : '';
            var issue = sub.hasIssue && sub.issueSummary
                ? '<span class="subprofiles-issue" title="' + escapeHtml(sub.issueSummary) + '">' + escapeHtml(sub.issueSummary) + '</span>'
                : '';
            var screenshot = sub.diagnosticAttachmentId
                ? '<a class="subprofiles-screenshot-link" href="/Diagnostics/Image/' + escapeHtml(sub.diagnosticAttachmentId) + '" target="_blank" rel="noopener" title="Открыть скриншот страницы при ошибке">скрин</a>'
                : '';
            return '<li class="' + classes + '" data-subprofile-id="' + escapeHtml(sub.id) + '">' +
                '<label class="subprofiles-toggle-sm" title="' + (sub.isEnabledInPanel ? 'Отключить субпрофиль' : 'Включить субпрофиль') + '">' +
                '<input type="checkbox" data-subprofile-toggle data-worker-id="' + escapeHtml(workerId) + '" data-account-id="' + escapeHtml(accountId) + '" data-subprofile-id="' + escapeHtml(sub.id) + '"' + (sub.isEnabledInPanel ? ' checked' : '') + ' />' +
                '<span class="subprofiles-toggle-sm-slider"></span></label>' +
                '<div class="subprofiles-item-body"><span class="subprofiles-name">' + escapeHtml(sub.name) + currentBadge + '</span>' + category + issue + screenshot + '</div>' +
                '<span class="subprofiles-balance">' + escapeHtml(sub.balanceText || '—') + '</span></li>';
        }).join('') + '</ul>';
    }

    function renderSubProfilesToolbar(workerId, account, panelIdPrefix) {
        if (!account.canRefreshSubProfiles && !account.hasSubProfiles) return '';
        var refreshBtn = account.canRefreshSubProfiles
            ? '<button type="button" class="subprofiles-refresh-btn' + (account.isSubProfilesRefreshPending ? ' is-pending' : '') + '" data-refresh-subprofiles data-worker-id="' + escapeHtml(workerId) + '" data-account-id="' + escapeHtml(account.id) + '" title="' + (account.isSubProfilesRefreshPending ? 'Обновление запрошено — ждём воркер' : 'Обновить список субпрофилей') + '" aria-label="Обновить субпрофили"><i class="fa-solid fa-arrows-rotate subprofiles-refresh-icon" aria-hidden="true"></i></button>'
            : '';
        var toggle = account.hasSubProfiles
            ? '<button type="button" class="subprofiles-toggle" data-subprofiles-toggle aria-expanded="false" aria-controls="' + panelIdPrefix + '-' + escapeHtml(account.id) + '"><i class="fa-solid fa-chevron-right subprofiles-toggle-icon" aria-hidden="true"></i><span>' + escapeHtml(account.subProfilesSummary || '') + '</span></button>'
            : (account.canRefreshSubProfiles ? '<span class="subprofiles-empty-hint">Субпрофили не обнаружены</span>' : '');
        var panel = account.hasSubProfiles
            ? '<div class="subprofiles-panel" id="' + panelIdPrefix + '-' + escapeHtml(account.id) + '" hidden>' + renderSubProfilesList(workerId, account.id, account.subProfiles) + '</div>'
            : '';
        return '<div class="subprofiles-toolbar">' + toggle + refreshBtn + '</div>' + panel;
    }

    function reinitLiveContent() {
        if (window.Orbita && typeof window.Orbita.reinitLiveContent === 'function') {
            window.Orbita.reinitLiveContent();
        }
    }

    window.OrbitaLiveShared = {
        stableJson: stableJson,
        escapeHtml: escapeHtml,
        highlightCard: highlightCard,
        updateUpdatedClock: updateUpdatedClock,
        setRefreshBusy: setRefreshBusy,
        updateKpiCards: updateKpiCards,
        getLiveRoot: getLiveRoot,
        getLiveAttr: getLiveAttr,
        urlFromTemplate: urlFromTemplate,
        getRequestVerificationToken: getRequestVerificationToken,
        rowMenuShell: rowMenuShell,
        formatPhone: formatPhone,
        isPhoneHidden: isPhoneHidden,
        displayAdId: displayAdId,
        displayAuthor: displayAuthor,
        shouldShowMachineName: shouldShowMachineName,
        formatBalance: formatBalance,
        updatePaginationInfo: updatePaginationInfo,
        renderSubProfilesList: renderSubProfilesList,
        renderSubProfilesToolbar: renderSubProfilesToolbar,
        reinitLiveContent: reinitLiveContent
    };
})();