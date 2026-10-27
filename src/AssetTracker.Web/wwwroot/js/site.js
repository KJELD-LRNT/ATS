// Kept in an external file because the Content-Security-Policy (script-src 'self') blocks inline scripts and inline
// event handlers such as onsubmit="...". Everything here is progressive enhancement: the pages work without it.

// Confirmation prompts for forms marked data-confirm="...".
document.addEventListener("submit", function (e) {
    var form = e.target;
    if (form instanceof HTMLFormElement && form.dataset.confirm && !window.confirm(form.dataset.confirm)) {
        e.preventDefault();
    }
});

// Quantity steppers: <div data-stepper><button data-step="-1">…<input type="number">…<button data-step="1"></div>.
document.addEventListener("click", function (e) {
    var button = e.target instanceof Element ? e.target.closest("[data-step]") : null;
    if (!button) return;
    var input = button.closest("[data-stepper]") && button.closest("[data-stepper]").querySelector("input[type=number]");
    if (!input || input.disabled) return;
    var step = Number(button.getAttribute("data-step"));
    var min = input.min === "" ? -Infinity : Number(input.min);
    var max = input.max === "" ? Infinity : Number(input.max);
    var value = Number(input.value || 0) + step;
    input.value = String(Math.min(max, Math.max(min, value)));
    input.dispatchEvent(new Event("change", { bubbles: true }));
});


// Forms marked data-autosubmit reload (GET) when one of their selects changes, e.g. choosing the type of a new asset
// shows that type's fields. The button inside is the fallback without JavaScript and is hidden here.
document.querySelectorAll("form[data-autosubmit]").forEach(function (form) {
    var button = form.querySelector("[data-autosubmit-button]");
    if (button) (button.closest(".form-actions") || button).hidden = true;
    form.addEventListener("change", function (e) {
        if (e.target instanceof HTMLSelectElement) form.submit();
    });
});

// Vendor and model dropdowns of an asset form: choosing a vendor narrows the models to that vendor's, choosing a model
// selects its vendor. Without JavaScript every model of the type is listed ("Vendor Model") and the server checks the pair.
(function () {
    var vendor = document.querySelector("[data-vendor-select]");
    var model = document.querySelector("[data-model-select]");
    if (!vendor || !model) return;

    function narrow() {
        var chosen = vendor.value;
        Array.prototype.forEach.call(model.options, function (o) {
            var of = o.getAttribute("data-vendor");
            var show = !of || !chosen || of === chosen;
            o.hidden = !show;
            o.disabled = !show;
        });
        if (model.selectedOptions[0] && model.selectedOptions[0].disabled) model.value = "";
    }

    vendor.addEventListener("change", narrow);
    model.addEventListener("change", function () {
        var option = model.selectedOptions[0];
        var of = option && option.getAttribute("data-vendor");
        if (of) {
            vendor.value = of;
            narrow();
        }
    });
    narrow();
})();

