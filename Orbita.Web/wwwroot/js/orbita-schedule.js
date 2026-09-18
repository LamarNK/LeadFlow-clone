(function () {
    'use strict';

    function root() { return document.querySelector('[data-orbita-live-page="schedule"]'); }
    function token(page) { var input = page.querySelector('input[name="__RequestVerificationToken"]'); return input ? input.value : ''; }
    function toast(message, variant) { if (window.Orbita && window.Orbita.toast) window.Orbita.toast(message, { variant: variant || 'success' }); }
    async function post(page, url, body) {
        var response = await fetch(url, {
            method: 'POST', credentials: 'same-origin',
            headers: { 'Content-Type': 'application/json', 'RequestVerificationToken': token(page) },
            body: JSON.stringify(body || {})
        });
        var payload = null;
        try { payload = await response.json(); } catch (_) { }
        if (!response.ok) throw new Error(payload && payload.error ? payload.error : 'Не удалось сохранить расписание.');
        return payload || {};
    }
    function reload() { window.location.reload(); }
    function bindModal(open, modal, closeSelector) {
        if (!open || !modal) return;
        open.addEventListener('click', function () { modal.hidden = false; document.body.style.overflow = 'hidden'; });
        modal.querySelectorAll(closeSelector).forEach(function (button) { button.addEventListener('click', function () { modal.hidden = true; document.body.style.overflow = ''; }); });
    }
    function setBusy(button, busy) { if (!button) return; button.disabled = busy; button.classList.toggle('is-loading', busy); }
    function showLoading(page) { var loading = page.querySelector('[data-schedule-loading]'); if (loading) loading.hidden = false; page.setAttribute('aria-busy', 'true'); }
    function hideLoading(page) { var loading = page.querySelector('[data-schedule-loading]'); if (loading) loading.hidden = true; page.removeAttribute('aria-busy'); }

    function init() {
        var page = root();
        if (!page || page.dataset.scheduleBound === '1') return;
        page.dataset.scheduleBound = '1';
        var overview = page.querySelector('[data-group-id]');
        if (!overview) return;
        var groupId = overview.dataset.groupId;

        var addModal = page.querySelector('[data-schedule-add-modal]');
        bindModal(page.querySelector('[data-schedule-add-open]'), addModal, '[data-schedule-modal-close]');
        var settingsModal = page.querySelector('[data-schedule-settings-modal]');
        bindModal(page.querySelector('[data-schedule-settings-open]'), settingsModal, '[data-schedule-settings-close]');

        var search = page.querySelector('[data-schedule-search]');
        if (search) search.addEventListener('input', function () {
            var query = search.value.trim().toLowerCase();
            page.querySelectorAll('[data-schedule-worker-row]').forEach(function (row) { row.hidden = query && row.dataset.search.indexOf(query) < 0; });
        });
        var candidateSearch = page.querySelector('[data-schedule-candidate-search]');
        if (candidateSearch) candidateSearch.addEventListener('input', function () {
            var query = candidateSearch.value.trim().toLowerCase();
            page.querySelectorAll('[data-candidate]').forEach(function (row) { row.hidden = query && row.dataset.search.indexOf(query) < 0; });
        });

        var addSubmit = page.querySelector('[data-schedule-add-submit]');
        if (addSubmit) addSubmit.addEventListener('click', async function () {
            var selected = Array.from(page.querySelectorAll('[data-candidate-check]:checked'));
            var error = page.querySelector('[data-schedule-add-error]');
            if (!selected.length) { error.textContent = 'Выберите хотя бы одного воркера.'; error.hidden = false; return; }
            var shift = page.querySelector('input[name="schedule-add-shift"]:checked').value;
            var workerIds = [];
            var moves = [];
            var cancelled = 0;
            for (var i = 0; i < selected.length; i++) {
                var existing = selected[i].dataset.existingGroup;
                if (!existing) { workerIds.push(selected[i].value); continue; }
                if (existing !== groupId) {
                    var confirmed = window.Orbita && window.Orbita.confirm ? await window.Orbita.confirm({ title: 'Переместить воркера?', message: 'Воркер уже находится в другой группе. Текущее назначение будет заменено.', confirmLabel: 'Переместить' }) : false;
                    if (!confirmed) { cancelled++; continue; }
                }
                moves.push(selected[i].value);
            }
            if (!workerIds.length && !moves.length) {
                error.textContent = 'Перемещение отменено. Выберите других воркеров или закройте окно.';
                error.hidden = false;
                return;
            }
            var saved = 0;
            try {
                setBusy(addSubmit, true); showLoading(page);
                for (var moveIndex = 0; moveIndex < moves.length; moveIndex++) {
                    await post(page, '/Schedule/Move', { workerId: moves[moveIndex], groupId: groupId, shift: shift });
                    saved++;
                }
                if (workerIds.length) {
                    await post(page, '/Schedule/Assign', { groupId: groupId, workerIds: workerIds, shift: shift });
                    saved += workerIds.length;
                }
                toast(cancelled ? 'Расписание обновлено. Отменённые перемещения пропущены.' : 'Воркеры добавлены в расписание.');
                reload();
            } catch (e) {
                error.textContent = saved ? 'Часть изменений сохранена. ' + e.message : e.message;
                error.hidden = false; hideLoading(page); setBusy(addSubmit, false);
            }
        });

        page.querySelectorAll('[data-schedule-worker-shift]').forEach(function (select) {
            select.addEventListener('change', async function () {
                select.disabled = true;
            try { showLoading(page); await post(page, '/Schedule/Move', { workerId: select.dataset.workerId, groupId: groupId, shift: select.value }); toast('Смена воркера обновлена.'); reload(); }
                catch (e) { toast(e.message, 'error'); hideLoading(page); select.disabled = false; }
            });
        });
        page.querySelectorAll('[data-schedule-remove]').forEach(function (button) {
            button.addEventListener('click', async function () {
                var confirmed = window.Orbita && window.Orbita.confirm ? await window.Orbita.confirm({ title: 'Удалить из группы?', message: 'Воркер «' + button.dataset.workerName + '» вернётся к индивидуальному расписанию.', confirmLabel: 'Удалить', variant: 'danger' }) : false;
                if (!confirmed) return;
                try { showLoading(page); await post(page, '/Schedule/Remove', { workerId: button.dataset.workerId }); toast('Воркер удалён из группы.'); reload(); }
                catch (e) { toast(e.message, 'error'); hideLoading(page); }
            });
        });
        var orientation = page.querySelector('[data-schedule-orientation]');
        if (orientation) orientation.addEventListener('change', async function () {
            orientation.disabled = true;
            try { showLoading(page); await post(page, '/Schedule/Group/' + groupId, { currentWeekShift: orientation.value }); toast('Начальная смена обновлена.'); reload(); }
            catch (e) { toast(e.message, 'error'); hideLoading(page); orientation.disabled = false; }
        });
        var auto = page.querySelector('[data-schedule-auto]');
        if (auto) auto.addEventListener('click', async function () {
            var confirmed = window.Orbita && window.Orbita.confirm ? await window.Orbita.confirm({ title: 'Распределить всех воркеров?', message: 'Текущие назначения офиса будут заменены равномерным распределением по 7 группам и двум сменам.', confirmLabel: 'Распределить' }) : false;
            if (!confirmed) return;
            try { setBusy(auto, true); showLoading(page); await post(page, '/Schedule/AutoDistribute', {}); toast('Воркеры распределены.'); reload(); }
            catch (e) { toast(e.message, 'error'); hideLoading(page); setBusy(auto, false); }
        });
        var settingsSubmit = page.querySelector('[data-schedule-settings-submit]');
        if (settingsSubmit) settingsSubmit.addEventListener('click', async function () {
            var error = page.querySelector('[data-schedule-settings-error]');
            var payload = { dayStartLocalTime: page.querySelector('[data-day-start]').value, dayEndLocalTime: page.querySelector('[data-day-end]').value, nightStartLocalTime: page.querySelector('[data-night-start]').value, nightEndLocalTime: page.querySelector('[data-night-end]').value, timeZoneId: page.querySelector('[data-timezone]').value };
            try { setBusy(settingsSubmit, true); showLoading(page); await post(page, '/Schedule/Settings', payload); toast('Время смен сохранено.'); reload(); }
            catch (e) { error.textContent = e.message; error.hidden = false; hideLoading(page); setBusy(settingsSubmit, false); }
        });
    }

    document.addEventListener('DOMContentLoaded', init);
    document.addEventListener('orbita:content-updated', init);
    if (document.readyState !== 'loading') init();
}());
