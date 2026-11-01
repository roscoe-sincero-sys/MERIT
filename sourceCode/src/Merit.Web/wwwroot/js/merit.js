// MERIT site script for the Razor + htmx screens. Behaviour that needs script lives here, never in hx-on attributes
// or js: values (architecture: htmx requirements, Scripts and CSP). Loaded with the page's CSP nonce.
(() => {
    "use strict";

    const liveRegion = () => document.getElementById("live-region");

    function announce(text) {
        const region = liveRegion();
        if (!region || !text) return;
        region.textContent = "";
        window.setTimeout(() => { region.textContent = text; }, 50);
    }

    function cookie(name) {
        const match = document.cookie.split("; ").find(c => c.startsWith(name + "="));
        return match ? decodeURIComponent(match.substring(name.length + 1)) : null;
    }

    // Session recovery keeps drafts only in this document. Authentication happens in a separate tab.
    function alertRegion() {
        return document.querySelector('#dialog[open] #dialog-alert-region') || document.getElementById('alert-region');
    }

    function showError(message) {
        const region = alertRegion();
        if (!region) return null;
        region.replaceChildren();
        const div = document.createElement('div');
        div.className = 'usa-alert usa-alert--error usa-alert--slim';
        div.setAttribute('role', 'alert');
        div.setAttribute('tabindex', '-1');
        const body = document.createElement('div');
        body.className = 'usa-alert__body';
        const p = document.createElement('p');
        p.className = 'usa-alert__text';
        p.textContent = message;
        body.appendChild(p);
        div.appendChild(body);
        region.appendChild(div);
        return div;
    }

    let expiryTimer;
    let warningTimer;
    let sessionEnded = false;
    function sessionMessage(ended, confirmed = ended) {
        if (sessionEnded) return;
        if (ended) {
            clearTimeout(warningTimer);
            clearTimeout(expiryTimer);
            sessionEnded = confirmed;
        }
        const alert = showError(ended
            ? (confirmed
                ? 'Your session ended. Your unsaved work is still here. Sign in in a new tab, then return here and check your session before saving again.'
                : 'Your session may have ended. Your unsaved work is still here. Check your session before saving, or sign in in a new tab.')
            : 'Your session may expire soon. Your unsaved work is still here. Check your session or sign in in a new tab to continue.');
        if (!alert) return;
        const link = document.createElement('a');
        link.className = 'usa-link';
        link.textContent = 'Sign in in a new tab';
        link.href = '/account/signin?ReturnUrl=' + encodeURIComponent(location.pathname + location.search);
        link.target = '_blank';
        link.rel = 'noopener';
        alert.appendChild(link);
        const check = document.createElement('button');
        check.type = 'button';
        check.className = 'usa-button usa-button--outline';
        check.textContent = 'Check session';
        check.addEventListener('click', async () => {
            check.disabled = true;
            try {
                const response = await fetch('/api/v1/me', { cache: 'no-store' });
                if (!response.ok) {
                    if (response.status === 401) sessionMessage(true);
                    else showError('Could not check your session. Try again when MERIT is available.');
                    return;
                }
                const me = await response.json();
                if (me.userName !== document.body.dataset.sessionUser) {
                    showError('A different user is signed in. Copy your unsaved work before refreshing, or sign in again as the original user.');
                    return;
                }
                sessionEnded = false;
                sessionActivity({ detail: response.headers.get('X-Merit-Session-Expires') });
                alert.replaceChildren(document.createTextNode('You are signed in again. Review your draft and save when ready.'));
                alert.focus();
            } catch { showError('Could not check your session. Check your connection and try again.'); }
            finally { check.disabled = false; }
        });
        alert.appendChild(check);
        if (confirmed) alert.focus();
    }

    let sessionDeadline = Number(document.body.dataset.sessionExpires);
    function sessionActivity(evt) {
        if (sessionEnded) return;
        const minutes = Number(document.body.dataset.sessionMinutes);
        if (!minutes) return;
        if (evt?.detail) sessionDeadline = Number(evt.detail);
        if (!sessionDeadline) sessionDeadline = Date.now() + minutes * 60000;
        const remaining = Math.max(0, sessionDeadline - Date.now());
        clearTimeout(warningTimer);
        clearTimeout(expiryTimer);
        if (remaining === 0) { sessionMessage(true, false); return; }
        warningTimer = setTimeout(() => sessionMessage(false), Math.max(0, remaining - 2 * 60000));
        expiryTimer = setTimeout(() => sessionMessage(true, false), remaining);
    }
    document.addEventListener('merit:sessionExpired', () => sessionMessage(true));
    document.addEventListener('merit:sessionActivity', sessionActivity);
    sessionActivity();

    // ------------------------------------------------------------------------------------------ phone menu
    // Below 64em the menu and the user block fold behind the Menu button (issue #47); Escape closes it.
    const header = document.querySelector(".merit-header");
    const menuButton = header && header.querySelector(".merit-menu-button");
    if (menuButton) {
        header.classList.add("merit-header--js");
        const setOpen = open => {
            header.classList.toggle("merit-header--open", open);
            menuButton.setAttribute("aria-expanded", String(open));
        };
        menuButton.addEventListener("click", () => setOpen(!header.classList.contains("merit-header--open")));
        header.addEventListener("keydown", evt => {
            if (evt.key === "Escape" && header.classList.contains("merit-header--open")) {
                setOpen(false);
                menuButton.focus();
            }
        });
    }

    // ------------------------------------------------------------------------------------------ error summary
    // A form posted without htmx comes back as a whole page; its error summary takes focus (issue #46).
    const summary = document.querySelector(".merit-error-summary");
    if (summary) summary.focus();

    // ------------------------------------------------------------------------------------------ antiforgery
    // Both clients send the token in X-XSRF-TOKEN, read from the XSRF-TOKEN cookie.
    document.addEventListener("htmx:config:request", evt => {
        const token = cookie("XSRF-TOKEN");
        if (token && evt.detail && evt.detail.ctx && evt.detail.ctx.request) {
            evt.detail.ctx.request.headers["X-XSRF-TOKEN"] = token;
        }
        const ctx = evt.detail && evt.detail.ctx;
        if (ctx && ctx.sourceElement?.closest('#dialog[open]')) ctx.request.headers['X-Merit-Dialog'] = 'true';
        if (ctx?.sourceElement?.matches('[role=combobox]')) {
            const input = ctx.sourceElement;
            ctx.meritLookupQuery = input.value;
            ctx.meritLookupVersion = lookupVersions.get(input) || 0;
            ctx.request.body.set('q', input.value);
            ctx.request.body.delete(input.name);
        }
    });

    // Explicit middleware denials must never swap an error page over the user's form or dialog.
    document.addEventListener('htmx:after:request', evt => {
        const ctx = evt.detail && evt.detail.ctx;
        const status = ctx?.response?.status;
        if (status === 401) {
            evt.preventDefault();
            sessionMessage(true);
        } else if (status === 403 && (ctx.response.headers.get('X-Merit-Denied') === 'true' || !ctx.text?.trim())) {
            evt.preventDefault();
            const alert = showError('Your current profile no longer permits this action. Your inputs are still here. Copy any unsaved work, then refresh the page to see your current access.');
            const refresh = document.createElement('button');
            refresh.type = 'button';
            refresh.className = 'usa-button usa-button--outline';
            refresh.textContent = 'Refresh current profile';
            refresh.addEventListener('click', () => location.reload());
            alert?.appendChild(refresh);
            alert?.focus();
        } else if (status && status < 400) sessionActivity({ detail: ctx.response.headers.get('X-Merit-Session-Expires') });
    });

    // ------------------------------------------------------------------------------------------ busy state
    document.addEventListener("htmx:before:request", evt => {
        const ctx = evt.detail?.ctx;
        const target = ctx?.target;
        if (ctx) ctx.meritBusyTarget = target;
        if (target && target.setAttribute) target.setAttribute("aria-busy", "true");
    });

    document.addEventListener("htmx:finally:request", evt => {
        const ctx = evt.detail?.ctx;
        // A 422 status rule can retarget the swap, leaving the original region dimmed and busy.
        for (const target of [ctx?.meritBusyTarget, ctx?.target]) {
            if (target?.removeAttribute) target.removeAttribute("aria-busy");
        }
    });

    // A request that fails for lack of a connection shows a USWDS alert; nothing is queued in the browser.
    document.addEventListener("htmx:error", evt => {
        const status = evt.detail && evt.detail.ctx && evt.detail.ctx.response && evt.detail.ctx.response.status;
        if (status) return; // the server answered; its alert partial is already swapped in
        const region = alertRegion();
        if (region) {
            region.innerHTML = "";
            const div = document.createElement("div");
            div.className = "usa-alert usa-alert--error usa-alert--slim";
            div.setAttribute("role", "alert");
            div.innerHTML = '<div class="usa-alert__body"><p class="usa-alert__text"></p></div>';
            div.querySelector("p").textContent = "The connection to MERIT was lost. Check your network and try again.";
            region.appendChild(div);
        }
    });

    document.addEventListener('htmx:before:swap', evt => {
        const ctx = evt.detail?.ctx;
        if (!ctx) return;
        ctx.meritOpenDetails = [...document.querySelectorAll('details[data-preserve-open][id][open]')].map(el => el.id);
        const active = document.activeElement;
        if (active?.closest('#person-header')) ctx.meritContactFocus = active.id || 'person-contact-details';
    });
    document.addEventListener('htmx:after:swap', evt => {
        const ctx = evt.detail?.ctx;
        for (const id of ctx?.meritOpenDetails || []) {
            const details = document.getElementById(id);
            if (details) details.open = true;
        }
        if (ctx?.meritContactFocus && document.activeElement === document.body) (document.getElementById(ctx.meritContactFocus)?.querySelector('summary') || document.getElementById(ctx.meritContactFocus))?.focus();
    });
    document.addEventListener('submit', evt => {
        const form = evt.target.closest('form[data-upload-busy]');
        if (!form) return;
        if (form.dataset.submitting) { evt.preventDefault(); return; }
        form.dataset.submitting = 'true';
        form.setAttribute('aria-busy', 'true');
        form.querySelectorAll('[type=submit]:not(:disabled)').forEach(button => { button.dataset.uploadDisabled = 'true'; button.disabled = true; });
        const status = document.createElement('p'); status.setAttribute('role', 'status'); status.dataset.uploadStatus = 'true'; status.textContent = 'Uploading file…'; form.appendChild(status);
    });

    // ------------------------------------------------------------------------------------------ after a swap
    // Preserve only user-edited sibling fields, together with the version they were edited against.
    // The server alone redisplays the submitted form on validation failure.
    const formEdits = new Map();
    function recordEdit(evt) {
        const field = evt.target;
        const form = field.closest("[data-preserve-drafts] form[id]");
        if (!form || !field.id || field.type === "hidden") return;
        if (!formEdits.has(form.id)) formEdits.set(form.id, new Set());
        formEdits.get(form.id).add(field.id);
    }
    document.addEventListener("input", recordEdit);
    document.addEventListener("change", recordEdit);
    document.addEventListener("htmx:before:swap", evt => {
        const ctx = evt.detail && evt.detail.ctx;
        if (!ctx) return;
        const target = typeof ctx.target === "string" ? document.querySelector(ctx.target) : ctx.target;
        if (!target || !target.matches("[data-preserve-drafts]")) return;
        const submitted = ctx.sourceElement && ctx.sourceElement.closest("form");
        const failed = ctx.response && ctx.response.status >= 400;
        ctx.meritDrafts = [];
        target.querySelectorAll("form[id]").forEach(form => {
            if (form === submitted) {
                if (!failed) formEdits.delete(form.id);
                return;
            }
            const edited = formEdits.get(form.id);
            if (!edited) return;
            const fields = [...form.querySelectorAll("input[id], select[id], textarea[id]")]
                .filter(field => edited.has(field.id) && field.type !== "hidden" && field.type !== "file")
                .map(field => ({ id: field.id, value: field.value, checked: field.checked }));
            ctx.meritDrafts.push({ id: form.id, fields,
                version: form.querySelector('[name="rowVersion"]')?.value });
        });
    });
    // htmx 4 completes its settle tasks before emitting this event with the request context.
    document.addEventListener("htmx:after:swap", evt => {
        const ctx = evt.detail && evt.detail.ctx;
        for (const draft of (ctx && ctx.meritDrafts) || []) {
            const form = document.getElementById(draft.id);
            if (!form) continue;
            for (const saved of draft.fields) {
                const field = document.getElementById(saved.id);
                if (!field || !form.contains(field) || field.disabled) continue;
                field.value = saved.value;
                if (saved.checked !== undefined) field.checked = saved.checked;
            }
            const version = form.querySelector('[name="rowVersion"]');
            if (draft.fields.length && version && draft.version !== undefined) version.value = draft.version;
        }
        if (ctx && ctx.meritDrafts) {
            delete ctx.meritDrafts;
            for (const id of formEdits.keys()) if (!document.getElementById(id)) formEdits.delete(id);
        }
    });

    document.addEventListener("htmx:after:swap", evt => {
        const ctx = evt.detail && evt.detail.ctx;
        const target = ctx && ctx.target;

        // An error message in a form gets focus so a screen reader hears it: the error summary first (issue #46). A swap
        // chosen by hx-status (a 422 re-rendering its form) carries no target, and an outerHTML swap leaves the target
        // detached, so the new content is looked up in the document.
        if (ctx && ctx.response && ctx.response.status >= 400) {
            const scope = document.querySelector('#dialog[open]') || (target && target.isConnected ? target : document);
            const error = scope.querySelector(".merit-error-summary, .usa-error-message:not(:empty), .usa-alert--error, .usa-alert--warning");
            if (error) {
                error.setAttribute("tabindex", "-1");
                error.focus();
            }
        }

        if (!target) return;

        // After a swap that replaces the main content, focus moves to the new content's heading.
        if (target.id === "main-content" || target.hasAttribute("data-focus-heading")) {
            const heading = target.querySelector("h1, h2");
            if (heading) {
                heading.setAttribute("tabindex", "-1");
                heading.focus();
            }
        }

        // Content swapped into the shared dialog opens it as a modal.
        if (target.id === "dialog-body") {
            document.getElementById('dialog-alert-region')?.replaceChildren();
            const dialog = document.getElementById("dialog");
            if (dialog && target.childElementCount > 0 && !dialog.open) dialog.showModal();
            const first = target.querySelector("input:not([type=hidden]), select, textarea, button");
            if (first) first.focus();
        }
    });

    // Server-sent HX-Trigger events.
    document.addEventListener("merit:announce", evt => announce(evt.detail && (evt.detail.value || evt.detail)));

    // ------------------------------------------------------------------------------------------ dialog
    const formOpeners = new Map();
    function rememberOpener(source) {
        return { node: source, id: source.id, url: source.getAttribute('hx-get'),
            sectionId: source.closest('section[id]')?.id };
    }
    function restoreOpener(opener) {
        if (!opener) return;
        const section = document.getElementById(opener.sectionId);
        const replacement = opener.id && document.getElementById(opener.id);
        const trigger = opener.node.isConnected ? opener.node : replacement ||
            [...(section?.querySelectorAll('[hx-get]') || [])].find(el => el.getAttribute('hx-get') === opener.url);
        if (trigger && !trigger.disabled && trigger.getClientRects().length) { trigger.focus(); return; }
        const heading = section?.querySelector('h2, h3');
        if (heading) { heading.setAttribute('tabindex', '-1'); heading.focus(); }
    }
    document.addEventListener('htmx:before:request', evt => {
        const ctx = evt.detail?.ctx;
        const source = ctx?.sourceElement;
        const id = ctx?.target?.id;
        if (source?.hasAttribute('hx-get') && (id === 'dialog-body' || id?.endsWith('-form')) && !ctx.target.contains(source)) {
            formOpeners.set(id, rememberOpener(source));
        }
    });
    document.addEventListener("click", evt => {
        const closer = evt.target.closest("[data-close-dialog]");
        if (closer) {
            const dialog = document.getElementById("dialog");
            if (dialog && dialog.open) dialog.close();
        }

        // Collapsible inline forms ("Add relationship" etc.) clear themselves with a cancel button.
        const cancel = evt.target.closest("[data-clear-target]");
        if (cancel) {
            const t = document.querySelector(cancel.getAttribute("data-clear-target"));
            if (t) {
                const opener = formOpeners.get(t.id);
                t.replaceChildren();
                restoreOpener(opener);
                formOpeners.delete(t.id);
            }
        }
    });

    document.addEventListener("close", evt => {
        if (evt.target && evt.target.id === "dialog") {
            const body = document.getElementById("dialog-body");
            if (body) body.innerHTML = "";
            document.getElementById('dialog-alert-region')?.replaceChildren();
            // The opener's section may have been replaced by the save response.
            const opener = formOpeners.get('dialog-body');
            requestAnimationFrame(() => restoreOpener(opener));
            formOpeners.delete('dialog-body');
        }
    }, true);

    // Closing the dialog after a successful save inside it (the response replaced a page region instead).
    document.addEventListener("merit:closeDialog", () => {
        const dialog = document.getElementById("dialog");
        if (dialog && dialog.open) dialog.close();
    });

    // ------------------------------------------------------------------------------------------ combobox type-ahead
    // ARIA combobox pattern: the text input's hx-get returns a listbox of options (Views/Shared/_Options); arrow keys move,
    // Enter or click chooses, Escape closes, and the chosen value posts as a hidden id.
    const lookupVersions = new WeakMap();
    function options(listbox) {
        return Array.from(listbox.querySelectorAll('[role=option]:not([aria-disabled=true])[data-value]'));
    }

    function closeCombobox(input) {
        lookupVersions.set(input, (lookupVersions.get(input) || 0) + 1);
        const listbox = document.getElementById(input.getAttribute('aria-controls'));
        if (listbox) { listbox.replaceChildren(); listbox.hidden = true; }
        input.setAttribute('aria-expanded', 'false');
        input.removeAttribute('aria-activedescendant');
    }

    function choose(option) {
        if (option.getAttribute('aria-disabled') === 'true' || !option.hasAttribute('data-value')) return;
        const listbox = option.closest("[role=listbox]");
        const input = document.getElementById(listbox.getAttribute("data-for"));
        const hidden = document.getElementById(input.getAttribute("data-hidden"));
        hidden.value = option.getAttribute("data-value");
        input.value = option.getAttribute("data-label");
        closeCombobox(input);
        hidden.dispatchEvent(new Event("change", { bubbles: true }));
        announce("Selected " + input.value);
    }

    document.addEventListener("keydown", evt => {
        const input = evt.target.closest("[role=combobox]");
        if (!input) return;
        const listbox = document.getElementById(input.getAttribute("aria-controls"));
        if (!listbox) return;
        const opts = options(listbox);
        let index = opts.findIndex(o => o.getAttribute("aria-selected") === "true");
        if (evt.key === "ArrowDown" || evt.key === "ArrowUp") {
            if (opts.length === 0) return;
            evt.preventDefault();
            index = evt.key === "ArrowDown" ? Math.min(index + 1, opts.length - 1) : Math.max(index - 1, 0);
            opts.forEach((o, i) => o.setAttribute("aria-selected", i === index ? "true" : "false"));
            input.setAttribute("aria-activedescendant", opts[index].id);
            opts[index].scrollIntoView({ block: "nearest" });
        } else if (evt.key === "Enter" && index >= 0) {
            evt.preventDefault();
            choose(opts[index]);
        } else if (evt.key === "Escape") {
            evt.preventDefault();
            evt.stopPropagation();
            closeCombobox(input);
        }
    });

    document.addEventListener("input", evt => {
        const input = evt.target.closest("[role=combobox]");
        if (!input) return;
        const hidden = document.getElementById(input.getAttribute("data-hidden"));
        if (hidden) hidden.value = "";
        closeCombobox(input);
    });

    // Keep focus on the input until an option click has selected its value.
    document.addEventListener('pointerdown', evt => {
        if (evt.target.closest('[role=listbox][data-for] [role=option]')) evt.preventDefault();
        document.querySelectorAll('[role=combobox]').forEach(input => {
            if (!input.closest('.merit-combobox').contains(evt.target)) closeCombobox(input);
        });
    });
    document.addEventListener('focusout', evt => {
        const input = evt.target.closest('[role=combobox]');
        if (input) closeCombobox(input);
    });
    document.addEventListener('htmx:before:request', evt => {
        const input = evt.detail?.ctx?.sourceElement;
        if (input?.matches('[role=combobox]') && (input !== document.activeElement || input.disabled)) evt.preventDefault();
    });
    document.addEventListener('htmx:before:swap', evt => {
        const ctx = evt.detail?.ctx;
        const input = ctx?.sourceElement;
        if (input?.matches('[role=combobox]') &&
            (input !== document.activeElement || input.disabled || input.value !== ctx.meritLookupQuery ||
                (lookupVersions.get(input) || 0) !== ctx.meritLookupVersion)) evt.preventDefault();
    });

    document.addEventListener("click", evt => {
        const option = evt.target.closest("[role=option]");
        if (option && option.closest("[role=listbox][data-for]")) choose(option);
    });

    document.addEventListener("htmx:after:swap", evt => {
        const target = evt.detail && evt.detail.ctx && evt.detail.ctx.target;
        if (target && target.getAttribute && target.getAttribute("role") === "listbox") {
            const input = document.getElementById(target.getAttribute("data-for"));
            const count = options(target).length;
            if (input) {
                target.hidden = false;
                input.removeAttribute('aria-activedescendant');
                input.setAttribute("aria-expanded", "true");
            }
            announce(count === 0 ? "No matches" : count + " matches. Use the arrow keys to choose.");
        }
    });

    // ------------------------------------------------------------------------------------------ small conveniences
    function invalidatePrimaryPreview(evt) {
        const form = evt.target.closest("[data-primary-preview]");
        if (!form) return;
        const preview = form.closest("#set-primary");
        const confirm = preview && preview.querySelector("[data-primary-confirm]");
        if (confirm) confirm.disabled = true;
        const message = preview && preview.querySelector("[data-preview-stale]");
        if (message) message.hidden = false;
    }
    document.addEventListener("input", invalidatePrimaryPreview);
    document.addEventListener("change", invalidatePrimaryPreview);

    // A select with data-autosubmit submits its form (used by plain GET filter forms; htmx forms trigger on change).
    document.addEventListener("change", evt => {
        const el = evt.target.closest("[data-autosubmit]");
        if (el && el.form && !el.form.hasAttribute("hx-get") && !el.form.hasAttribute("hx-post")) el.form.requestSubmit();
    });

    function clearCombobox(box) {
        box.value = "";
        closeCombobox(box);
        const hidden = document.getElementById(box.getAttribute("data-hidden"));
        if (hidden) hidden.value = "";
        const listbox = document.getElementById(box.getAttribute("aria-controls"));
        if (listbox) listbox.innerHTML = "";
    }

    // Shows or hides dependent fields, disables inactive controls, and clears incompatible selections on role changes.
    function syncToggles(root, changedField) {
        const container = root || document;
        const targets = container.matches && container.matches("[data-show-when]")
            ? [container, ...container.querySelectorAll("[data-show-when]")]
            : container.querySelectorAll("[data-show-when]");
        targets.forEach(el => {
            const [name, values] = el.getAttribute("data-show-when").split("=");
            const scope = el.closest("form") || document;
            const field = scope.querySelector(`[name="${name}"]:checked, select[name="${name}"]`);
            const value = field ? field.value : "";
            const active = values.split("|").includes(value);
            el.hidden = !active;
            if ("disabled" in el) el.disabled = !active;
            el.querySelectorAll("input, select, textarea, button").forEach(ctrl => { ctrl.disabled = !active; });
            el.querySelectorAll("[role=combobox]").forEach(box => {
                if (box.getAttribute("data-field") === "OrganizationSite") {
                    if (changedField === field) box.dispatchEvent(new Event("htmx:abort"));
                    box.setAttribute("hx-get", "/lookup/sites?type=" + (value === "school" ? "school" : "childCare"));
                }
                if (!active || (changedField === field && box.getAttribute("data-field") === "OrganizationSite")) {
                    clearCombobox(box);
                }
            });
        });
    }

    document.addEventListener("change", evt => syncToggles(document, evt.target));
    document.addEventListener("htmx:after:settle", evt => syncToggles(evt.detail && evt.detail.ctx && evt.detail.ctx.target));
    document.addEventListener("DOMContentLoaded", () => syncToggles());
})();

