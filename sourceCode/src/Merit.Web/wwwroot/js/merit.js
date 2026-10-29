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

    // ------------------------------------------------------------------------------------------ phone menu
    // Below 40em the menu and the user block fold behind the Menu button (issue #47); Escape closes it.
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
    const summary = document.getElementById("error-summary");
    if (summary) summary.focus();

    // ------------------------------------------------------------------------------------------ antiforgery
    // Both clients send the token in X-XSRF-TOKEN, read from the XSRF-TOKEN cookie.
    document.addEventListener("htmx:config:request", evt => {
        const token = cookie("XSRF-TOKEN");
        if (token && evt.detail && evt.detail.ctx && evt.detail.ctx.request) {
            evt.detail.ctx.request.headers["X-XSRF-TOKEN"] = token;
        }
    });

    // ------------------------------------------------------------------------------------------ busy state
    document.addEventListener("htmx:before:request", evt => {
        const target = evt.detail && evt.detail.ctx && evt.detail.ctx.target;
        if (target && target.setAttribute) target.setAttribute("aria-busy", "true");
    });

    document.addEventListener("htmx:finally:request", evt => {
        const target = evt.detail && evt.detail.ctx && evt.detail.ctx.target;
        if (target && target.removeAttribute) target.removeAttribute("aria-busy");
    });

    // A request that fails for lack of a connection shows a USWDS alert; nothing is queued in the browser.
    document.addEventListener("htmx:error", evt => {
        const status = evt.detail && evt.detail.ctx && evt.detail.ctx.response && evt.detail.ctx.response.status;
        if (status) return; // the server answered; its alert partial is already swapped in
        const region = document.getElementById("alert-region");
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

    // ------------------------------------------------------------------------------------------ after a swap
    document.addEventListener("htmx:after:swap", evt => {
        const ctx = evt.detail && evt.detail.ctx;
        const target = ctx && ctx.target;

        // An error message in a form gets focus so a screen reader hears it: the error summary first (issue #46). A swap
        // chosen by hx-status (a 422 re-rendering its form) carries no target, and an outerHTML swap leaves the target
        // detached, so the new content is looked up in the document.
        if (ctx && ctx.response && ctx.response.status >= 400) {
            const scope = target && target.isConnected ? target : document;
            const error = document.getElementById("error-summary") || scope.querySelector(".usa-error-message:not(:empty), .usa-alert--error");
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
            const dialog = document.getElementById("dialog");
            if (dialog && target.childElementCount > 0 && !dialog.open) dialog.showModal();
            const first = target.querySelector("input:not([type=hidden]), select, textarea, button");
            if (first) first.focus();
        }
    });

    // Server-sent HX-Trigger events.
    document.addEventListener("merit:announce", evt => announce(evt.detail && (evt.detail.value || evt.detail)));

    // ------------------------------------------------------------------------------------------ dialog
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
            if (t) t.innerHTML = "";
        }
    });

    document.addEventListener("close", evt => {
        if (evt.target && evt.target.id === "dialog") {
            const body = document.getElementById("dialog-body");
            if (body) body.innerHTML = "";
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
    function options(listbox) {
        return Array.from(listbox.querySelectorAll("[role=option]"));
    }

    function choose(option) {
        const listbox = option.closest("[role=listbox]");
        const input = document.getElementById(listbox.getAttribute("data-for"));
        const hidden = document.getElementById(input.getAttribute("data-hidden"));
        hidden.value = option.getAttribute("data-value");
        input.value = option.getAttribute("data-label");
        input.setAttribute("aria-expanded", "false");
        listbox.innerHTML = "";
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
            listbox.innerHTML = "";
            input.setAttribute("aria-expanded", "false");
        }
    });

    document.addEventListener("input", evt => {
        const input = evt.target.closest("[role=combobox]");
        if (!input) return;
        const hidden = document.getElementById(input.getAttribute("data-hidden"));
        if (hidden) hidden.value = "";
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
            if (input) input.setAttribute("aria-expanded", count > 0 ? "true" : "false");
            announce(count === 0 ? "No matches" : count + " matches. Use the arrow keys to choose.");
        }
    });

    // ------------------------------------------------------------------------------------------ small conveniences
    // A select with data-autosubmit submits its form (used by plain GET filter forms; htmx forms trigger on change).
    document.addEventListener("change", evt => {
        const el = evt.target.closest("[data-autosubmit]");
        if (el && el.form && !el.form.hasAttribute("hx-get") && !el.form.hasAttribute("hx-post")) el.form.requestSubmit();
    });

    function clearCombobox(box) {
        box.value = "";
        box.setAttribute("aria-expanded", "false");
        box.removeAttribute("aria-activedescendant");
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
                if (box.id === "OrganizationSite-text") {
                    box.setAttribute("hx-get", "/lookup/sites?type=" + (value === "school" ? "school" : "childCare"));
                }
                if (!active || (changedField && changedField.name === name && box.id === "OrganizationSite-text")) {
                    clearCombobox(box);
                }
            });
        });
    }

    document.addEventListener("change", evt => syncToggles(document, evt.target));
    document.addEventListener("htmx:after:settle", evt => syncToggles(evt.detail && evt.detail.ctx && evt.detail.ctx.target));
    document.addEventListener("DOMContentLoaded", () => syncToggles());
})();