// ---------------------------------------------------------------------------------------------------------------
// Typeahead comboboxes on the checkout page: "What is being lent?" (items) and "Who receives it?" (people).
// createCombo wires one ARIA combobox: while typing (or on focus, when `browse` is set) it fetches JSON from the page's
// handler, renders the rows, and calls `choose` for the picked one. Arrows move (rows that cannot be picked are skipped),
// Enter picks, Escape closes. Both finders only enhance a plain GET form: without JavaScript the typed text is sent with
// "Find" and searched on the server.
function createCombo(o) {
    var input = o.input, list = o.list;
    var timer = null, controller = null, rows = [], active = -1;

    function make(tag, className, text) {
        var node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined) node.textContent = text;
        return node;
    }

    function close() {
        list.hidden = true;
        input.setAttribute("aria-expanded", "false");
        input.removeAttribute("aria-activedescendant");
        active = -1;
    }

    function options() { return Array.prototype.slice.call(list.querySelectorAll("[role=option]")); }

    function setActive(index) {
        var opts = options();
        var pickable = opts.map(function (el, i) { return el.getAttribute("aria-disabled") === "true" ? -1 : i; })
            .filter(function (i) { return i >= 0; });
        if (pickable.length === 0) return;
        var at = pickable.indexOf(index);
        active = at >= 0 ? index : pickable[index < 0 ? pickable.length - 1 : 0];
        opts.forEach(function (el, i) { el.setAttribute("aria-selected", i === active ? "true" : "false"); });
        input.setAttribute("aria-activedescendant", opts[active].id);
        opts[active].scrollIntoView({ block: "nearest" });
    }

    function step(delta) {
        var opts = options();
        var i = active;
        for (var n = 0; n < opts.length; n++) {
            i = (i + delta + opts.length) % opts.length;
            if (opts[i].getAttribute("aria-disabled") !== "true") { setActive(i); return; }
        }
    }

    function pick(row) {
        close();
        o.choose(row);
    }

    function render() {
        list.textContent = "";
        active = -1;
        if (rows.length === 0) {
            var none = make("li", "combo-empty", o.emptyText);
            none.setAttribute("role", "presentation");
            list.appendChild(none);
        }
        rows.forEach(function (row, i) {
            var pickable = o.pickable ? o.pickable(row) : true;
            var option = make("li", "combo-option" + (pickable ? "" : " combo-option--disabled"));
            option.id = o.idPrefix + i;
            option.setAttribute("role", "option");
            option.setAttribute("aria-selected", "false");
            if (!pickable) option.setAttribute("aria-disabled", "true");
            o.render(row, option);
            // mousedown, not click: the input would lose focus (and close the list) before a click lands.
            option.addEventListener("mousedown", function (e) { e.preventDefault(); if (pickable) pick(row); });
            list.appendChild(option);
        });
        list.hidden = false;
        input.setAttribute("aria-expanded", "true");
    }

    function search() {
        var q = input.value.trim();
        if (q.length < o.min && !(o.browse && q.length === 0)) { close(); return; }
        if (controller) controller.abort();
        controller = typeof AbortController === "function" ? new AbortController() : null;
        fetch(o.url(q), {
            headers: { Accept: "application/json" },
            credentials: "same-origin",
            signal: controller ? controller.signal : undefined
        }).then(function (res) {
            if (!res.ok) throw new Error("search failed");
            return res.json();
        }).then(function (found) {
            // Ignore an answer to text that has changed meanwhile.
            if (input.value.trim() !== q) return;
            rows = found;
            render();
        }).catch(function (err) {
            if (err && err.name === "AbortError") return;
            close(); // the Find button still searches the typed text
        });
    }

    input.addEventListener("input", function () {
        if (o.edited) o.edited();
        clearTimeout(timer);
        timer = setTimeout(search, 200);
    });

    if (o.browse) input.addEventListener("focus", function () { if (input.value.trim() === "") search(); });

    input.addEventListener("keydown", function (e) {
        if (e.key === "ArrowDown" || e.key === "ArrowUp") {
            if (list.hidden) { if (rows.length) render(); else { search(); return; } }
            e.preventDefault();
            if (active < 0) setActive(e.key === "ArrowDown" ? 0 : -1); else step(e.key === "ArrowDown" ? 1 : -1);
        } else if (e.key === "Enter" && !list.hidden && active >= 0) {
            e.preventDefault();
            pick(rows[active]);
        } else if (e.key === "Escape" && !list.hidden) {
            e.preventDefault();
            close();
        }
    });

    input.addEventListener("blur", close);

    return { make: make, refresh: function () { clearTimeout(timer); search(); }, close: close };
}

// People typeahead ("Who receives it?"). /Loans/Checkout?handler=People returns matching people with their site; picking
// one fills the hidden holderId so the confirm step has that person selected, and narrows the item finder to their site.
(function () {
    var root = document.querySelector("[data-people-lookup]");
    if (!root) return;
    var input = root.querySelector("input[role=combobox]");
    var idField = root.querySelector("[data-people-id]");
    var selected = root.querySelector("[data-people-selected]");
    var url = root.getAttribute("data-url");

    function initials(name) {
        var parts = name.trim().split(/\s+/).filter(Boolean);
        return ((parts[0] || "?")[0] + (parts.length > 1 ? parts[parts.length - 1][0] : "")).toUpperCase();
    }

    var combo = createCombo({
        input: input, list: root.querySelector("[role=listbox]"), idPrefix: "who-option-", min: Number(root.getAttribute("data-min") || 2),
        emptyText: "No matching people who can receive a loan.",
        url: function (q) { return url + "&q=" + encodeURIComponent(q); },
        render: function (p, option) {
            var text = combo.make("span", "picker-text");
            var name = combo.make("span", "combo-name");
            name.appendChild(document.createTextNode(p.name + " "));
            name.appendChild(siteBadge(p));
            text.appendChild(name);
            text.appendChild(combo.make("span", "combo-meta", [p.username, p.email, p.siteName, p.jobTitle].filter(Boolean).join(" · ")));
            option.appendChild(combo.make("span", "avatar", initials(p.name)));
            option.appendChild(text);
        },
        choose: function (p) {
            input.value = p.name;
            idField.value = String(p.id);
            selected.textContent = "";
            selected.appendChild(combo.make("span", null, "Selected:"));
            selected.appendChild(combo.make("strong", null, p.name));
            selected.appendChild(siteBadge(p));
            if (p.siteName) selected.appendChild(combo.make("span", null, p.siteName));
            selected.hidden = false;
            // Most loans stay within a site: show that site's items, unless the site was chosen by hand.
            document.dispatchEvent(new CustomEvent("checkout:person", { detail: p }));
        },
        edited: function () {
            if (!idField.value) return;
            idField.value = "";
            selected.hidden = true;
            selected.textContent = "";
        }
    });

    function siteBadge(p) {
        var badge = document.createElement("span");
        badge.className = p.siteKey ? "badge badge--info" : "badge badge--muted";
        badge.textContent = p.siteKey || "No site";
        return badge;
    }
})();

