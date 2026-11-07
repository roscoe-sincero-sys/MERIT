/* First-party navigation metadata only. No URLs, queries, page text, patient ids or form values leave this module. */
(() => {
    'use strict';
    const endpoint = '/api/v1/usage/';
    const guid = () => crypto.randomUUID();
    const read = key => { try { return sessionStorage.getItem('merit.usage.' + key); } catch { return null; } };
    const write = (key, value) => { try { sessionStorage.setItem('merit.usage.' + key, value); } catch { /* optional correlation */ } };
    const token = () => {
        const value = document.cookie.split('; ').find(c => c.startsWith('XSRF-TOKEN='));
        return value ? decodeURIComponent(value.slice('XSRF-TOKEN='.length)) : '';
    };
    const tabId = read('tab') || guid();
    write('tab', tabId);
    let current = null;
    let milliseconds = 0;
    let since = null;
    let lastSentMs = null;
    let lastSentAt = null;
    let ready = Promise.resolve();
    const visible = () => document.visibilityState === 'visible' && document.hasFocus();
    const pause = () => {
        if (since !== null) milliseconds = Math.min(1800000, milliseconds + performance.now() - since);
        since = null;
    };
    const resume = () => { if (current && visible() && since === null) since = performance.now(); };
    const checkpoint = (force = false) => {
        pause();
        const duration = Math.round(milliseconds);
        const now = performance.now();
        if (current && token() && duration !== lastSentMs && (force || lastSentAt === null || now - lastSentAt >= 5000)) {
            const data = new FormData();
            data.set('__RequestVerificationToken', token());
            data.set('visitId', current.id);
            data.set('durationMs', String(duration));
            // The form token protects the unload beacon as well as ordinary authenticated requests.
            if (navigator.sendBeacon(endpoint + 'duration', data)) {
                lastSentMs = duration;
                lastSentAt = now;
            }
        }
        resume();
    };
    const begin = (code, id = guid(), navigationType = 'router') => {
        if (!code || current?.id === id) return;
        checkpoint(true);
        const previousVisitId = read('last');
        current = { id, code };
        milliseconds = 0;
        since = null;
        lastSentMs = null;
        lastSentAt = null;
        write('last', id);
        const body = JSON.stringify({ visitId: id, screenCode: code, tabId, previousVisitId, navigationType });
        const post = () => fetch(endpoint + 'visits', {
            method: 'POST', credentials: 'same-origin', keepalive: true,
            headers: { 'Content-Type': 'application/json', 'X-XSRF-TOKEN': token() }, body,
        });
        // A full-page predecessor's keepalive can arrive late. Retry this idempotent visit once
        // only when the server requests reconciliation; never retain an unvalidated link in SQL.
        ready = ready.catch(() => {}).then(async () => {
            const response = await post();
            if (response.status === 202) {
                await new Promise(resolve => setTimeout(resolve, 750));
                await post();
            }
        }).catch(() => {});
        resume();
    };
    const initial = document.querySelector('meta[name="merit-screen-visit"]');
    if (initial) {
        const type = performance.getEntriesByType('navigation')[0]?.type;
        const hint = type === 'back_forward' ? 'back' : type === 'reload' ? 'reload' : read('next') || 'direct';
        write('next', 'direct');
        begin(initial.dataset.screenCode, initial.content, hint);
    }
    document.addEventListener('merit:usageScreen', event => {
        const code = event.detail?.code;
        if (typeof code === 'string') begin(code, guid(), event.detail.back ? 'back' : 'router');
    });
    document.addEventListener('htmx:after:swap', () => {
        // A successful manual-save outcome is a real displayed state even though htmx did not change the URL.
        const saved = document.querySelector('#result-saved[data-screen-visit]');
        if (saved?.dataset.screenVisit) begin(saved.dataset.screenCode, saved.dataset.screenVisit, 'redirect');
    });
    document.addEventListener('click', event => {
        const link = event.target.closest?.('a[href]');
        if (link && link.target !== '_blank' && link.origin === location.origin)
            write('next', link.closest('nav') ? 'menu' : 'link');
    }, true);
    document.addEventListener('submit', event => write('next', event.target.method.toLowerCase() === 'get' ? 'search' : 'redirect'), true);
    document.addEventListener('visibilitychange', () => checkpoint(true));
    window.addEventListener('blur', () => checkpoint());
    window.addEventListener('focus', resume);
    window.addEventListener('pagehide', () => { checkpoint(true); pause(); });
    window.addEventListener('pageshow', event => { if (event.persisted && current) begin(current.code, guid(), 'back'); });
})();
