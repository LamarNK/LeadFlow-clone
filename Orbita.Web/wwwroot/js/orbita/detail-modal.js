(function (runtime) {
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

        detailTitle.textContent = options.title || 'Детали';
        if (detailSubtitle) {
            if (options.subtitle) {
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

        if (options.attachmentUrl) {
            var img = document.createElement('img');
            img.className = 'orbita-detail-screenshot';
            img.alt = 'Скриншот страницы';
            img.src = options.attachmentUrl;
            detailBody.appendChild(img);
        }

        if (options.sections && options.sections.length) {
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
        } else if (options.body) {
            var text = document.createElement('p');
            text.className = 'orbita-detail-text';
            text.textContent = options.body;
            detailBody.appendChild(text);
        }

        if (options.chatMessages && options.chatMessages.length) {
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
        detailModal.classList.remove('orbita-detail-modal--media', 'orbita-detail-modal--chat');
        runtime.setDetailFooter(null);
    }

})(window.OrbitaRuntime = window.OrbitaRuntime || {});