// Item finder ("What is being lent?"). /Loans/Checkout?handler=Items returns tagged assets and quantity pools matching the
// text, type and site (an empty box lists what is available). Items that cannot be lent are shown with their state and
// cannot be picked. Picking fills the hidden assetId/poolId (enabled only then) and moves on to the person field.
(function () {
    var root = document.querySelector("[data-item-lookup]");
    if (!root) return;
    var input = root.querySelector("input[role=combobox]");
    var assetField = root.querySelector("[data-item-asset]");
    var poolField = root.querySelector("[data-item-pool]");
    var typeSelect = document.querySelector("[data-item-type]");
    var siteSelect = document.querySelector("[data-item-site]");
    var url = root.getAttribute("data-url");
    var siteChosenByHand = !!(siteSelect && siteSelect.value);

    // A picked item (after "Find") keeps its id until the text is edited.
    [assetField, poolField].forEach(function (f) { f.disabled = !f.value; });

    function setPicked(kind, id) {
        assetField.value = kind === "asset" ? String(id) : "";
        poolField.value = kind === "pool" ? String(id) : "";
        assetField.disabled = !assetField.value;
        poolField.disabled = !poolField.value;
    }

    function badge(text, kind) {
        var b = document.createElement("span");
        b.className = "badge" + (kind ? " badge--" + kind : "");
        b.textContent = text;
        return b;
    }

    var combo = createCombo({
        input: input, list: root.querySelector("[role=listbox]"), idPrefix: "item-option-", min: Number(root.getAttribute("data-min") || 2),
        browse: true, emptyText: "Nothing found. Try another word or clear the type and site filters.",
        url: function (q) {
            var u = url + "&q=" + encodeURIComponent(q);
            if (typeSelect && typeSelect.value) u += "&type=" + encodeURIComponent(typeSelect.value);
            if (siteSelect && siteSelect.value) u += "&site=" + encodeURIComponent(siteSelect.value);
            return u;
        },
        pickable: function (i) { return i.canCheckOut; },
        render: function (i, option) {
            var text = combo.make("span", "picker-text");
            var name = combo.make("span", "combo-name");
            name.appendChild(combo.make("span", "mono", i.key));
            name.appendChild(document.createTextNode(" "));
            name.appendChild(badge(i.siteKey, "info"));
            name.appendChild(document.createTextNode(" "));
            var status = i.kind === "pool" ? i.status : (i.canCheckOut ? "Available" : i.status + (i.holder ? " to " + i.holder : ""));
            name.appendChild(badge(status, i.canCheckOut ? null : "muted"));
            text.appendChild(name);
            text.appendChild(combo.make("span", "combo-meta",
                [i.title, i.type, i.description, i.kind === "pool" ? "quantity item" : null, i.siteName].filter(Boolean).join(" · ")));
            option.appendChild(text);
        },
        choose: function (i) {
            input.value = i.key;
            setPicked(i.kind, i.id);
            var person = document.getElementById("f-who");
            if (person && !person.value) person.focus();
        },
        edited: function () { setPicked(null, null); }
    });

    // Filters narrow the suggestions straight away.
    if (typeSelect) typeSelect.addEventListener("change", function () { input.focus(); combo.refresh(); });
    if (siteSelect) siteSelect.addEventListener("change", function () { siteChosenByHand = true; input.focus(); combo.refresh(); });

    document.addEventListener("checkout:person", function (e) {
        var site = e.detail && e.detail.siteId;
        if (!siteSelect || siteChosenByHand || !site) return;
        var value = String(site);
        var known = Array.prototype.some.call(siteSelect.options, function (o) { return o.value === value; });
        if (known) siteSelect.value = value;
    });
})();
