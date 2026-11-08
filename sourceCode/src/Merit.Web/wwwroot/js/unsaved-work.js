/* Drafts live only in this document's memory. Never store patient text in browser storage. */
(() => {
    'use strict';
    const selector = '[data-preserve-drafts] form[id], form[data-unsaved-work][id]';
    const baseline = new Map();
    const forced = new Set();
    const forms = () => [...document.querySelectorAll(selector)];
    const value = form => JSON.stringify([...form.elements]
        .filter(el => el.name && !['hidden', 'submit', 'button'].includes(el.type))
        .map(el => [el.name, el.type === 'checkbox' || el.type === 'radio' ? el.checked : el.value]));
    const capture = () => {
        for (const form of forms()) if (!baseline.has(form.id)) {
            baseline.set(form.id, value(form));
            if (form.dataset.unsavedDirty === 'True' || form.dataset.unsavedDirty === 'true') forced.add(form.id);
        }
        for (const id of baseline.keys()) if (!document.getElementById(id)) { baseline.delete(id); forced.delete(id); }
    };
    const dirty = except => forms().some(form => form !== except &&
        (forced.has(form.id) || (baseline.has(form.id) && value(form) !== baseline.get(form.id))));
    const dialog = document.getElementById('unsaved-work-dialog');
    let pending = null, opener = null, replay = false, leaving = false;
    const ask = action => {
        if (dialog.open) return;
        pending = action;
        opener = document.activeElement;
        dialog.showModal();
        dialog.querySelector('[data-unsaved-stay]').focus();
    };
    const stay = () => {
        pending = null;
        dialog.close();
        if (opener?.isConnected) opener.focus();
    };
    dialog.querySelector('[data-unsaved-stay]').addEventListener('click', stay);
    dialog.addEventListener('cancel', event => { event.preventDefault(); stay(); });
    dialog.querySelector('[data-unsaved-leave]').addEventListener('click', () => {
        const action = pending;
        pending = null;
        dialog.close();
        replay = true; leaving = true;
        try { action?.(); } finally { replay = false; }
        // A cancelled navigation or a download must not suppress future warnings.
        setTimeout(() => { leaving = false; }, 0);
    });
    document.addEventListener('click', event => {
        if (replay || event.defaultPrevented || event.button !== 0 || event.ctrlKey || event.metaKey || event.shiftKey || event.altKey) return;
        const link = event.target.closest('a[href]');
        if (!link || link.target === '_blank' || link.hasAttribute('download') || link.hasAttribute('hx-get')) return;
        const url = new URL(link.href, location.href);
        if (!['http:', 'https:'].includes(url.protocol)) return;
        if (url.pathname === location.pathname && url.search === location.search && url.hash) return;
        if (!dirty()) return;
        event.preventDefault(); event.stopImmediatePropagation();
        ask(() => link.click());
    }, true);
    document.addEventListener('submit', event => {
        if (replay || event.defaultPrevented) return;
        const form = event.target;
        if (form.hasAttribute('hx-post') || form.hasAttribute('hx-get')) return;
        if (!dirty(form)) { leaving = true; setTimeout(() => { leaving = false; }, 0); return; }
        event.preventDefault(); event.stopImmediatePropagation();
        const submitter = event.submitter;
        ask(() => form.requestSubmit(submitter));
    }, true);
    window.addEventListener('beforeunload', event => {
        if (leaving || !dirty()) return;
        event.preventDefault(); event.returnValue = '';
    });
    window.addEventListener('pageshow', () => { leaving = false; });
    document.addEventListener('htmx:after:swap', event => {
        const ctx = event.detail?.ctx, source = ctx?.sourceElement;
        if (ctx?.response?.status >= 200 && ctx.response.status < 300 &&
            source?.matches('form[hx-post], button[type=submit], input[type=submit]')) {
            const form = source.closest('form');
            if (form) { baseline.delete(form.id); forced.delete(form.id); }
        }
        // merit.js restores edited sibling fields before this listener; retain their original baseline.
        capture();
    });
    document.addEventListener('merit:formSaved', event => {
        const form = event.target;
        if (form.matches?.(selector)) { baseline.set(form.id, value(form)); forced.delete(form.id); }
    });
    capture();
})();
