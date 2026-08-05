(function (runtime) {
    function responseInitials(name) {
        return String(name || '').trim().split(/\s+/).filter(Boolean).slice(0, 2)
            .map(function (part) { return part.charAt(0).toLocaleUpperCase(); }).join('') || '—';
    }

    function responseTone(tone) {
        return ['unique', 'duplicate', 'sent', 'action_required', 'error'].indexOf(tone) >= 0
            ? tone
            : 'unique';
    }

    function responseIcon(className) {
        var icon = document.createElement('i');
        icon.className = className;
        icon.setAttribute('aria-hidden', 'true');
        return icon;
    }

    function appendResponseFact(list, section) {
        var row = document.createElement('div');
        row.className = 'orbita-response-detail__fact';

        var label = document.createElement('dt');
        label.textContent = section.label || '';
        row.appendChild(label);

        var value = document.createElement('dd');
        if (section.values && section.values.length) {
            var values = document.createElement('ul');
            values.className = 'orbita-response-detail__fact-values';
            section.values.forEach(function (item) {
                var valueItem = document.createElement('li');
                valueItem.textContent = item || '—';
                values.appendChild(valueItem);
            });
            value.appendChild(values);
        } else if (section.href) {
            var link = document.createElement('a');
            link.href = section.href;
            link.target = '_blank';
            link.rel = 'noopener';
            link.textContent = section.value || 'Открыть';
            value.appendChild(link);
        } else {
            value.textContent = section.value || '—';
        }
        row.appendChild(value);
        list.appendChild(row);
    }

    function createResponseChatBubble(message) {
        var tone = message.tone === 'outgoing' || message.tone === 'incoming'
            ? message.tone
            : 'system';
        var bubble = document.createElement('article');
        bubble.className = 'orbita-response-detail__chat-bubble orbita-response-detail__chat-bubble--' + tone;
        var text = document.createElement('p');
        text.textContent = message.text || '—';
        bubble.appendChild(text);
        if (message.timeLabel) {
            var time = document.createElement('time');
            time.textContent = message.timeLabel;
            bubble.appendChild(time);
        }
        return bubble;
    }

    function createResponseDetail(options) {
        var profile = options.responseProfile || {};
        var sections = options.sections || [];
        var messages = options.chatMessages || [];
        var detail = document.createElement('div');
        detail.className = 'orbita-response-detail';

        var hero = document.createElement('header');
        hero.className = 'orbita-response-detail__hero';
        var identity = document.createElement('div');
        identity.className = 'orbita-response-detail__identity';
        var avatar = document.createElement('span');
        avatar.className = 'orbita-response-detail__avatar';
        avatar.textContent = responseInitials(profile.candidateName || options.title);
        if (profile.avatarUrl) {
            var avatarImage = document.createElement('img');
            avatarImage.src = profile.avatarUrl;
            avatarImage.alt = '';
            avatarImage.addEventListener('error', function () { avatarImage.remove(); });
            avatar.appendChild(avatarImage);
        }
        identity.appendChild(avatar);

        var person = document.createElement('div');
        var nameLine = document.createElement('div');
        nameLine.className = 'orbita-response-detail__name-line';
        var name = document.createElement('h2');
        name.textContent = profile.candidateName || options.title || 'Кандидат';
        nameLine.appendChild(name);
        if (profile.statusLabel) {
            var status = document.createElement('span');
            status.className = 'orbita-response-detail__status orbita-response-detail__status--' + responseTone(profile.statusTone);
            status.textContent = profile.statusLabel;
            nameLine.appendChild(status);
        }
        person.appendChild(nameLine);
        if (profile.candidateMeta) {
            var meta = document.createElement('p');
            meta.className = 'orbita-response-detail__meta';
            meta.textContent = profile.candidateMeta;
            person.appendChild(meta);
        }
        identity.appendChild(person);
        hero.appendChild(identity);

        var quickActions = document.createElement('div');
        quickActions.className = 'orbita-response-detail__quick-actions';
        if (profile.phoneHref) {
            var phoneAction = document.createElement('a');
            phoneAction.className = 'orbita-response-detail__quick-action';
            phoneAction.href = 'tel:' + profile.phoneHref;
            phoneAction.appendChild(responseIcon('fa-solid fa-phone'));
            var phoneLabel = document.createElement('span');
            phoneLabel.textContent = 'Позвонить';
            phoneAction.appendChild(phoneLabel);
            quickActions.appendChild(phoneAction);
        }
        if (profile.phone && profile.phone !== 'Скрыт') {
            var copyPhone = document.createElement('button');
            copyPhone.type = 'button';
            copyPhone.className = 'orbita-response-detail__quick-action orbita-response-detail__quick-action--icon';
            copyPhone.title = 'Скопировать телефон';
            copyPhone.setAttribute('aria-label', 'Скопировать телефон');
            copyPhone.appendChild(responseIcon('fa-regular fa-copy'));
            copyPhone.addEventListener('click', function () { runtime.copyText(profile.phone); });
            quickActions.appendChild(copyPhone);
        }
        if (profile.messengerUrl) {
            var messenger = document.createElement('a');
            messenger.className = 'orbita-response-detail__quick-action orbita-response-detail__quick-action--icon';
            messenger.href = profile.messengerUrl;
            messenger.target = '_blank';
            messenger.rel = 'noopener';
            messenger.title = 'Открыть чат';
            messenger.setAttribute('aria-label', 'Открыть чат');
            messenger.appendChild(responseIcon('fa-solid fa-paper-plane'));
            quickActions.appendChild(messenger);
        }
        if (quickActions.childElementCount) {
            hero.appendChild(quickActions);
        }
        detail.appendChild(hero);

        var layout = document.createElement('div');
        layout.className = 'orbita-response-detail__layout';
        var summary = document.createElement('section');
        summary.className = 'orbita-response-detail__summary';
        var vacancyIcon = document.createElement('span');
        vacancyIcon.className = 'orbita-response-detail__summary-icon';
        vacancyIcon.appendChild(responseIcon('fa-solid fa-briefcase'));
        summary.appendChild(vacancyIcon);
        var vacancyCopy = document.createElement('div');
        var vacancyLabel = document.createElement('span');
        vacancyLabel.className = 'orbita-response-detail__eyebrow';
        vacancyLabel.textContent = 'Отклик на вакансию';
        vacancyCopy.appendChild(vacancyLabel);
        if (profile.vacancyUrl) {
            var vacancyLink = document.createElement('a');
            vacancyLink.className = 'orbita-response-detail__vacancy';
            vacancyLink.href = profile.vacancyUrl;
            vacancyLink.target = '_blank';
            vacancyLink.rel = 'noopener';
            vacancyLink.textContent = profile.vacancy || 'Вакансия не указана';
            vacancyCopy.appendChild(vacancyLink);
        } else {
            var vacancy = document.createElement('p');
            vacancy.className = 'orbita-response-detail__vacancy';
            vacancy.textContent = profile.vacancy || 'Вакансия не указана';
            vacancyCopy.appendChild(vacancy);
        }
        if (profile.source) {
            var source = document.createElement('span');
            source.className = 'orbita-response-detail__source';
            source.textContent = profile.source;
            vacancyCopy.appendChild(source);
        }
        summary.appendChild(vacancyCopy);

        var left = document.createElement('div');
        left.className = 'orbita-response-detail__main';
        left.appendChild(summary);
        var info = document.createElement('section');
        info.className = 'orbita-response-detail__panel';
        var infoTitle = document.createElement('h3');
        infoTitle.textContent = 'Данные кандидата';
        info.appendChild(infoTitle);
        var facts = document.createElement('dl');
        facts.className = 'orbita-response-detail__facts';
        var factLabels = ['Телефон', 'История номеров', 'Город', 'Аккаунт', 'Воркер', 'Источник', 'ID отклика', 'Сбор', 'Отклик'];
        var routeSections = [];
        sections.forEach(function (section) {
            if (factLabels.indexOf(section.label) >= 0) {
                appendResponseFact(facts, section);
            } else if (['Возраст', 'Пол', 'Объявление'].indexOf(section.label) < 0) {
                routeSections.push(section);
            }
        });
        if (facts.childElementCount) {
            info.appendChild(facts);
        } else {
            var noFacts = document.createElement('p');
            noFacts.className = 'orbita-response-detail__empty';
            noFacts.textContent = 'Дополнительных данных пока нет.';
            info.appendChild(noFacts);
        }
        left.appendChild(info);

        if (routeSections.length) {
            var routing = document.createElement('section');
            routing.className = 'orbita-response-detail__panel orbita-response-detail__panel--routing';
            var routingTitle = document.createElement('h3');
            routingTitle.textContent = 'Статус и отправка';
            routing.appendChild(routingTitle);
            var routeFacts = document.createElement('dl');
            routeFacts.className = 'orbita-response-detail__facts';
            routeSections.forEach(function (section) { appendResponseFact(routeFacts, section); });
            routing.appendChild(routeFacts);
            left.appendChild(routing);
        }
        layout.appendChild(left);

        var chat = document.createElement('aside');
        chat.className = 'orbita-response-detail__history';
        var chatTitle = document.createElement('h3');
        chatTitle.textContent = 'Чат';
        chat.appendChild(chatTitle);
        var thread = document.createElement('div');
        thread.className = 'orbita-response-detail__chat-thread';
        messages.forEach(function (message) {
            thread.appendChild(createResponseChatBubble(message));
        });
        if (!thread.childElementCount) {
            var emptyChat = document.createElement('p');
            emptyChat.className = 'orbita-response-detail__empty';
            emptyChat.textContent = 'Переписка с кандидатом пока не найдена.';
            thread.appendChild(emptyChat);
        }
        chat.appendChild(thread);
        layout.appendChild(chat);
        detail.appendChild(layout);
        return detail;
    }

    runtime.getRowDetailLinks = function getRowDetailLinks(row) {
        var links = [];
        var logUrl = row.getAttribute('data-detail-log-url');
        var accountUrl = row.getAttribute('data-detail-account-url');
        var workerUrl = row.getAttribute('data-detail-worker-url');

        if (logUrl) {
            links.push({ href: logUrl, label: 'Открыть лог', icon: 'fa-regular fa-file-lines' });
        }
        if (accountUrl) {
            links.push({ href: accountUrl, label: 'К аккаунту', icon: 'fa-regular fa-user' });
        }
        if (workerUrl) {
            links.push({ href: workerUrl, label: 'К воркеру', icon: 'fa-solid fa-server' });
        }

        return links;
    }

    runtime.getRowDetailOptions = function getRowDetailOptions(row) {
        if (!row) return null;

        var primaryActions = null;
        if (window.OrbitaCaptchaSolver && window.OrbitaCaptchaSolver.canSolveRow(row)) {
            primaryActions = [{
                action: 'captcha-solve',
                label: 'Пройти капчу',
                tone: 'primary',
                payload: window.OrbitaCaptchaSolver.payloadFromRow(row)
            }];
        }

        var options = {
            title: row.getAttribute('data-detail-title') || 'Детали',
            subtitle: row.getAttribute('data-detail-subtitle') || '',
            body: row.getAttribute('data-detail-body') || row.getAttribute('data-copy') || '',
            attachmentUrl: row.getAttribute('data-detail-attachment') || '',
            links: runtime.getRowDetailLinks(row),
            copyText: row.getAttribute('data-copy') || ''
        };

        if (row.classList.contains('events-row')) {
            options.copyLabel = 'Копировать сообщение';
            if (primaryActions) {
                options.primaryActions = primaryActions;
            }
            options.dismiss = {
                eventId: row.getAttribute('data-event-id') || '',
                url: '/Events/Dismiss',
                rowSelector: '.events-row',
                title: 'Отметить обработанным?',
                message: 'Событие будет скрыто из списка.',
                label: 'Отметить обработанным'
            };
        } else if (row.classList.contains('errors-row')) {
            options.copyLabel = 'Копировать текст';
            if (primaryActions) {
                options.primaryActions = primaryActions;
            }
            options.dismiss = {
                eventId: row.getAttribute('data-event-id') || '',
                url: '/Errors/Dismiss',
                rowSelector: '.errors-row',
                title: 'Отметить как обработанную?',
                message: 'Ошибка будет скрыта из списка.',
                label: 'Отметить как обработанную'
            };
        } else if (row.classList.contains('dash-event-row--detail')) {
            var isError = row.getAttribute('data-is-error') === 'true';
            if (isError) {
                options.copyLabel = 'Копировать текст';
                options.dismiss = {
                    eventId: row.getAttribute('data-event-id') || '',
                    url: '/Errors/Dismiss',
                    rowSelector: '.dash-event-row--detail',
                    title: 'Отметить как обработанную?',
                    message: 'Ошибка будет скрыта из списка.',
                    label: 'Отметить как обработанную'
                };
            } else {
                options.copyLabel = 'Копировать сообщение';
                options.dismiss = {
                    eventId: row.getAttribute('data-event-id') || '',
                    url: '/Events/Dismiss',
                    rowSelector: '.dash-event-row--detail',
                    title: 'Отметить обработанным?',
                    message: 'Событие будет скрыто из списка.',
                    label: 'Отметить обработанным'
                };
            }

            if (primaryActions) {
                options.primaryActions = primaryActions;
            }
        }

        return options;
    }

    runtime.openDetailFromRow = function openDetailFromRow(row) {
        var options = runtime.getRowDetailOptions(row);
        if (!options) return;
        runtime.openDetailModal(options);
    }

    runtime.renderDetailPrimaryActions = function renderDetailPrimaryActions(actions) {
        if (!detailPrimary) return false;
        detailPrimary.innerHTML = '';
        var items = actions || [];
        if (!items.length) {
            detailPrimary.setAttribute('hidden', '');
            return false;
        }

        items.forEach(function (action) {
            if (action.action === 'captcha-solve' && action.payload && window.OrbitaCaptchaSolver) {
                var btn = document.createElement('button');
                btn.type = 'button';
                btn.className = 'orbita-detail-modal__action orbita-detail-modal__action--primary';
                btn.textContent = action.label || 'Пройти капчу';
                btn.addEventListener('click', function () {
                    runtime.closeDetailModal();
                    window.OrbitaCaptchaSolver.open(action.payload);
                });
                detailPrimary.appendChild(btn);
                return;
            }

            if (action.action === 'send-bitrix' && action.responseId) {
                var sendBtn = document.createElement('button');
                sendBtn.type = 'button';
                sendBtn.className = 'orbita-detail-modal__action orbita-detail-modal__action--primary';
                sendBtn.textContent = action.label || 'Отправить…';
                sendBtn.addEventListener('click', function () {
                    runtime.closeDetailModal();
                    if (window.OrbitaResponses && typeof window.OrbitaResponses.openSendBitrixModal === 'function') {
                        window.OrbitaResponses.openSendBitrixModal(action.responseId);
                    }
                });
                detailPrimary.appendChild(sendBtn);
                return;
            }

            if (action.action === 'resend' && action.responseId) {
                var form = document.createElement('form');
                form.method = 'post';
                form.action = '/Responses/Resend';
                form.className = 'orbita-detail-modal__primary-form';
                var token = document.querySelector('input[name="__RequestVerificationToken"]');
                if (token) {
                    var tokenInput = document.createElement('input');
                    tokenInput.type = 'hidden';
                    tokenInput.name = '__RequestVerificationToken';
                    tokenInput.value = token.value;
                    form.appendChild(tokenInput);
                }
                var idInput = document.createElement('input');
                idInput.type = 'hidden';
                idInput.name = 'id';
                idInput.value = action.responseId;
                form.appendChild(idInput);
                var btn = document.createElement('button');
                btn.type = 'submit';
                btn.className = 'orbita-detail-modal__action orbita-detail-modal__action--primary';
                btn.textContent = action.label || 'Отправить';
                form.appendChild(btn);
                detailPrimary.appendChild(form);
                return;
            }

            var anchor = document.createElement('a');
            anchor.className = 'orbita-detail-modal__action orbita-detail-modal__action--' + (action.tone || 'secondary');
            anchor.href = action.href || '#';
            anchor.textContent = action.label || '';
            if (action.external) {
                anchor.target = '_blank';
                anchor.rel = 'noopener';
            }
            detailPrimary.appendChild(anchor);
        });
        detailPrimary.removeAttribute('hidden');
        return true;
    }

    runtime.setDetailFooter = function setDetailFooter(options) {
        if (!detailFoot || !detailNav || !detailCopy || !detailCopyLabel || !detailDismiss || !detailDismissLabel) return;

        options = options || {};
        var links = options.links || [];
        var hasPrimary = runtime.renderDetailPrimaryActions(options.primaryActions);
        var hasLinks = links.length > 0;
        var hasCopy = !!options.copyText;
        var dismiss = options.dismiss || null;
        var hasDismiss = !!(dismiss && dismiss.eventId);

        detailNav.innerHTML = '';
        if (hasLinks) {
            links.forEach(function (link) {
                var anchor = document.createElement('a');
                anchor.className = 'orbita-detail-modal__nav-link';
                anchor.href = link.href;
                anchor.innerHTML = '<i class="' + link.icon + '" aria-hidden="true"></i><span>' + link.label + '</span>';
                detailNav.appendChild(anchor);
            });
            detailNav.removeAttribute('hidden');
        } else {
            detailNav.setAttribute('hidden', '');
        }

        if (hasCopy) {
            detailCopy.dataset.copyText = options.copyText;
            detailCopyLabel.textContent = options.copyLabel || 'Копировать';
            detailCopy.removeAttribute('hidden');
        } else {
            detailCopy.removeAttribute('data-copy-text');
            detailCopy.setAttribute('hidden', '');
        }

        detailDismiss.removeAttribute('data-event-id');
        detailDismiss.removeAttribute('data-dismiss-url');
        detailDismiss.removeAttribute('data-dismiss-row-selector');
        detailDismiss.removeAttribute('data-dismiss-title');
        detailDismiss.removeAttribute('data-dismiss-message');

        if (hasDismiss) {
            detailDismiss.setAttribute('data-event-id', dismiss.eventId);
            detailDismiss.setAttribute('data-dismiss-url', dismiss.url);
            detailDismiss.setAttribute('data-dismiss-row-selector', dismiss.rowSelector);
            detailDismiss.setAttribute('data-dismiss-title', dismiss.title);
            detailDismiss.setAttribute('data-dismiss-message', dismiss.message);
            detailDismissLabel.textContent = dismiss.label;
            detailDismiss.removeAttribute('hidden');
        } else {
            detailDismiss.setAttribute('hidden', '');
            detailDismissLabel.textContent = '';
        }

        if (hasLinks || hasCopy || hasDismiss || hasPrimary) {
            detailFoot.removeAttribute('hidden');
        } else {
            detailFoot.setAttribute('hidden', '');
        }
    }

    runtime.initDetailModal = function initDetailModal() {
        detailModal = document.getElementById('orbitaDetailModal');
        if (!detailModal || detailModal.hasAttribute('data-orbita-detail-ready')) return;

        detailTitle = detailModal.querySelector('#orbitaDetailTitle');
        detailSubtitle = detailModal.querySelector('#orbitaDetailSubtitle');
        detailBody = detailModal.querySelector('#orbitaDetailBody');
        detailFoot = detailModal.querySelector('#orbitaDetailFoot');
        detailNav = detailModal.querySelector('#orbitaDetailNav');
        detailCopy = detailModal.querySelector('#orbitaDetailCopy');
        detailCopyLabel = detailModal.querySelector('#orbitaDetailCopyLabel');
        detailDismiss = detailModal.querySelector('#orbitaDetailDismiss');
        detailDismissLabel = detailModal.querySelector('#orbitaDetailDismissLabel');
        detailPrimary = detailModal.querySelector('#orbitaDetailPrimary');

        detailModal.querySelectorAll('[data-orbita-detail-close]').forEach(function (btn) {
            btn.addEventListener('click', runtime.closeDetailModal);
        });

        if (detailCopy) {
            detailCopy.addEventListener('click', function (e) {
                e.stopPropagation();
                var text = detailCopy.dataset.copyText || '';
                if (text) runtime.copyText(text);
            });
        }

        if (detailDismiss) {
            detailDismiss.addEventListener('click', function (e) {
                e.stopPropagation();
                var url = detailDismiss.getAttribute('data-dismiss-url');
                var rowSelector = detailDismiss.getAttribute('data-dismiss-row-selector');
                var title = detailDismiss.getAttribute('data-dismiss-title');
                var message = detailDismiss.getAttribute('data-dismiss-message');
                if (!url || !rowSelector) return;
                handleDismissRow(detailDismiss, url, rowSelector, title, message);
            });
        }

        document.addEventListener('keydown', function (e) {
            if (e.key === 'Escape' && detailModal && !detailModal.hasAttribute('hidden')) {
                runtime.closeDetailModal();
            }
        });

        detailModal.setAttribute('data-orbita-detail-ready', '1');
    }

    runtime.openDetailModal = function openDetailModal(options) {
        runtime.initDetailModal();
        if (!detailModal || !detailTitle || !detailBody) return;

        var isResponseDetail = !!options.responseProfile;
        detailTitle.textContent = isResponseDetail ? 'Детали отклика' : (options.title || 'Детали');
        if (detailSubtitle) {
            if (!isResponseDetail && options.subtitle) {
                detailSubtitle.textContent = options.subtitle;
                detailSubtitle.removeAttribute('hidden');
            } else {
                detailSubtitle.textContent = '';
                detailSubtitle.setAttribute('hidden', '');
            }
        }
        detailCloseHandler = options.onClose || null;
        detailBody.textContent = '';
        detailModal.classList.remove('orbita-detail-modal--chat');
        detailModal.classList.toggle('orbita-detail-modal--response', isResponseDetail);

        if (isResponseDetail) {
            detailBody.appendChild(createResponseDetail(options));
        } else if (options.attachmentUrl) {
            var img = document.createElement('img');
            img.className = 'orbita-detail-screenshot';
            img.alt = 'Скриншот страницы';
            img.src = options.attachmentUrl;
            detailBody.appendChild(img);
        }

        if (!isResponseDetail && options.sections && options.sections.length) {
            var dl = document.createElement('dl');
            dl.className = 'orbita-detail-sections';
            options.sections.forEach(function (section) {
                var dt = document.createElement('dt');
                dt.textContent = section.label || '';
                var dd = document.createElement('dd');
                if (section.values && section.values.length) {
                    var list = document.createElement('ul');
                    list.className = 'orbita-detail-value-list';
                    section.values.forEach(function (value) {
                        var item = document.createElement('li');
                        item.textContent = value || '';
                        list.appendChild(item);
                    });
                    dd.appendChild(list);
                } else if (section.href) {
                    var link = document.createElement('a');
                    link.href = section.href;
                    link.textContent = section.value || '';
                    link.target = '_blank';
                    link.rel = 'noopener';
                    dd.appendChild(link);
                } else {
                    dd.textContent = section.value || '';
                }
                dl.appendChild(dt);
                dl.appendChild(dd);
            });
            detailBody.appendChild(dl);
        } else if (!isResponseDetail && options.body) {
            var text = document.createElement('p');
            text.className = 'orbita-detail-text';
            text.textContent = options.body;
            detailBody.appendChild(text);
        }

        if (!isResponseDetail && options.chatMessages && options.chatMessages.length) {
            detailModal.classList.add('orbita-detail-modal--chat');
            var chat = document.createElement('section');
            chat.className = 'orbita-detail-chat';
            chat.setAttribute('aria-label', 'Переписка');
            var chatTitle = document.createElement('h3');
            chatTitle.className = 'orbita-detail-chat__title';
            chatTitle.textContent = 'Чат';
            chat.appendChild(chatTitle);
            var thread = document.createElement('div');
            thread.className = 'orbita-detail-chat__thread';
            options.chatMessages.forEach(function (message) {
                var bubble = document.createElement('div');
                bubble.className = 'orbita-detail-chat__bubble orbita-detail-chat__bubble--' + (message.tone || 'incoming');
                var bubbleText = document.createElement('div');
                bubbleText.className = 'orbita-detail-chat__text';
                bubbleText.textContent = message.text || '';
                bubble.appendChild(bubbleText);
                if (message.timeLabel) {
                    var time = document.createElement('time');
                    time.className = 'orbita-detail-chat__time';
                    time.textContent = message.timeLabel;
                    bubble.appendChild(time);
                }
                thread.appendChild(bubble);
            });
            chat.appendChild(thread);
            detailBody.appendChild(chat);
        }

        runtime.setDetailFooter({
            links: options.links || [],
            copyText: options.copyText || '',
            copyLabel: options.copyLabel || 'Копировать',
            dismiss: options.dismiss || null,
            primaryActions: options.primaryActions || []
        });
        detailModal.classList.toggle('orbita-detail-modal--media', !!options.attachmentUrl);
        detailModal.removeAttribute('hidden');
        detailBody.scrollTop = 0;
        runtime.closeAllPopovers();
    }

    runtime.closeDetailModal = function closeDetailModal() {
        if (!detailModal) return;
        if (typeof detailCloseHandler === 'function') {
            detailCloseHandler();
            detailCloseHandler = null;
        }
        detailModal.setAttribute('hidden', '');
        detailModal.classList.remove('orbita-detail-modal--media', 'orbita-detail-modal--chat', 'orbita-detail-modal--response');
        runtime.setDetailFooter(null);
    }

})(window.OrbitaRuntime = window.OrbitaRuntime || {});
