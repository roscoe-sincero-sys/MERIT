/* Private patient-document upload. A retry retains the same upload UUID so a lost response cannot duplicate a file. */
(() => {
    'use strict';
    const form = document.getElementById('patient-document-form');
    if (!form || !window.XMLHttpRequest || !window.FormData) return;
    const status = document.getElementById('upload-status');
    const progress = document.getElementById('upload-progress');
    const cancel = document.getElementById('cancel-upload');
    const submit = document.getElementById('upload-document');
    const refresh = document.getElementById('upload-refresh');
    // Force a new GET even when the current URL already ends in #documents. Fragment-only navigation cannot refresh.
    const listUrl = new URL(refresh.href);
    listUrl.searchParams.set('refresh', Date.now().toString());
    refresh.href = listUrl.href;
    let request;
    form.addEventListener('submit', event => {
        if (!form.checkValidity()) return;
        event.preventDefault();
        if (request) return;
        const xhr = new XMLHttpRequest(); request = xhr;
        xhr.open('POST', form.action);
        xhr.setRequestHeader('X-Merit-Upload', '1');
        submit.disabled = true; cancel.hidden = false; progress.hidden = false; progress.value = 0;
        status.textContent = 'Uploading document…';
        xhr.upload.onprogress = e => {
            if (e.lengthComputable) progress.value = Math.round(e.loaded * 100 / e.total);
            if (progress.value === 100) status.textContent = 'Upload received; waiting for validation and save…';
        };
        const finish = () => { request = null; cancel.hidden = true; submit.disabled = false; };
        xhr.onload = () => {
            finish();
            let message;
            try { message = JSON.parse(xhr.responseText).message; } catch (_) { /* Login expiry/errors may return HTML. */ }
            if (xhr.status >= 200 && xhr.status < 300 && message) {
                progress.value = 100;
                status.textContent = message; submit.disabled = true; refresh.hidden = false;
                form.reset();
                form.dispatchEvent(new Event('merit:formSaved', { bubbles: true }));
            } else {
                status.textContent = message || 'Upload was not confirmed. Check your session and the document list, then retry.';
                submit.textContent = 'Retry upload'; refresh.hidden = false;
            }
        };
        xhr.onerror = xhr.onabort = () => {
            finish(); status.textContent = 'Upload not confirmed. The server may already have saved it. Check the list or retry this same file; retrying will not create a duplicate.';
            submit.textContent = 'Retry upload'; refresh.hidden = false;
        };
        xhr.send(new FormData(form));
    });
    cancel.addEventListener('click', () => { if (request) request.abort(); });
})();