document.addEventListener('change', function (event) {
    var type = event.target.closest('[data-sample-type]');
    if (!type) return;
    var form = type.closest('[data-sample-units]');
    var units = JSON.parse(form.dataset.sampleUnits)[type.value] || [];
    var select = form.querySelector('[data-sample-unit]');
    var previous = select.value;
    select.replaceChildren(...units.map(function (unit) { return new Option(unit, unit, false, unit === previous); }));
    var surface = form.querySelector('[data-sample-surface]');
    surface.disabled = !['dustWipe', 'surfaceWipe'].includes(type.value);
    if (surface.disabled) surface.value = '';
});

// A programmatically focused heading follows the last input method, including when it was already focused.
document.addEventListener('pointerdown', () => document.body.classList.remove('merit-keyboard-mode'), true);
document.addEventListener('keydown', event => {
    if (!event.metaKey && !event.ctrlKey && !event.altKey) document.body.classList.add('merit-keyboard-mode');
}, true);

function resetUploadForm(form) {
    if (!form) return;
    delete form.dataset.submitting; form.removeAttribute('aria-busy');
    form.querySelectorAll('[data-upload-disabled]').forEach(button => { button.disabled = false; delete button.dataset.uploadDisabled; });
    form.querySelector('[data-upload-status]')?.remove();
}
window.addEventListener('pageshow', () => document.querySelectorAll('form[data-upload-busy]').forEach(resetUploadForm));
document.addEventListener('htmx:after:request', event => resetUploadForm(event.detail?.ctx?.sourceElement?.closest('form[data-upload-busy]')));

// Plain POST forms use the same explicit confirmation pattern as htmx actions.
document.addEventListener('submit', event => {
    const message = event.target.dataset.confirm;
    if (message && !window.confirm(message)) event.preventDefault();
});

// Recovery codes stay in this document; copying and printing require an explicit user action.
document.addEventListener('click', async event => {
    if (event.target.closest('[data-print-recovery]')) window.print();
    if (!event.target.closest('[data-copy-recovery]')) return;
    const status = document.querySelector('[data-recovery-status]');
    try {
        const codes = [...document.querySelectorAll('[data-recovery-code]')].map(el => el.textContent.trim()).join('\n');
        await navigator.clipboard.writeText(codes);
        status.textContent = 'Recovery codes copied. Store them somewhere safe.';
    } catch { status.textContent = 'Copy is unavailable. Select and copy the codes manually, or print them.'; }
});
document.addEventListener('change', event => {
    if (event.target.id === 'saved-codes') document.querySelector('[data-recovery-submit]').disabled = !event.target.checked;
});
