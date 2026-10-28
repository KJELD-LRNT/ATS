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
// "Find" and searched on the server. Options used by the floor plan's desk view: `group(row)` puts a heading above each
// run of rows with the same group, `autoActive` makes the first row that can be picked the active one as soon as text
// is typed (so Enter picks it, also when pressed before the answer is in), `emptyText` may be a function of the text and
// `browseOnClick` opens the browse list on a click or an arrow key instead of on focus.
function createCombo(o) {
    var input = o.input, list = o.list;
    var timer = null, controller = null, rows = [], active = -1, pending = false, enterPending = false;

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
            var none = make("li", "combo-empty", typeof o.emptyText === "function" ? o.emptyText(input.value.trim()) : o.emptyText);
            none.setAttribute("role", "presentation");
            list.appendChild(none);
        }
        var lastGroup = null;
        rows.forEach(function (row, i) {
            if (o.group) {
                var g = o.group(row);
                if (g && g !== lastGroup) {
                    var heading = make("li", "combo-group", g);
                    heading.setAttribute("role", "presentation");
                    list.appendChild(heading);
                }
                lastGroup = g;
            }
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
        if (o.autoActive && input.value.trim() !== "") setActive(0);
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
            pending = false;
            rows = found;
            render();
            if (enterPending) {
                enterPending = false;
                if (active >= 0) pick(rows[active]);
            }
        }).catch(function (err) {
            if (err && err.name === "AbortError") return;
            enterPending = false;
            close(); // the Find button still searches the typed text
        });
    }

    input.addEventListener("input", function () {
        pending = true;
        if (o.edited) o.edited();
        clearTimeout(timer);
        timer = setTimeout(search, 200);
    });

    // Browsing (the list with no text) opens on focus, or with `browseOnClick` only on a click or the arrow keys, so a
    // box that gets the focus by itself does not cover what is below it.
    if (o.browse && !o.browseOnClick) input.addEventListener("focus", function () { if (input.value.trim() === "") search(); });
    if (o.browse && o.browseOnClick) input.addEventListener("click", function () { if (input.value.trim() === "" && list.hidden) search(); });

    input.addEventListener("keydown", function (e) {
        if (e.key === "ArrowDown" || e.key === "ArrowUp") {
            if (list.hidden) { if (rows.length) render(); else { search(); return; } }
            e.preventDefault();
            if (active < 0) setActive(e.key === "ArrowDown" ? 0 : -1); else step(e.key === "ArrowDown" ? 1 : -1);
        } else if (e.key === "Enter" && o.autoActive && input.value.trim() !== "" && (pending || list.hidden)) {
            // Typed and pressed Enter before the suggestions were in: pick the first one when they arrive.
            e.preventDefault();
            enterPending = true;
            clearTimeout(timer);
            search();
        } else if (e.key === "Enter" && !list.hidden && active >= 0) {
            e.preventDefault();
            pick(rows[active]);
        } else if (e.key === "Escape" && !list.hidden) {
            e.preventDefault();
            close();
        }
    });

    input.addEventListener("blur", close);

    return { make: make, refresh: function () { clearTimeout(timer); search(); }, close: close, input: input, reset: function () { clearTimeout(timer); rows = []; pending = false; enterPending = false; close(); } };
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

// Overview "Find, check out and return" (IT). /?handler=Find returns assets, pools and people; each result links to its
// page and offers the action that fits its state (check out an available item, return a checked-out one). Enter on an
// exact tag goes straight to the return (checked out) or the asset page, so a scanner can be used without the mouse.
// The top bar has the same lookup (IT only), so every [data-find-lookup] gets its own combo.
document.querySelectorAll("[data-find-lookup]").forEach(function (root) {
    var input = root.querySelector("input[role=combobox]");
    var url = root.getAttribute("data-url");
    var site = root.getAttribute("data-site");
    var ID = "123456789";

    function target(attr, id) { return root.getAttribute(attr).replace(ID, String(id)); }

    function findUrl(q) { return url + "&q=" + encodeURIComponent(q) + (site ? "&site=" + encodeURIComponent(site) : ""); }

    function pageOf(row, fast) {
        if (row.kind === "person") return root.getAttribute("data-person-url").replace("__Q__", encodeURIComponent(row.key));
        if (row.kind === "pool") return target("data-pool-url", row.id);
        if (fast && row.loanId) return target("data-return-url", row.loanId);
        return target("data-asset-url", row.id);
    }

    function actionsOf(row) {
        var actions = [];
        if (row.kind === "asset" && row.canCheckOut) actions.push(["Check out", target("data-checkout-asset-url", row.id)]);
        if (row.kind === "asset" && row.loanId) actions.push(["Return", target("data-return-url", row.loanId)]);
        if (row.kind === "pool" && row.canCheckOut) actions.push(["Check out", target("data-checkout-pool-url", row.id)]);
        return actions;
    }

    function badge(text, kind) {
        var b = document.createElement("span");
        b.className = "badge" + (kind ? " badge--" + kind : "");
        b.textContent = text;
        return b;
    }

    var combo = createCombo({
        input: input, list: root.querySelector("[role=listbox]"), idPrefix: root.getAttribute("data-prefix") || "find-option-", min: 2,
        emptyText: "Nothing found.",
        url: findUrl,
        render: function (row, option) {
            var text = combo.make("span", "picker-text");
            var name = combo.make("span", "combo-name");
            var meta;
            if (row.kind === "person") {
                name.appendChild(document.createTextNode(row.name + " "));
                name.appendChild(badge(row.siteKey || "No site", row.siteKey ? "info" : "muted"));
                if (row.active === false) name.appendChild(badge("Left", "error"));
                meta = [row.key, row.email, row.jobTitle, "person: their loans"];
            } else {
                name.appendChild(combo.make("span", "mono", row.key));
                name.appendChild(document.createTextNode(" "));
                name.appendChild(badge(row.siteKey, "info"));
                name.appendChild(document.createTextNode(" "));
                var status = row.kind === "pool" ? row.status : (row.canCheckOut ? "Available" : row.status + (row.holder ? " to " + row.holder : ""));
                name.appendChild(badge(status, row.canCheckOut ? null : "muted"));
                meta = [row.title, row.type, row.description, row.kind === "pool" ? "quantity item" : null, row.siteName];
            }
            text.appendChild(name);
            text.appendChild(combo.make("span", "combo-meta", meta.filter(Boolean).join(" · ")));
            option.appendChild(text);

            var actions = actionsOf(row);
            if (actions.length) {
                var box = combo.make("span", "combo-actions");
                actions.forEach(function (a) {
                    var link = document.createElement("a");
                    link.href = a[1];
                    link.textContent = a[0];
                    // Keep focus in the input (the list would close on blur) but let the click go through.
                    link.addEventListener("mousedown", function (e) { e.preventDefault(); e.stopPropagation(); });
                    box.appendChild(link);
                });
                option.appendChild(box);
            }
        },
        choose: function (row) { window.location.href = pageOf(row, false); }
    });

    input.addEventListener("keydown", function (e) {
        if (e.key !== "Enter" || input.getAttribute("aria-activedescendant")) return;
        var text = input.value.trim();
        if (text.length < 2) return; // plain form submit
        e.preventDefault();
        fetch(findUrl(text), { headers: { Accept: "application/json" }, credentials: "same-origin" })
            .then(function (res) { if (!res.ok) throw new Error("find failed"); return res.json(); })
            .then(function (found) {
                var things = found.filter(function (r) { return r.kind !== "person"; });
                var exact = things.filter(function (r) { return r.key.toUpperCase() === text.toUpperCase(); })[0];
                var only = found.length === 1 && things.length === 1 ? things[0] : null;
                var row = exact || only;
                if (row) window.location.href = pageOf(row, true);
                else input.form.submit();
            })
            .catch(function () { input.form.submit(); });
    });
});

// Narrow screens: the menu folds away behind a "Menu" button (open on desktop; without JavaScript it simply stays open).
(function () {
    var toggle = document.querySelector("[data-menu-toggle]");
    var menu = document.getElementById("sidebar-menu");
    if (!toggle || !menu || !window.matchMedia) return;
    var narrow = window.matchMedia("(max-width: 820px)");
    function set(open) {
        menu.hidden = !open;
        toggle.setAttribute("aria-expanded", open ? "true" : "false");
    }
    toggle.hidden = false;
    set(!narrow.matches);
    toggle.addEventListener("click", function () { set(menu.hidden); });
    var onChange = function () { set(!narrow.matches); };
    if (narrow.addEventListener) narrow.addEventListener("change", onChange); else narrow.addListener(onChange);
})();

// ---------------------------------------------------------------------------------------------------------------
// Workplace: find a seat, room, zone, person or desktop (any floor). Picking a result highlights it when it is on the floor that is
// open, otherwise opens its floor with the seats highlighted (?focus=ws:ID | room:ID | zone:ID).
(function () {
    Array.prototype.forEach.call(document.querySelectorAll("[data-workplace-find]"), function (root) {
        var input = root.querySelector("input[role=combobox]");
        var url = root.getAttribute("data-url");
        var floorUrl = root.getAttribute("data-floor-url");
        var KINDS = { room: "Room", zone: "Zone", team: "Zone", person: "Person", asset: "Desktop / screen" };

        var combo = createCombo({
            input: input, list: root.querySelector("[role=listbox]"), idPrefix: input.id + "-option-", min: 2,
            emptyText: "Nothing found.",
            url: function (q) { return url + "&q=" + encodeURIComponent(q); },
            render: function (row, option) {
                var text = combo.make("span", "picker-text");
                var name = combo.make("span", "combo-name");
                name.appendChild(document.createTextNode(row.title + " "));
                var kind = combo.make("span", "badge badge--info", KINDS[row.kind] || row.kind);
                name.appendChild(kind);
                text.appendChild(name);
                text.appendChild(combo.make("span", "combo-meta", [row.siteKey, row.floorName, row.subtitle].filter(Boolean).join(" · ")));
                option.appendChild(text);
            },
            choose: function (row) {
                var map = document.querySelector("[data-floor-map]");
                if (map && Number(map.getAttribute("data-floor-id")) === row.floorId) {
                    input.value = row.title;
                    map.dispatchEvent(new CustomEvent("workplace:focus", { detail: row.focus }));
                } else {
                    window.location.href = floorUrl.replace("123456789", String(row.floorId)) + "?focus=" + encodeURIComponent(row.focus);
                }
            }
        });
    });
})();

// ---------------------------------------------------------------------------------------------------------------
// Workplace label engine (floor page and print page). The server writes each label (the person's name and the desktop
// tag, beside the seat away from its table) as a fallback; this fits them to the space there is, so they never overlap:
//  1. once per drawing, every labelled desk gets a slot in drawing units on its outside, as wide as the gap to the
//     neighbouring desks on that side (all of the gap when the neighbour has no label, half of it when it has one);
//  2. when the zoom changes enough the text is fitted again: shrunk down to `minPx` (10 px on screen, 7 pt on paper);
//  3. shortened: "Gabi Demoson" → "Gabi D." → "GD", "AMS-DT-001" → "DT-001" (the header names the site);
//  4. labels in one row are spread within their slots, and a last pass hides what still touches something: the tag
//     first, then the name becomes a numbered callout (never hidden) listed in a key;
//  5. zone names step aside (upwards) when a label is where they are.
// Labels are drawn in screen pixels: each label group is scaled so one of its units is one pixel. Only SVG attributes
// and element.style properties are set (the CSP forbids inline style attributes).
var WP_LABEL = { name: 12, tag: 11, halo: 3, gap: 4 };

function wpNameVariants(name) {
    var full = String(name || "").trim().replace(/\s+/g, " ");
    if (!full) return [];
    var parts = full.split(" ");
    var out = [full];
    if (parts.length >= 2) {
        var last = parts[parts.length - 1];
        out.push(parts[0] + " " + last.charAt(0).toUpperCase() + ".");
        out.push((parts[0].charAt(0) + last.charAt(0)).toUpperCase());
    } else if (full.length > 3) {
        out.push(full.slice(0, 2));
    }
    return out.filter(function (v, i) { return out.indexOf(v) === i; });
}

function wpTagVariants(tag, prefix) {
    var full = String(tag || "").trim();
    if (!full) return [];
    var out = [full];
    if (prefix && full.toUpperCase().indexOf(prefix.toUpperCase()) === 0 && full.length > prefix.length) out.push(full.slice(prefix.length));
    return out;
}

function createLabelEngine(svg, options) {
    var o = options || {};
    var NS = "http://www.w3.org/2000/svg";
    var ctx = document.createElement("canvas").getContext("2d");
    var widths = {};
    var items = [], obstacles = [], zones = [], spacing = 1, ready = false, lastK = 0, lastMin = 0, result = { callouts: 0, key: [], labels: 0, hiddenTags: 0 };

    function all(sel) { return Array.prototype.slice.call(svg.querySelectorAll(sel)); }
    function fontOf(el) { var cs = getComputedStyle(el); return cs.fontStyle + " " + cs.fontWeight + " {px} " + cs.fontFamily; }
    // Width of `text` at 100 px, measured once per text and font (text is never re-measured while zooming).
    function measure(text, font) {
        var key = font + "|" + text;
        if (widths[key] === undefined) { ctx.font = font.replace("{px}", "100px"); widths[key] = ctx.measureText(text).width; }
        return widths[key];
    }
    // Ascent and descent of a font as a share of its size (the box the browser draws the text in).
    var fontMetrics = {};
    function metricsOf(font) {
        if (!fontMetrics[font]) {
            ctx.font = font.replace("{px}", "100px");
            var m = ctx.measureText("Hg");
            var asc = m.fontBoundingBoxAscent, desc = m.fontBoundingBoxDescent;
            fontMetrics[font] = asc > 0 && desc >= 0 ? { asc: asc / 100, desc: desc / 100 } : { asc: 0.97, desc: 0.26 };
        }
        return fontMetrics[font];
    }
    function overlaps(a, b) { return a.x0 < b.x1 && b.x0 < a.x1 && a.y0 < b.y1 && b.y0 < a.y1; }
    function scaled(b, k) { return { x0: b.x * k, y0: b.y * k, x1: (b.x + b.w) * k, y1: (b.y + b.h) * k, seat: b.seat }; }

    function prepare() {
        ready = true;
        var seats = all("[data-ws]").map(function (el) {
            var b = userBox(svg, el);
            return { el: el, id: el.getAttribute("data-ws"), x: b.x + b.w / 2, y: b.y + b.h / 2, r: Math.max(b.w, b.h) / 2, box: b, desk: el.hasAttribute("data-desk"), labelled: false };
        });
        var byId = {};
        seats.forEach(function (st) { byId[st.id] = st; });
        // Typical distance between neighbouring desks.
        var desks = seats.filter(function (st) { return st.desk; }).slice(0, 600);
        var near = desks.map(function (a) {
            var best = Infinity;
            desks.forEach(function (b) { if (a !== b) best = Math.min(best, Math.hypot(a.x - b.x, a.y - b.y)); });
            return best;
        }).filter(isFinite).sort(function (a, b) { return a - b; });
        spacing = near.length ? near[Math.floor(near.length / 2)] : 20;

        var nameFont = null, tagFont = null;
        items = all(".fp-label").map(function (g) {
            var seat = byId[g.getAttribute("data-label-for")];
            var m = /fp-label-(up|down|left|right)/.exec(g.getAttribute("class") || "");
            if (!seat || !m) return null;
            var nameEl = g.querySelector(".fp-label-name"), tagEl = g.querySelector(".fp-label-tag");
            if (nameEl && !nameFont) nameFont = fontOf(nameEl);
            if (tagEl && !tagFont) tagFont = fontOf(tagEl);
            seat.labelled = true;
            return {
                g: g, seat: seat, side: m[1], ax: parseFloat(g.getAttribute("data-ax")), ay: parseFloat(g.getAttribute("data-ay")),
                nameEl: nameEl, tagEl: tagEl, person: seat.el.getAttribute("data-person"), desktop: seat.el.getAttribute("data-desktop"),
                names: nameEl ? wpNameVariants(seat.el.getAttribute("data-person") || nameEl.textContent) : [],
                tags: tagEl ? wpTagVariants(seat.el.getAttribute("data-desktop") || tagEl.textContent, o.prefix) : []
            };
        }).filter(Boolean);
        items.forEach(function (it) { it.nameFont = nameFont; it.tagFont = tagFont; });

        // The slot of each label: along its row (up/down labels) or column (left/right), up to the neighbouring desks.
        var cap = spacing * 1.6;
        items.forEach(function (it) {
            var st = it.seat, along = it.side === "up" || it.side === "down", lo = cap, hi = cap;
            seats.forEach(function (other) {
                if (other === st) return;
                var d = along ? other.x - st.x : other.y - st.y;
                var perp = along ? other.y - st.y : other.x - st.x;
                if (Math.abs(perp) > Math.max(st.r, other.r) * 1.5 || d === 0) return;
                var room = other.labelled ? Math.abs(d) / 2 : Math.abs(d) - other.r * 1.3;
                if (d < 0) lo = Math.min(lo, room); else hi = Math.min(hi, room);
            });
            it.lo = Math.max(lo, st.r);
            it.hi = Math.max(hi, st.r);
        });

        // Things a label must not cover: the seats and the drawing's own text (room names and the like).
        obstacles = seats.map(function (st) { var b = st.box; return { x: b.x, y: b.y, w: b.w, h: b.h, seat: st }; });
        all("text").forEach(function (t) {
            if (t.closest(".fp-labels, .zone-layer, defs, symbol, title") || !t.textContent.trim()) return;
            var b = userBox(svg, t);
            if (b.w > 0 && b.h > 0) obstacles.push(b);
        });
        // Zone names (a halo and the coloured text at the same spot) may move up out of the way.
        var byPos = {};
        all(".zone-layer text").forEach(function (t) {
            var key = t.getAttribute("x") + "|" + t.getAttribute("y");
            (byPos[key] = byPos[key] || []).push(t);
        });
        var outlines = all(".zone-outline").map(function (r) { var b = userBox(svg, r); b.zone = r.getAttribute("data-zone"); return b; });
        zones = Object.keys(byPos).map(function (key) {
            var els = byPos[key], b = userBox(svg, els[els.length - 1]);
            var x = parseFloat(els[0].getAttribute("x")), y = parseFloat(els[0].getAttribute("y"));
            var size = parseFloat(els[0].getAttribute("font-size")) || b.h;
            // Below the zone's tables in this column, the other place its name can go.
            var column = outlines.filter(function (o) {
                return o.zone === els[0].getAttribute("data-zone") && (Math.abs(o.x - x) < size * 0.2 || Math.abs(o.x + o.w - x) < size * 0.2);
            });
            var bottom = column.length ? Math.max.apply(null, column.map(function (o) { return o.y + o.h; })) : null;
            return { els: els, y: y, box: b, dy: 0, hidden: false, below: bottom === null ? null : bottom + size * 1.05 - y };
        });
    }

    function line(text, font, size, w) {
        var m = metricsOf(font);
        return { text: text, size: size, w: w * size + WP_LABEL.halo, asc: m.asc * size, h: (m.asc + m.desc) * size };
    }

    // The largest variant that fits `limit` px at a size between `max` and `min` px; null when none does.
    function pick(variants, font, max, min, limit) {
        for (var i = 0; i < variants.length; i++) {
            var w = measure(variants[i], font) / 100;
            var size = Math.min(max, (limit - WP_LABEL.halo) / w);
            if (size >= min) return line(variants[i], font, size, w);
        }
        return null;
    }

    // The box of a label's lines relative to its anchor, in px.
    function boxOf(it, lines) {
        var w = 0, h = 0;
        lines.forEach(function (l) { w = Math.max(w, l.w); h += l.h; });
        var b;
        if (it.side === "up") b = { x0: -w / 2, x1: w / 2, y0: -2 - h, y1: -2 };
        else if (it.side === "down") b = { x0: -w / 2, x1: w / 2, y0: 2, y1: 2 + h };
        else if (it.side === "left") b = { x0: -w, x1: 0, y0: -h / 2, y1: h / 2 };
        else b = { x0: 0, x1: w, y0: -h / 2, y1: h / 2 };
        b.w = w; b.h = h;
        return b;
    }

    function absBox(it, k) {
        // Half the gap on each side, so two labels never end up touching.
        var b = it.rel, g = b.w > 0 ? WP_LABEL.gap / 2 : 0;
        return { x0: it.ax * k + it.shift + b.x0 - g, x1: it.ax * k + it.shift + b.x1 + g, y0: it.ay * k + b.y0, y1: it.ay * k + b.y1 };
    }

    function linesOf(it) { return [it.name, it.tag].filter(Boolean); }

    // The box of a callout number, relative to the label's anchor: a small dark circle right next to the desk on the
    // label's side (over the desktop and screen badges, which are too small to read at such a zoom anyway).
    function calloutBox(it, minPx, k) {
        var r = Math.max(6.5, minPx * 0.66), st = it.seat, gap = st.r * k + r + 1;
        var cx = (st.x - it.ax) * k, cy = (st.y - it.ay) * k;
        if (it.side === "up") cy -= gap; else if (it.side === "down") cy += gap; else if (it.side === "left") cx -= gap; else cx += gap;
        return { x0: cx - r, x1: cx + r, y0: cy - r, y1: cy + r, cx: cx, cy: cy, r: r };
    }

    function fit(k, minPx) {
        var hiddenTags = 0;
        // 1. Text and size per label: the longest form that fits its slot at minPx or more.
        items.forEach(function (it) {
            var along = it.side === "up" || it.side === "down";
            var limit = along ? (it.lo + it.hi) * k - WP_LABEL.gap : spacing * 1.6 * k;
            it.fitName = it.names.length ? pick(it.names, it.nameFont, WP_LABEL.name, minPx, limit) : null;
            it.fitTag = it.tags.length ? pick(it.tags, it.tagFont, WP_LABEL.tag, minPx, limit) : null;
            if (!along && it.fitName && it.fitTag && it.fitName.h + it.fitTag.h > (it.lo + it.hi) * k) it.fitTag = null;
            it.forced = it.names.length ? !it.fitName : !it.fitTag;
            if (!along && it.fitName && it.fitName.h > (it.lo + it.hi) * k) it.forced = true;
        });

        var obs = obstacles.map(function (b) { return scaled(b, k); });
        var order = items.slice().sort(function (a, b) { return (b.names.length - a.names.length) || (a.ay - b.ay) || (a.ax - b.ax); });
        var placed, circles;

        function blocked(it, box, ignore) {
            return obs.some(function (b) { return b.seat !== it.seat && overlaps(box, b); }) ||
                circles.some(function (c) { return c.it !== it && overlaps(box, c.box); }) ||
                placed.some(function (p) { return p !== ignore && overlaps(box, p.box); });
        }
        // Where a label may go: its place after spreading first, then further along its row, but never so far that it
        // no longer covers its own desk.
        function tryPlace(it, ignore) {
            var base = it.shift, shifts = [base];
            if (it.side === "up" || it.side === "down") {
                var range = Math.min(Math.max(it.lo, it.hi) * k, it.rel.w / 2 - 2), step = Math.max(1.5, range / 16);
                for (var d = -range; d <= range; d += step) shifts.push(d);
                shifts.sort(function (x, y) { return Math.abs(x - base) - Math.abs(y - base); });
            }
            for (var i = 0; i < shifts.length; i++) {
                it.shift = shifts[i];
                var box = absBox(it, k);
                if (!blocked(it, box, ignore)) return box;
            }
            it.shift = base;
            return null;
        }
        function circleOf(it) {
            var c = calloutBox(it, minPx, k);
            return { it: it, box: { x0: it.ax * k + c.x0, x1: it.ax * k + c.x1, y0: it.ay * k + c.y0, y1: it.ay * k + c.y1 } };
        }

        // 2. Names first (they matter most). Each label has a list of forms, from its best fit down to the shortest
        // form at minPx. Rows are spread within their slots, then labels are placed one by one from the top left, each
        // in the first form that touches no seat, drawing text, other label or callout. When one cannot be placed, the
        // labels in its way step down a form and the pass runs again; what still cannot be placed becomes a callout.
        items.forEach(function (it) {
            it.level = 0;
            it.cands = [];
            var list = it.names.length ? it.names : it.tags, font = it.names.length ? it.nameFont : it.tagFont;
            var max = it.names.length ? WP_LABEL.name : WP_LABEL.tag, first = it.names.length ? it.fitName : it.fitTag;
            if (!first) return;
            for (var i = list.indexOf(first.text); i < list.length; i++) {
                var w = measure(list[i], font) / 100;
                var best = i === list.indexOf(first.text) ? first.size : Math.min(max, first.size);
                [best, minPx].forEach(function (size, j) {
                    if (j === 1 && size >= best) return;
                    it.cands.push(line(list[i], font, size, w));
                });
            }
        });
        function setForm(it, c) {
            if (it.names.length) { it.name = c; it.tag = null; } else { it.name = null; it.tag = c; }
            it.rel = boxOf(it, linesOf(it));
        }
        for (var round = 0; round < 10; round++) {
            items.forEach(function (it) {
                it.callout = it.forced || !it.cands.length;
                it.shift = 0;
                if (it.callout) { it.name = it.tag = null; it.rel = boxOf(it, []); } else setForm(it, it.cands[Math.min(it.level, it.cands.length - 1)]);
            });
            var rows = {};
            items.forEach(function (it) {
                if (it.callout || (it.side !== "up" && it.side !== "down")) return;
                var key = it.side + ":" + Math.round(it.ay * k / 4);
                (rows[key] = rows[key] || []).push(it);
            });
            Object.keys(rows).forEach(function (key) {
                var row = rows[key].sort(function (x, y) { return x.ax - y.ax; });
                row.forEach(function (it) {
                    var half = it.rel.w / 2;
                    it.min = -it.lo * k + half;
                    it.max = it.hi * k - half;
                    if (it.min > it.max) it.min = it.max = (it.hi - it.lo) * k / 2;
                    it.pos = it.ax * k + Math.max(it.min, Math.min(it.max, 0));
                });
                for (var i = 1; i < row.length; i++) {
                    var need = row[i - 1].pos + row[i - 1].rel.w / 2 + WP_LABEL.gap + row[i].rel.w / 2;
                    if (row[i].pos < need) row[i].pos = Math.min(need, row[i].ax * k + row[i].max);
                }
                for (var j = row.length - 2; j >= 0; j--) {
                    var room = row[j + 1].pos - row[j + 1].rel.w / 2 - WP_LABEL.gap - row[j].rel.w / 2;
                    if (row[j].pos > room) row[j].pos = Math.max(room, row[j].ax * k + row[j].min);
                }
                row.forEach(function (it) { it.shift = it.pos - it.ax * k; });
            });
            placed = [];
            circles = items.filter(function (it) { return it.callout; }).map(circleOf);
            var changed = false;
            order.forEach(function (it) {
                if (it.callout) return;
                for (var c = Math.min(it.level, it.cands.length - 1); c < it.cands.length; c++) {
                    setForm(it, it.cands[c]);
                    var box = tryPlace(it, null);
                    if (box) { it.used = c; placed.push({ it: it, box: box }); return; }
                }
                // In the way: labels placed before that overlap this one's shortest form wherever it could go.
                var probe = { x0: it.ax * k - (it.lo * k), x1: it.ax * k + it.hi * k, y0: it.ay * k + it.rel.y0, y1: it.ay * k + it.rel.y1 };
                var blockers = placed.filter(function (p) { return overlaps(probe, p.box) && p.it.used < p.it.cands.length - 1; });
                if (blockers.length && round < 9) {
                    blockers.forEach(function (p) { p.it.level = p.it.used + 1; });
                    changed = true;
                    setForm(it, it.cands[it.cands.length - 1]);
                    return;
                }
                it.callout = true;
                it.name = it.tag = null;
                it.rel = boxOf(it, []);
                circles.push(circleOf(it));
                if (round < 9) changed = true;
            });
            if (!changed) break;
            // A label that became a callout keeps it in the next pass (as a fixed circle).
            items.forEach(function (it) { if (it.callout) it.forced = true; });
        }

        // 3. Then the tags, where they fit next to the names without touching anything.
        placed.forEach(function (p) {
            var it = p.it;
            if (!it.names.length || !it.fitTag) return;
            var keepShift = it.shift, keepRel = it.rel;
            it.tag = it.fitTag;
            it.rel = boxOf(it, linesOf(it));
            var box = tryPlace(it, p);
            if (box) { p.box = box; return; }
            it.tag = null; it.shift = keepShift; it.rel = keepRel; hiddenTags++;
        });
        items.forEach(function (it) { if (it.callout && it.tags.length) hiddenTags++; });

        // 4. A zone name that is where labels are moves above them or below its tables; else the labels drop their
        // tags if that is enough; else the zone name is left out (its colour and the panel still show the zone).
        var zoneBoxes = [];
        zones.forEach(function (z) {
            z.dy = 0; z.hidden = false;
            var zb = scaled(z.box, k);
            var hits = placed.filter(function (p) { return overlaps(zb, p.box); });
            var circleHits = circles.filter(function (c) { return overlaps(zb, c.box); });
            if (!hits.length && !circleHits.length) { zoneBoxes.push(zb); return; }
            var top = Math.min.apply(null, hits.concat(circleHits).map(function (p) { return p.box.y0; }));
            var cands = [top - WP_LABEL.gap - zb.y1];
            if (z.below !== null) cands.push(z.below * k);
            for (var i = 0; i < cands.length; i++) {
                var dy = cands[i];
                var moved = { x0: zb.x0, x1: zb.x1, y0: zb.y0 + dy, y1: zb.y1 + dy };
                var clear = !placed.some(function (p) { return overlaps(moved, p.box); }) && !circles.some(function (c) { return overlaps(moved, c.box); }) &&
                    !obs.some(function (b) { return overlaps(moved, b); }) && !zoneBoxes.some(function (b) { return overlaps(moved, b); });
                if (clear) { z.dy = dy / k; zoneBoxes.push(moved); return; }
            }
            if (circleHits.length) { z.hidden = true; return; } // callout numbers are never hidden
            var withoutTags = hits.map(function (p) {
                var lines = p.it.name ? [p.it.name] : linesOf(p.it);
                var rel = boxOf(p.it, lines), b = p.box;
                return { x0: b.x0 - p.it.rel.x0 + rel.x0, x1: b.x0 - p.it.rel.x0 + rel.x1, y0: b.y0 - p.it.rel.y0 + rel.y0, y1: b.y0 - p.it.rel.y0 + rel.y1 };
            });
            if (!withoutTags.some(function (b) { return overlaps(zb, b); })) {
                hits.forEach(function (p, j) {
                    if (p.it.name && p.it.tag) { p.it.tag = null; hiddenTags++; p.it.rel = boxOf(p.it, linesOf(p.it)); }
                    p.box = withoutTags[j];
                });
                zoneBoxes.push(zb);
                return;
            }
            z.hidden = true;
        });

        // 5. Callout numbers that touch each other or a label slide along their row (not further than to the next
        // desk). One that still touches another number shares that number (the key lists both desks); a label in the
        // way of a number loses its tag, then joins the number. Nothing overlaps and no number is hidden.
        var groups = [];
        circles.sort(function (a, b) { return (a.it.ay - b.it.ay) || (a.it.ax - b.it.ax); });
        circles.forEach(function (c) {
            var it = c.it, base = calloutBox(it, minPx, k), along = it.side === "up" || it.side === "down";
            var range = along ? Math.min(it.lo, it.hi) * k : 0, step = Math.max(1.5, base.r / 2), best = null;
            var shifts = [0];
            for (var d = step; d < range; d += step) shifts.push(d, -d);
            if (range > 0) shifts.push(range, -range);
            for (var i = 0; i < shifts.length && !best; i++) {
                var box = { x0: c.box.x0 + shifts[i], x1: c.box.x1 + shifts[i], y0: c.box.y0, y1: c.box.y1 };
                var hit = groups.some(function (g) { return overlaps(box, g.box); }) || placed.some(function (p) { return overlaps(box, p.box); }) ||
                    zoneBoxes.some(function (b) { return overlaps(box, b); }) ||
                    obs.some(function (b) { return b.seat !== it.seat && overlaps(box, b); });
                if (!hit) best = { shift: shifts[i], box: box };
            }
            it.calloutShift = best ? best.shift : 0;
            it.calloutLead = true;
            var join = best ? null : groups.filter(function (g) { return overlaps(c.box, g.box); })[0];
            if (join) { join.members.push(it); it.calloutLead = false; return; }
            var group = { box: best ? best.box : c.box, members: [it] };
            groups.push(group);
            if (best) return;
            placed.slice().forEach(function (p) {
                if (!overlaps(group.box, p.box)) return;
                var other = p.it;
                if (other.name && other.tag) {
                    other.tag = null; hiddenTags++;
                    other.rel = boxOf(other, linesOf(other));
                    p.box = absBox(other, k);
                    if (!overlaps(group.box, p.box)) return;
                }
                other.name = other.tag = null;
                other.callout = true;
                other.calloutLead = false;
                other.rel = boxOf(other, []);
                placed.splice(placed.indexOf(p), 1);
                group.members.push(other);
            });
        });

        // 6. Callouts: numbered in reading order.
        groups.forEach(function (g, i) { g.members.forEach(function (it) { it.number = i + 1; }); });
        var members = [];
        groups.forEach(function (g) { members = members.concat(g.members); });
        result = {
            labels: items.length, callouts: members.length, numbers: groups.length, hiddenTags: hiddenTags,
            key: members.map(function (it) { return { n: it.number, person: it.person, desktop: it.desktop }; })
        };
    }

    function el(tag, cls) { var n = document.createElementNS(NS, tag); if (cls) n.setAttribute("class", cls); return n; }

    function render(minPx) {
        items.forEach(function (it) {
            var call = it.g.querySelector(".fp-callout");
            if (it.callout) {
                if (it.nameEl) it.nameEl.style.display = "none";
                if (it.tagEl) it.tagEl.style.display = "none";
                if (!it.calloutLead) { if (call) call.style.display = "none"; it.g.style.display = "none"; return; }
                if (!call) {
                    call = el("g", "fp-callout");
                    call.appendChild(el("circle"));
                    call.appendChild(el("text"));
                    it.g.appendChild(call);
                }
                var cb = calloutBox(it, minPx, lastK), size = minPx, r = cb.r, cy = cb.cy, cx = cb.cx + (it.calloutShift || 0);
                call.style.display = "";
                it.g.style.display = "";
                var c = call.firstChild, t = call.lastChild;
                c.setAttribute("cx", cx.toFixed(1)); c.setAttribute("cy", cy.toFixed(1)); c.setAttribute("r", r.toFixed(1));
                t.textContent = String(it.number);
                t.setAttribute("x", cx.toFixed(1)); t.setAttribute("y", (cy + size * 0.36).toFixed(1));
                t.style.fontSize = size.toFixed(1) + "px";
                return;
            }
            if (call) call.style.display = "none";
            var lines = linesOf(it), top = it.rel.y0;
            [[it.nameEl, it.name], [it.tagEl, it.tag]].forEach(function (pair) {
                var node = pair[0], line = pair[1];
                if (!node) return;
                if (!line) { node.style.display = "none"; return; }
                node.style.display = "";
                node.textContent = line.text;
                node.style.fontSize = line.size.toFixed(2) + "px";
                node.setAttribute("x", it.shift.toFixed(1));
                node.setAttribute("y", (top + line.asc).toFixed(1));
                top += line.h;
            });
            if (!lines.length) it.g.style.display = "none"; else it.g.style.display = "";
        });
        zones.forEach(function (z) {
            z.els.forEach(function (t) { t.setAttribute("y", (z.y + z.dy).toFixed(2)); t.style.display = z.hidden ? "none" : ""; });
        });
    }

    // Keeps the labels at a constant size on screen (every frame while zooming: cheap).
    function place(k) {
        var s = (1 / k).toFixed(5);
        items.forEach(function (it) { it.g.setAttribute("transform", "translate(" + it.ax + " " + it.ay + ") scale(" + s + ")"); });
    }

    return {
        // k: screen px per drawing unit; minPx: the smallest text allowed. Fits again only when the zoom changed enough.
        layout: function (k, minPx, force) {
            if (!k || !isFinite(k)) return result;
            if (!ready) prepare();
            if (force || !lastK || Math.abs(k / lastK - 1) > 0.04 || minPx !== lastMin) {
                lastK = k; lastMin = minPx;
                fit(k, minPx);
                render(minPx);
            }
            place(k);
            return result;
        },
        place: place,
        // After the fonts have loaded: measure the text again.
        remeasure: function () { widths = {}; lastK = 0; },
        lastScale: function () { return lastK; },
        // For checking in the browser's console: the labels and what they must not cover.
        inspect: function () { return { items: items, obstacles: obstacles, zones: zones, spacing: spacing }; },
        result: function () { return result; }
    };
}

// The key to the callout numbers: "1  Gabi Demoson · AMS-DT-001".
function renderCalloutKey(list, key) {
    if (!list) return;
    list.textContent = "";
    key.forEach(function (k) {
        var li = document.createElement("li");
        var n = document.createElement("span");
        n.className = "wp-key-n";
        n.textContent = String(k.n);
        li.appendChild(n);
        li.appendChild(document.createTextNode([k.person, k.desktop].filter(Boolean).join(" · ")));
        list.appendChild(li);
    });
}

// The box of an element in the drawing's own coordinates (its viewBox space), whatever transforms the drawing puts on
// it or its groups: measured on screen and mapped back, so any shape or nesting works.
function userBox(svg, el) {
    var r = el.getBoundingClientRect();
    var m = svg.getScreenCTM().inverse();
    var p = svg.createSVGPoint();
    p.x = r.left; p.y = r.top;
    var a = p.matrixTransform(m);
    p.x = r.right; p.y = r.bottom;
    var b = p.matrixTransform(m);
    return { x: Math.min(a.x, b.x), y: Math.min(a.y, b.y), w: Math.abs(b.x - a.x), h: Math.abs(b.y - a.y) };
}

// Workplace floor map: pan (drag), zoom (wheel, pinch, buttons), select seats (click, Shift-click, table or room click,
// Shift-drag or "Select area"), highlight search results, zones, free desks and seats to check, print what you see or a
// part, and (IT and facility) drag people, desktops and screens onto desks. A drop is posted with fetch and the map is
// swapped for a fresh one without losing the zoom. Without JavaScript the map is a static picture.
(function () {
    var root = document.querySelector("[data-floor-map]");
    if (!root) return;
    var canvas = root.querySelector("[data-canvas]");
    var svg = canvas && canvas.querySelector("svg");
    if (!svg) return;

    var editable = root.getAttribute("data-editable") === "true";
    var wrap = root.querySelector("[data-canvas-wrap]") || canvas;
    var vb = svg.viewBox.baseVal;
    // Fit shows the rooms and desks (the server measures them: data-content), not the paper around them in the drawing.
    function parseBox(text) {
        var v = String(text || "").split(/[\s,]+/).map(parseFloat);
        return v.length === 4 && v.every(isFinite) && v[2] > 0 && v[3] > 0 ? { x: v[0], y: v[1], w: v[2], h: v[3] } : null;
    }
    var content = parseBox(svg.getAttribute("data-content")) || { x: vb.x, y: vb.y, w: vb.width, h: vb.height };
    var fitW = content.w;
    var view = { x: content.x, y: content.y, w: content.w, h: content.h };
    var SHAPES = { rect: 1, circle: 1, ellipse: 1, path: 1, polygon: 1, polyline: 1, g: 1 };
    var declared = !!svg.querySelector("[data-kind=seat]");
    var areaMode = false;
    var printMode = false;
    var namesOn = true;
    var full = false;
    var selected = []; // elements, in the order they were selected
    var labelFrame = 0, labelTimer = 0;
    var prefix = root.getAttribute("data-tag-prefix") || "";
    var engine = createLabelEngine(svg, { prefix: prefix });
    root.wpLabels = function () { return engine; };
    var zoomLevel = root.querySelector("[data-zoom-level]");
    var calloutBox = root.querySelector("[data-callout-box]");

    // The view always has the canvas's shape, so nothing is cut off and there are no empty bands.
    function aspect() {
        var r = canvas.getBoundingClientRect();
        return r.width > 0 && r.height > 0 ? r.height / r.width : content.h / content.w;
    }

    // Labels follow the zoom every frame (cheap); their text is fitted again when the zoom settles or jumps.
    function relabel(force) {
        canvas.classList.toggle("show-labels", namesOn);
        var ctm = svg.getScreenCTM();
        if (!ctm || !ctm.a) return;
        var res = engine.layout(ctm.a, 10, force);
        canvas.setAttribute("data-callouts", String(res.callouts));
        if (calloutBox) {
            calloutBox.hidden = !namesOn || res.callouts === 0;
            renderCalloutKey(calloutBox.querySelector("[data-callout-key]"), res.key);
            var n = calloutBox.querySelector("[data-callout-count]");
            if (n) n.textContent = String(res.callouts);
        }
    }

    function scheduleLabels() {
        if (labelFrame) return;
        labelFrame = requestAnimationFrame(function () {
            labelFrame = 0;
            var ctm = svg.getScreenCTM(), last = engine.lastScale();
            if (ctm && ctm.a && last && Math.abs(ctm.a / last - 1) < 0.25) engine.place(ctm.a);
            else relabel(false);
            clearTimeout(labelTimer);
            labelTimer = setTimeout(function () { relabel(false); }, 160);
        });
    }

    function setView(x, y, w) {
        var nw = Math.max(fitW / 60, Math.min(fitW * 2, w));
        // Zoom limits keep the centre where it was meant to be.
        x += (w - nw) / 2;
        var a = aspect();
        y += (w - nw) * a / 2;
        view = { x: x, y: y, w: nw, h: nw * a };
        svg.setAttribute("viewBox", [view.x, view.y, view.w, view.h].join(" "));
        if (zoomLevel) zoomLevel.textContent = Math.round(fitW / view.w * 100) + "%";
        scheduleLabels();
    }

    function fitView() {
        var a = aspect();
        fitW = Math.max(content.w, content.h / a);
        setView(content.x + content.w / 2 - fitW / 2, content.y + content.h / 2 - fitW * a / 2, fitW);
    }

    function zoomAt(factor, cx, cy) {
        var w = Math.max(fitW / 60, Math.min(fitW * 2, view.w / factor));
        var k = w / view.w;
        setView(cx - (cx - view.x) * k, cy - (cy - view.y) * k, w);
    }

    // The canvas fills the window below the top bar and the page header (at least 480 px); in full screen, all of it.
    // A wide drawing would leave empty bands above and below it at Fit, so outside full screen the canvas is no taller
    // than the drawing's content box at the canvas's width (still at least 480 px).
    function sizeCanvas() {
        var top = wrap.getBoundingClientRect().top + (full ? 0 : window.scrollY);
        var below = full ? 12 + (root.querySelector(".wp-stage > .wp-legend") || { offsetHeight: 0 }).offsetHeight + 10 : 16;
        var h = window.innerHeight - top - below;
        if (full) { wrap.style.height = Math.round(Math.max(240, h)) + "px"; return; }
        var width = wrap.getBoundingClientRect().width;
        var drawing = width > 0 ? width * content.h / content.w + 2 : h;
        wrap.style.height = Math.round(Math.max(480, Math.min(h, drawing))) + "px";
    }

    function userPoint(clientX, clientY) {
        var p = svg.createSVGPoint();
        p.x = clientX;
        p.y = clientY;
        return p.matrixTransform(svg.getScreenCTM().inverse());
    }

    function centre() { return { x: view.x + view.w / 2, y: view.y + view.h / 2 }; }

    function all(selector, scope) { return Array.prototype.slice.call((scope || root).querySelectorAll(selector)); }

    function esc(s) { return (window.CSS && CSS.escape) ? CSS.escape(s) : String(s).replace(/"/g, '\\"'); }

    // ---- messages ----------------------------------------------------------------------------------------------

    var toast = document.createElement("div");
    toast.className = "wp-toast";
    toast.setAttribute("role", "status");
    toast.hidden = true;
    document.body.appendChild(toast);
    var toastTimer = null;

    function say(text, bad) {
        toast.textContent = text;
        toast.classList.toggle("wp-toast--bad", !!bad);
        toast.hidden = false;
        clearTimeout(toastTimer);
        toastTimer = setTimeout(function () { toast.hidden = true; }, bad ? 6000 : 3000);
    }

    // ---- selection -------------------------------------------------------------------------------------------

    function flag(el, on) { el.classList.toggle("is-selected", on); }

    function setSelection(list) {
        selected.forEach(function (el) { flag(el, false); });
        selected = [];
        list.forEach(function (el) { if (selected.indexOf(el) < 0) { selected.push(el); flag(el, true); } });
        updatePanel();
    }

    function toggle(list) {
        var allIn = list.every(function (el) { return selected.indexOf(el) >= 0; });
        list.forEach(function (el) {
            var at = selected.indexOf(el);
            if (allIn) { if (at >= 0) { selected.splice(at, 1); flag(el, false); } }
            else if (at < 0) { selected.push(el); flag(el, true); }
        });
        updatePanel();
    }

    // What a click on `target` selects: a seat, the seats of a table or room, or (plain drawings) a shape to mark.
    function resolve(target) {
        if (!target || !svg.contains(target)) return [];
        var seat = target.closest("[data-ws]");
        if (seat && svg.contains(seat)) return [seat];
        var table = target.closest("[data-kind=table]");
        if (table) { var inTable = table.querySelectorAll("[data-ws]"); if (inTable.length) return Array.prototype.slice.call(inTable); }
        var room = target.closest("[data-kind=room]");
        if (room && room.getAttribute("data-id")) {
            var inRoom = svg.querySelectorAll('[data-ws][data-room="' + esc(room.getAttribute("data-id")) + '"]');
            if (inRoom.length) return Array.prototype.slice.call(inRoom);
        }
        if (editable && !declared && target.id && SHAPES[target.tagName.toLowerCase()] && target !== svg) return [target];
        return [];
    }

    function seats() { return selected.filter(function (el) { return el.hasAttribute("data-ws"); }); }
    function shapes() { return selected.filter(function (el) { return !el.hasAttribute("data-ws"); }); }

    function show(selector, on) { all(selector).forEach(function (el) { el.hidden = !on; }); }

    function zoneName(id) {
        var el = root.querySelector('[data-zone-id="' + esc(id) + '"] .wp-zone-name');
        return el ? el.textContent : "";
    }

    function updatePanel() {
        var s = seats(), e = shapes();
        show('[data-when="none"]', s.length === 0 && e.length === 0);
        // One desk: the desk view (and, for editors, its zone and seat settings); several seats: the selection box.
        show('[data-when="ws"]', s.length > 1 || (s.length === 1 && editable));
        show('[data-when="one"]', s.length === 1);
        show('[data-when="zoned"]', s.some(function (x) { return x.hasAttribute("data-zone"); }));
        show('[data-when="on"]', s.some(function (x) { return !x.classList.contains("fp-ws-off"); }));
        show('[data-when="off"]', s.some(function (x) { return x.classList.contains("fp-ws-off"); }));
        show('[data-when="el"]', e.length > 0);
        root.classList.toggle("has-selection", selected.length > 0);
        var printSel = root.querySelector('[data-print="selection"]');
        if (printSel) printSel.disabled = selected.length === 0;

        all('[data-ids="ws"]').forEach(function (f) { f.value = s.map(function (x) { return x.getAttribute("data-ws"); }).join(","); });
        all('[data-ids="el"]').forEach(function (f) { f.value = e.map(function (x) { return x.id; }).join(","); });

        var title = root.querySelector("[data-sel-title]");
        if (title) title.textContent = s.length === 1 ? "Zone and seat settings" : "Selection";
        var summary = root.querySelector("[data-sel-summary]");
        if (summary) { summary.textContent = s.length === 1 ? "" : describe(s); summary.hidden = s.length === 1; }
        var list = root.querySelector("[data-sel-list]");
        if (list) {
            list.textContent = "";
            if (s.length > 1) s.slice(0, 12).forEach(function (x) {
                var li = document.createElement("li");
                li.textContent = seatText(x);
                list.appendChild(li);
            });
            if (s.length > 12) { var more = document.createElement("li"); more.textContent = "… and " + (s.length - 12) + " more"; list.appendChild(more); }
            list.hidden = s.length < 2;
        }
        var elSummary = root.querySelector("[data-el-summary]");
        if (elSummary) elSummary.textContent = e.length === 1 ? "1 shape selected." : e.length + " shapes selected.";
        syncZoneForm(s);

        // The desk view (or the selection) is at the top of the panel; in full screen the panel is an overlay.
        var panel = root.querySelector(".wp-panel");
        if (panel && s.length) panel.scrollTop = 0;
        if (s.length === 1) openDesk(s[0]); else closeDesk();
    }

    function roomOf(seat) { return seat.getAttribute("data-room-name") || seat.getAttribute("data-room") || ""; }

    // A seat has no name of its own: it is who sits there, its desktop, or what kind of seat it is.
    function seatText(seat) {
        return seat.getAttribute("data-person") || seat.getAttribute("data-desktop") ||
            (seat.classList.contains("fp-ws-off") ? "Switched-off seat" : seat.hasAttribute("data-desk") ? "Free desk" : "Seat, not a desk");
    }

    function distinct(list) { return list.filter(function (v, i) { return v && list.indexOf(v) === i; }); }

    // "A whole table in Finance · 6 seats, 2 free desks", "3 of the 8 seats of a table in Sales", "14 seats in 2 rooms".
    function describe(s) {
        var tables = distinct(s.map(function (x) { return x.getAttribute("data-table"); }));
        var rooms = distinct(s.map(roomOf));
        var text = s.length + " seats";
        if (tables.length === 1 && s.every(function (x) { return x.getAttribute("data-table") === tables[0]; })) {
            var total = svg.querySelectorAll('[data-ws][data-table="' + esc(tables[0]) + '"]').length;
            text = total === s.length ? "A whole table, " + s.length + " seats," : s.length + " of the " + total + " seats of a table";
        }
        if (rooms.length === 1) text += " in " + rooms[0];
        else if (rooms.length > 1) text += " in " + rooms.length + " rooms";
        var free = s.filter(function (x) { return x.classList.contains("fp-free"); }).length;
        var zones = distinct(s.map(function (x) { return x.getAttribute("data-zone"); }));
        var parts = [];
        if (free) parts.push(free + " free desk" + (free === 1 ? "" : "s"));
        if (zones.length === 1) parts.push(s.every(function (x) { return x.getAttribute("data-zone") === zones[0]; }) ? "zone " + zoneName(zones[0]) : "partly zone " + zoneName(zones[0]));
        else if (zones.length > 1) parts.push(zones.length + " zones");
        return text.replace(/,$/, "") + (parts.length ? " · " + parts.join(", ") : "") + ".";
    }

    // The zone box starts at the zone the selected seats share; "New zone…" when they have none or several.
    var zoneForm = root.querySelector("[data-zone-form]");
    function syncZoneNew() {
        if (!zoneForm) return;
        var isNew = zoneForm.querySelector("[data-zone-select]").value === "new";
        zoneForm.querySelector("[data-zone-new]").hidden = !isNew;
        zoneForm.querySelector("[data-zone-new] input").required = isNew;
    }
    function syncZoneForm(s) {
        if (!zoneForm || s.length === 0) return;
        var zones = distinct(s.map(function (x) { return x.getAttribute("data-zone") || "-"; }));
        var select = zoneForm.querySelector("[data-zone-select]");
        select.value = zones.length === 1 && zones[0] !== "-" ? zones[0] : "new";
        zoneForm.querySelector("[data-zone-submit]").textContent = s.length === 1 ? "Set zone" : "Set zone for " + s.length + " seats";
        syncZoneNew();
    }
    if (zoneForm) zoneForm.querySelector("[data-zone-select]").addEventListener("change", syncZoneNew);

    // ---- the desk view: one desk, its person, desktop and screens ----------------------------------------------------
    // Click a desk: the map zooms to its table and the panel shows what is there. Editors fill each slot with a search
    // box (Enter picks and moves to the next slot; Enter in an empty box goes to the next free desk), replace the person
    // or the desktop, release anything, and drag a card onto another desk to move it. Managers see the same, read only.

    var deskView = root.querySelector("[data-desk-view]");
    var assignForm = document.getElementById("wp-assign-form");
    var replaceForm = document.getElementById("wp-replace-form");
    var releaseForm = document.getElementById("wp-release-form");
    var SLOTS = ["person", "desktop", "screen"];
    var GROUPS = { free: "Not placed yet", elsewhere: "Placed elsewhere", here: "At this desk", blocked: "Cannot be placed here" };
    var deskSeat = null;      // the seat in the desk view
    var deskWs = null;        // its workspot id (the element is swapped when the map is refreshed)
    var deskData = null;      // what the server said is there
    var deskToken = 0;
    var deskTrail = [];       // desks shown before, for "Previous desk"
    var goingBack = false;
    var pendingFocus = null;  // { slot, chain }: the slot to focus once the desk view is drawn again
    var replacing = null;     // "person" or "desktop" while its Replace box is open
    var combos = {};
    var coarse = window.matchMedia && window.matchMedia("(pointer: coarse)").matches;

    function node(tag, cls, text) {
        var n = document.createElement(tag);
        if (cls) n.className = cls;
        if (text !== undefined) n.textContent = text;
        return n;
    }

    function icon(kind) {
        var i = node("span", "wp-pal-icon wp-pal-icon--" + kind, kind === "person" ? "P" : kind === "desktop" ? "D" : "S");
        i.setAttribute("aria-hidden", "true");
        return i;
    }

    function slotEl(kind) { return deskView ? deskView.querySelector('[data-slot="' + kind + '"]') : null; }

    // What sits in a slot: a card with the name or tag and the job title or model. Editors drag it onto another desk to
    // move it, and get Replace (person, desktop) and Release.
    function card(kind, title, meta, dragId, assignmentId) {
        var li = node("li", "wp-item");
        if (editable && dragId) {
            li.setAttribute("data-drag-kind", kind);
            li.setAttribute("data-drag-id", String(dragId));
            li.setAttribute("data-drag-title", title);
            li.title = "Drag onto another desk to move";
        }
        li.appendChild(icon(kind));
        var text = node("span", "wp-item-text");
        text.appendChild(node("strong", null, title));
        if (meta) text.appendChild(node("span", "muted", meta));
        li.appendChild(text);
        if (editable && assignmentId && releaseForm) {
            var actions = node("span", "wp-item-actions");
            if (kind !== "screen" && replaceForm && deskData && deskData.isActive && deskData.isDesk) {
                var r = node("button", "btn--sm", "Replace");
                r.type = "button";
                r.setAttribute("data-replace", kind);
                r.setAttribute("aria-label", "Replace " + title);
                actions.appendChild(r);
            }
            var b = node("button", "btn--sm", "Release");
            b.type = "button";
            b.setAttribute("data-release-id", String(assignmentId));
            b.setAttribute("data-release-name", title);
            b.setAttribute("data-release-kind", kind);
            b.setAttribute("aria-label", "Release " + title);
            actions.appendChild(b);
            li.appendChild(actions);
        }
        return li;
    }

    // "Finance · zone Finance": the room and the zone; a desk has no number of its own.
    function placeLine(room, zone) { return [room, zone ? "zone " + zone : null].filter(Boolean).join(" · "); }

    function fillSlot(kind, cards, searchOpen, emptyText, isDesk) {
        var slot = slotEl(kind);
        if (!slot) return;
        var items = slot.querySelector("[data-slot-items]");
        items.textContent = "";
        cards.forEach(function (c) { items.appendChild(c); });
        if (cards.length === 0 && !searchOpen) items.appendChild(node("li", "wp-item wp-item-empty", emptyText));
        items.hidden = items.children.length === 0;
        slot.hidden = !isDesk && cards.length === 0;
        var find = slot.querySelector("[data-slot-find]");
        if (!find) return;
        var wasOpen = !find.hidden;
        find.hidden = !searchOpen;
        var input = find.querySelector("[data-slot-input]");
        var swap = replacing === kind && cards.length > 0;
        input.placeholder = swap ? (kind === "person" ? "Who sits here instead? Type a name…" : "Which desktop instead? Type a tag…") : input.getAttribute("data-hint");
        find.querySelector("[data-slot-label]").textContent = swap ? "Replace with" : kind === "screen" ? "Add a screen" : "Find a " + kind;
        find.querySelector("[data-slot-cancel]").hidden = !swap;
        if (!searchOpen || !wasOpen) { input.value = ""; if (combos[kind]) combos[kind].reset(); }
    }

    function renderDesk(d) {
        deskData = d;
        deskView.querySelector("[data-spot-status]").hidden = true;
        deskView.querySelector("[data-spot-body]").hidden = false;
        var open = editable && d.isActive && d.isDesk;
        var kindLine = deskView.querySelector("[data-spot-kind]");
        kindLine.hidden = d.isActive && d.isDesk;
        kindLine.textContent = !d.isActive ? "This seat is switched off: nothing new is placed here." : !d.isDesk ? "This seat is not a desk: nobody is placed here." : "";
        deskView.querySelector("[data-desk-title]").textContent = d.isDesk && d.isActive ? "Desk" : "Seat";
        deskView.querySelector("[data-desk-place]").textContent = placeLine(d.roomName, d.zoneName) || d.floorName;
        var p = d.person;
        fillSlot("person", p ? [card("person", p.name, [p.isActive ? null : "No longer works here", p.jobTitle || p.username, p.siteKey].filter(Boolean).join(" · "),
            p.personId, p.assignmentId)] : [], open && (!p || replacing === "person"), d.isDesk ? "Nobody sits here: free desk." : "Nobody.", d.isDesk);
        var dt = d.desktop;
        fillSlot("desktop", dt ? [card("desktop", dt.tag, [dt.model, dt.description].filter(Boolean).join(" · ") || dt.type, dt.assetId, dt.assignmentId)] : [],
            open && (!dt || replacing === "desktop"), "No desktop.", d.isDesk);
        fillSlot("screen", d.screens.map(function (sc) {
            return card("screen", sc.tag, [sc.model, sc.description].filter(Boolean).join(" · ") || sc.type, sc.assetId, sc.assignmentId);
        }), open, "No screen.", d.isDesk);
        var flags = deskView.querySelector("[data-spot-flags]");
        flags.textContent = "";
        d.flags.forEach(function (f) { flags.appendChild(node("li", null, f)); });
        flags.hidden = d.flags.length === 0;
        deskView.querySelector("[data-desk-prev]").disabled = deskTrail.length === 0;
        if (pendingFocus) { var f = pendingFocus; pendingFocus = null; focusSlot(f.slot, f.chain); }
    }

    // Focuses the search box of a slot, or of the next one that is open (person, desktop, screen). Opening a desk with a
    // click only goes as far as the desktop: a full desk keeps the focus on the map.
    function focusSlot(kind, chain) {
        for (var i = SLOTS.indexOf(kind); i >= 0 && i < SLOTS.length; i++) {
            if (!chain && SLOTS[i] === "screen") return;
            var slot = slotEl(SLOTS[i]);
            var find = slot && slot.querySelector("[data-slot-find]");
            if (find && !find.hidden && !slot.hidden) { find.querySelector("[data-slot-input]").focus({ preventScroll: false }); return; }
        }
    }

    function openDesk(seat) {
        if (!deskView) return;
        var ws = seat.getAttribute("data-ws");
        if (deskWs !== ws) {
            if (deskWs && !goingBack) { deskTrail.push(deskWs); if (deskTrail.length > 100) deskTrail.shift(); }
            replacing = null;
            Object.keys(combos).forEach(function (k) { combos[k].reset(); combos[k].input.value = ""; });
        }
        goingBack = false;
        var same = deskWs === ws && deskData && String(deskData.workspotId) === ws;
        deskSeat = seat;
        deskWs = ws;
        if (!same) {
            // Show the room and zone at once; the rest comes from the server.
            deskData = null;
            var zone = seat.hasAttribute("data-zone") ? zoneName(seat.getAttribute("data-zone")) : "";
            deskView.querySelector("[data-desk-title]").textContent = seat.hasAttribute("data-desk") ? "Desk" : "Seat";
            deskView.querySelector("[data-desk-place]").textContent = placeLine(roomOf(seat), zone);
            var status = deskView.querySelector("[data-spot-status]");
            status.hidden = false;
            status.textContent = "Loading…";
            deskView.querySelector("[data-spot-body]").hidden = true;
        }
        var token = ++deskToken;
        fetch(deskView.getAttribute("data-spot-url") + "&workspotId=" + encodeURIComponent(ws), {
            headers: { Accept: "application/json" }, credentials: "same-origin"
        }).then(function (res) {
            if (!res.ok) throw new Error("spot failed");
            return res.json();
        }).then(function (d) {
            if (token === deskToken && deskWs === ws) renderDesk(d);
        }).catch(function () {
            if (token !== deskToken) return;
            var status = deskView.querySelector("[data-spot-status]");
            status.hidden = false;
            status.textContent = "Could not load what is at this desk.";
        });
    }

    function closeDesk() {
        if (!deskView) return;
        deskToken++;
        deskSeat = null;
        deskWs = null;
        deskData = null;
        replacing = null;
        pendingFocus = null;
        Object.keys(combos).forEach(function (k) { combos[k].reset(); });
    }

    // Zooms to the desk's table, unless the table is already in view at a closer zoom. In full screen the panel covers
    // the right of the map, so the table goes to the middle of what is left.
    function zoomToDesk(seat) {
        var table = seat.closest("[data-kind=table]");
        if (!table && seat.getAttribute("data-table")) table = svg.querySelector('[data-kind=table][data-id="' + esc(seat.getAttribute("data-table")) + '"]');
        var b = boundsOf(table ? [table, seat] : [seat]);
        var panel = root.querySelector(".wp-panel");
        var cover = full && panel ? panel.getBoundingClientRect().width + 12 : 0;
        var cw = canvas.getBoundingClientRect().width || 1;
        var visibleW = view.w * Math.max(0.3, 1 - cover / cw);
        var inView = b.x >= view.x && b.y >= view.y && b.x + b.w <= view.x + visibleW && b.y + b.h <= view.y + view.h;
        if (inView && view.w <= fitW / 2.5) return;
        // The table with its neighbours around it, not filling the canvas.
        var a = aspect();
        var w = Math.max(b.w * 2.6, b.h * 2.6 / a, fitW / 4);
        setView(b.x + b.w / 2 - w / 2, b.y + b.h / 2 - w * a / 2, w);
        if (cover) setView(view.x + view.w * cover / cw / 2, view.y, view.w);
    }

    function showDesk(seat, focus) {
        clearHits();
        pendingFocus = focus || null;
        setSelection([seat]);
        zoomToDesk(seat);
    }

    // The next free desk: at the same table first, then in the same room, then anywhere on the floor (in drawing order,
    // starting after this desk).
    function nextFreeDesk(from) {
        var seatsInOrder = all("[data-ws]", svg);
        var at = seatsInOrder.indexOf(from);
        var free = seatsInOrder.filter(function (x) { return x !== from && x.hasAttribute("data-desk") && x.classList.contains("fp-free"); });
        function after(list) {
            if (list.length === 0) return null;
            var later = list.filter(function (x) { return seatsInOrder.indexOf(x) > at; });
            return later[0] || list[0];
        }
        var table = from.getAttribute("data-table"), room = from.getAttribute("data-room");
        return (table && after(free.filter(function (x) { return x.getAttribute("data-table") === table; }))) ||
            (room && after(free.filter(function (x) { return x.getAttribute("data-room") === room; }))) ||
            after(free);
    }

    function goNextFree() {
        if (!deskSeat) return;
        var next = nextFreeDesk(deskSeat);
        if (!next) { say("There is no other free desk on this floor."); return; }
        showDesk(next, editable ? { slot: "person", chain: false } : null);
    }

    function goBack() {
        var seat = null;
        while (deskTrail.length && !seat) seat = svg.querySelector('[data-ws="' + esc(deskTrail.pop()) + '"]');
        if (!seat) return;
        goingBack = true;
        showDesk(seat, editable ? { slot: "person", chain: false } : null);
    }

    // ---- placing, replacing and releasing (fetch, then swap in the fresh map) ------------------------------------

    function post(form, values) {
        Object.keys(values).forEach(function (k) {
            var f = form.querySelector('[name="' + k + '"]');
            if (f) f.value = String(values[k]);
        });
        return fetch(form.getAttribute("action"), {
            method: "POST", body: new FormData(form), credentials: "same-origin", headers: { Accept: "application/json" }
        }).then(function (res) {
            return res.json().catch(function () { return { message: "Something went wrong (" + res.status + ")." }; })
                .then(function (body) { if (!res.ok) throw new Error(body.message || "Not possible."); return body; });
        });
    }

    // A drop on the map or a click in the list.
    function place(seat, kind, id) {
        if (!assignForm) return;
        var ws = seat.getAttribute("data-ws");
        post(assignForm, { workspotId: ws, kind: kind, targetId: id })
            .then(function (body) { say(body.message); refresh(ws); })
            .catch(function (err) { say(err.message, true); });
    }

    // A pick in a slot of the desk view: place it, or replace what is there; then on to the next slot.
    function pickFor(kind, row) {
        if (!deskWs || !assignForm) return;
        var ws = deskWs;
        var current = deskData && (kind === "person" ? deskData.person : kind === "desktop" ? deskData.desktop : null);
        var form = replacing === kind && current ? replaceForm : assignForm;
        post(form, { workspotId: ws, kind: kind, targetId: row.id }).then(function (body) {
            replacing = null;
            pendingFocus = { slot: kind === "person" ? "desktop" : "screen", chain: true };
            say(body.message);
            refresh(ws);
        }).catch(function (err) {
            say(err.message, true);
            focusSlot(kind, true);
        });
    }

    function release(assignmentId, name, kind) {
        var ws = deskWs;
        post(releaseForm, { assignmentId: assignmentId, workspotId: ws })
            .then(function () { pendingFocus = { slot: kind, chain: true }; say("Released " + name + " from this desk."); refresh(ws); })
            .catch(function (err) { say(err.message, true); });
    }

    function startReplace(kind) {
        replacing = kind;
        if (deskData) renderDesk(deskData);
        focusSlot(kind, true);
    }

    function cancelReplace() {
        var kind = replacing;
        replacing = null;
        if (deskData) renderDesk(deskData);
        var b = kind && slotEl(kind) && slotEl(kind).querySelector("[data-replace]");
        if (b) b.focus();
    }

    if (deskView) {
        deskView.addEventListener("click", function (e) {
            var t = e.target instanceof Element ? e.target : null;
            if (!t) return;
            var rel = t.closest("[data-release-id]");
            if (rel) { release(rel.getAttribute("data-release-id"), rel.getAttribute("data-release-name"), rel.getAttribute("data-release-kind")); return; }
            var rep = t.closest("[data-replace]");
            if (rep) { startReplace(rep.getAttribute("data-replace")); return; }
            if (t.closest("[data-slot-cancel]")) { cancelReplace(); return; }
            if (t.closest("[data-desk-close]")) { setSelection([]); canvas.focus({ preventScroll: true }); return; }
            if (t.closest("[data-desk-next]")) { goNextFree(); return; }
            if (t.closest("[data-desk-prev]")) goBack();
        });

        if (editable) SLOTS.forEach(function (kind) {
            var slot = slotEl(kind);
            var input = slot && slot.querySelector("[data-slot-input]");
            if (!input) return;
            combos[kind] = createCombo({
                input: input, list: slot.querySelector(".combo-list"), min: 1, browse: true, browseOnClick: true, autoActive: true,
                idPrefix: "wp-slot-" + kind + "-opt-",
                emptyText: function (q) {
                    return q ? "Nothing found." : kind === "person" ? "Everyone at this site is placed." : kind === "desktop" ? "No desktops left to place at this site." : "No screens left to place at this site.";
                },
                url: function (q) {
                    return deskView.getAttribute("data-search-url") + "&workspotId=" + encodeURIComponent(deskWs || "") + "&kind=" + kind + "&q=" + encodeURIComponent(q);
                },
                pickable: function (r) { return r.canPick; },
                group: function (r) { return GROUPS[r.group]; },
                render: function (r, option) {
                    option.classList.add("wp-cand");
                    option.appendChild(icon(r.kind));
                    var text = node("span", "wp-cand-text");
                    text.appendChild(node("span", "combo-name", r.title));
                    var meta = [r.subtitle, r.siteKey].filter(Boolean).join(" · ");
                    if (meta) text.appendChild(node("span", "combo-meta", meta));
                    var where = r.group === "elsewhere" ? (r.place || "Another desk") + " – move here"
                        : r.group === "here" ? "Here at this desk" : r.reason || (r.place ? "At " + r.place : "");
                    if (r.group === "blocked" && r.reason && r.place) where = r.reason + " (at " + r.place + ")";
                    if (where) text.appendChild(node("span", "wp-cand-where wp-cand-where--" + r.group, where));
                    option.appendChild(text);
                },
                choose: function (r) { input.value = ""; combos[kind].reset(); pickFor(kind, r); }
            });
            input.addEventListener("keydown", function (e) {
                if (e.defaultPrevented) return;
                if (e.key === "Enter" && input.value.trim() === "") { e.preventDefault(); goNextFree(); }
                else if (e.key === "Escape" && replacing === kind) { e.preventDefault(); cancelReplace(); }
            });
        });
    }

    function refresh(selectWs) {
        var keep = seats().map(function (x) { return x.getAttribute("data-ws"); });
        if (selectWs && keep.indexOf(selectWs) < 0) keep = [selectWs];
        var get = function (url) { return fetch(url, { credentials: "same-origin" }).then(function (r) { if (!r.ok) throw new Error(); return r.text(); }); };
        Promise.all([get(root.getAttribute("data-map-url")), get(root.getAttribute("data-status-url"))]).then(function (parts) {
            var holder = document.createElement("div");
            holder.innerHTML = parts[0].trim();
            var fresh = holder.querySelector("svg");
            if (fresh) {
                fresh.setAttribute("viewBox", [view.x, view.y, view.w, view.h].join(" "));
                canvas.replaceChild(fresh, svg);
                svg = fresh;
                engine = createLabelEngine(svg, { prefix: prefix });
            }
            var host = root.querySelector("[data-status-host]");
            if (host) host.innerHTML = parts[1];
            syncHighlightButtons();
            selected = [];
            // The restored selection opens the desk view again (updatePanel), where pendingFocus is picked up.
            setSelection(keep.map(function (id) { return svg.querySelector('[data-ws="' + esc(id) + '"]'); }).filter(Boolean));
            relabel(true);
            if (palette) palette.reload();
        }).catch(function () { window.location.reload(); });
    }

    // ---- the list ("Place from a list"): a second way to place, collapsed by default ---------------------------------

    var palette = null;
    var palRoot = root.querySelector("[data-palette]");
    if (palRoot) {
        var palKind = "person";
        var palInput = palRoot.querySelector("[data-palette-q]");
        var palList = palRoot.querySelector("[data-palette-list]");
        var palTimer = null, palToken = 0;
        var PAL_EMPTY = { person: "Everyone is placed.", desktop: "No desktops left to place.", screen: "No screens left to place." };

        var load = function () {
            if (!palRoot.open) return; // loaded when opened
            var token = ++palToken;
            var q = palInput.value.trim();
            fetch(palRoot.getAttribute("data-palette-url") + "&kind=" + palKind + "&q=" + encodeURIComponent(q), {
                headers: { Accept: "application/json" }, credentials: "same-origin"
            }).then(function (r) { if (!r.ok) throw new Error(); return r.json(); }).then(function (rows) {
                if (token !== palToken) return;
                palList.textContent = "";
                if (rows.length === 0) palList.appendChild(node("li", "wp-pal-empty", q ? "Nothing found that is not placed yet." : PAL_EMPTY[palKind]));
                rows.forEach(function (r) {
                    var li = node("li");
                    var b = node("button", "wp-pal-item");
                    b.type = "button";
                    b.setAttribute("data-drag-kind", r.kind);
                    b.setAttribute("data-drag-id", String(r.id));
                    b.setAttribute("data-drag-title", r.title);
                    b.appendChild(icon(r.kind));
                    var text = node("span", "wp-pal-text");
                    text.appendChild(node("strong", null, r.title));
                    var meta = [r.subtitle, r.siteKey].filter(Boolean).join(" · ");
                    if (meta) text.appendChild(node("span", null, meta));
                    b.appendChild(text);
                    li.appendChild(b);
                    palList.appendChild(li);
                });
            }).catch(function () { if (token === palToken) palList.textContent = ""; });
        };
        palette = { reload: load };
        palRoot.addEventListener("toggle", load);

        all("[data-palette-kind]", palRoot).forEach(function (tab) {
            tab.addEventListener("click", function () {
                palKind = tab.getAttribute("data-palette-kind");
                all("[data-palette-kind]", palRoot).forEach(function (t) { t.setAttribute("aria-pressed", t === tab ? "true" : "false"); });
                load();
            });
        });
        palInput.addEventListener("input", function () { clearTimeout(palTimer); palTimer = setTimeout(load, 200); });
        // A click (or Enter) on an entry places it at the selected desk.
        palList.addEventListener("click", function (e) {
            var b = e.target.closest("[data-drag-kind]");
            if (!b) return;
            var s = seats();
            if (s.length !== 1 || !s[0].hasAttribute("data-desk")) {
                say("Drag the entry onto a desk, or select one desk first and then click the entry.", true);
                return;
            }
            place(s[0], b.getAttribute("data-drag-kind"), b.getAttribute("data-drag-id"));
        });
    }

    // Dragging an entry (from the list, or a person, desktop or screen at the selected seat) onto a desk. Pointer events,
    // so it works with a mouse, a pen and a finger alike.
    var drag = null;
    var ghost = null;
    var dropTarget = null;

    function seatAt(x, y) {
        var el = document.elementFromPoint(x, y);
        if (!el || !svg.contains(el)) return null;
        return el.closest("[data-ws]");
    }

    function setDropTarget(seat) {
        if (dropTarget === seat) return;
        if (dropTarget) dropTarget.classList.remove("is-drop");
        dropTarget = seat;
        if (dropTarget) dropTarget.classList.add("is-drop");
    }

    root.addEventListener("pointerdown", function (e) {
        if (!editable || (e.button !== undefined && e.button !== 0)) return;
        var it = e.target.closest("[data-drag-kind]");
        if (!it) return;
        var button = e.target.closest("button");
        if (button && button !== it) return; // Replace and Release on a card

        drag = { kind: it.getAttribute("data-drag-kind"), id: it.getAttribute("data-drag-id"), title: it.getAttribute("data-drag-title"),
            x: e.clientX, y: e.clientY, pointerId: e.pointerId, started: false };
    });

    document.addEventListener("pointermove", function (e) {
        if (!drag || e.pointerId !== drag.pointerId) return;
        if (!drag.started) {
            if (Math.hypot(e.clientX - drag.x, e.clientY - drag.y) < 6) return;
            drag.started = true;
            ghost = node("div", "wp-ghost", drag.title);
            document.body.appendChild(ghost);
            canvas.classList.add("is-dropping");
        }
        e.preventDefault();
        ghost.style.left = (e.clientX + 14) + "px";
        ghost.style.top = (e.clientY + 10) + "px";
        var seat = seatAt(e.clientX, e.clientY);
        setDropTarget(seat && seat.hasAttribute("data-desk") ? seat : null);
    });

    function endDrag(e, cancelled) {
        if (!drag || (e && e.pointerId !== drag.pointerId)) return;
        var d = drag;
        drag = null;
        if (ghost) { ghost.remove(); ghost = null; }
        canvas.classList.remove("is-dropping");
        var target = dropTarget;
        setDropTarget(null);
        if (!d.started || cancelled) return;
        if (target) place(target, d.kind, d.id);
        else if (e && seatAt(e.clientX, e.clientY)) say("That seat is not a desk.", true);
    }

    document.addEventListener("pointerup", function (e) { endDrag(e, false); });
    document.addEventListener("pointercancel", function (e) { endDrag(e, true); });

    // ---- highlight and zoom ----------------------------------------------------------------------------------

    function clearHits() { all(".is-hit", svg).forEach(function (el) { el.classList.remove("is-hit"); }); }

    function boundsOf(list) {
        var minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        list.forEach(function (el) {
            var b = userBox(svg, el);
            minX = Math.min(minX, b.x); minY = Math.min(minY, b.y);
            maxX = Math.max(maxX, b.x + b.w); maxY = Math.max(maxY, b.y + b.h);
        });
        return { x: minX, y: minY, w: maxX - minX, h: maxY - minY };
    }

    function focusElements(list) {
        clearHits();
        if (list.length === 0) return;
        list.forEach(function (el) { el.classList.add("is-hit"); });
        setSelection(list);
        zoomTo(boundsOf(list), 2.2);
    }

    // Zooms so the box (drawing units) fills the canvas, with `room` times its size around it.
    function zoomTo(b, room) {
        var a = aspect();
        var w = Math.max(b.w * room, fitW / 8);
        var h = Math.max(b.h * room, fitW * a / 8);
        w = Math.max(w, h / a);
        setView(b.x + b.w / 2 - w / 2, b.y + b.h / 2 - w * a / 2, w);
    }

    function focusSpec(spec) {
        var i = spec.indexOf(":");
        if (i < 0) return;
        var kind = spec.slice(0, i), value = spec.slice(i + 1), q = null;
        if (kind === "ws") q = value.split(",").map(function (v) { return '[data-ws="' + esc(v) + '"]'; }).join(",");
        else if (kind === "room") q = '[data-ws][data-room="' + esc(value) + '"]';
        else if (kind === "zone" || kind === "team") q = '[data-ws][data-zone="' + esc(value) + '"]'; // team: = old links
        if (q) focusElements(all(q, svg));
    }

    root.addEventListener("workplace:focus", function (e) { focusSpec(String(e.detail)); });

    function syncHighlightButtons() {
        all("[data-highlight]").forEach(function (b) {
            b.setAttribute("aria-pressed", canvas.classList.contains("hl-" + b.getAttribute("data-highlight")) ? "true" : "false");
        });
    }

    // Buttons in the side panel (some of it is swapped in after a drop, so the clicks are delegated).
    root.addEventListener("click", function (e) {
        var spot = e.target.closest("[data-show-spot]");
        if (spot) { focusSpec("ws:" + spot.getAttribute("data-show-spot")); return; }
        var group = e.target.closest("[data-show-seats]");
        if (group) { focusSpec("ws:" + group.getAttribute("data-show-seats")); return; }
        var zone = e.target.closest("[data-show-zone]");
        if (zone) { focusSpec("zone:" + zone.getAttribute("data-show-zone")); return; }
        var hl = e.target.closest("[data-highlight]");
        if (hl) {
            var cls = "hl-" + hl.getAttribute("data-highlight");
            var on = !canvas.classList.contains(cls);
            canvas.classList.remove("hl-free", "hl-flag");
            canvas.classList.toggle(cls, on);
            syncHighlightButtons();
        }
    });

    all("[data-layer]").forEach(function (box) {
        box.addEventListener("change", function () {
            if (box.getAttribute("data-layer") === "zones") canvas.classList.toggle("hide-zones", !box.checked);
            else { namesOn = box.checked; relabel(true); }
        });
    });

    // ---- printing (the browser's print dialog makes the PDF) ----------------------------------------------------

    function openPrint(area, what) {
        var url = root.getAttribute("data-print-url") + "?auto=true";
        if (area) url += "&area=" + [area.x, area.y, area.w, area.h].map(function (v) { return v.toFixed(1); }).join(",");
        if (what) url += "&what=" + encodeURIComponent(what);
        window.open(url, "_blank");
        var menu = root.querySelector(".wp-print");
        if (menu) menu.open = false;
    }

    all("[data-print]").forEach(function (b) {
        b.addEventListener("click", function () {
            var what = b.getAttribute("data-print");
            if (what === "view") openPrint(view, "Part of the floor, as it was on screen");
            else if (what === "floor") openPrint(null, null);
            else if (what === "selection" && selected.length) {
                var bb = boundsOf(selected);
                var pad = Math.max(bb.w, bb.h) * 0.25 + fitW / 100;
                openPrint({ x: bb.x - pad, y: bb.y - pad, w: bb.w + 2 * pad, h: bb.h + 2 * pad }, "Selected seats");
            } else if (what === "area") {
                printMode = true;
                canvas.classList.add("is-print-area");
                var menu = root.querySelector(".wp-print");
                if (menu) menu.open = false;
                say("Drag a rectangle on the map around the part to print.");
            }
        });
    });

    // ---- pointer handling on the map: pan, pinch, click, area -------------------------------------------------

    var band = document.createElement("div");
    band.className = "wp-band";
    band.hidden = true;
    canvas.appendChild(band);

    var pointers = {};
    var gesture = null;

    function count() { return Object.keys(pointers).length; }

    canvas.addEventListener("pointerdown", function (e) {
        if (e.button !== undefined && e.button !== 0) return;
        pointers[e.pointerId] = { x: e.clientX, y: e.clientY };
        try { canvas.setPointerCapture(e.pointerId); } catch (err) { /* synthetic events */ }
        if (count() === 2) {
            var ids = Object.keys(pointers), a = pointers[ids[0]], b = pointers[ids[1]];
            var mid = userPoint((a.x + b.x) / 2, (a.y + b.y) / 2);
            gesture = { type: "pinch", dist: Math.hypot(a.x - b.x, a.y - b.y), view: { x: view.x, y: view.y, w: view.w }, mid: mid };
            band.hidden = true;
            return;
        }
        gesture = {
            type: (printMode || areaMode || e.shiftKey) ? "box" : "pan", print: printMode, target: e.target, startX: e.clientX, startY: e.clientY,
            start: userPoint(e.clientX, e.clientY), moved: false, shift: e.shiftKey || e.ctrlKey || e.metaKey
        };
    });

    canvas.addEventListener("pointermove", function (e) {
        if (!pointers[e.pointerId]) return;
        pointers[e.pointerId] = { x: e.clientX, y: e.clientY };
        if (!gesture) return;
        if (gesture.type === "pinch" && count() >= 2) {
            var ids = Object.keys(pointers), a = pointers[ids[0]], b = pointers[ids[1]];
            var factor = Math.hypot(a.x - b.x, a.y - b.y) / gesture.dist;
            var w = Math.max(fitW / 60, Math.min(fitW * 2, gesture.view.w / factor));
            var k = w / gesture.view.w;
            setView(gesture.mid.x - (gesture.mid.x - gesture.view.x) * k, gesture.mid.y - (gesture.mid.y - gesture.view.y) * k, w);
            return;
        }
        if (!gesture.start) return;
        if (!gesture.moved && Math.hypot(e.clientX - gesture.startX, e.clientY - gesture.startY) < 4) return;
        gesture.moved = true;
        if (gesture.type === "pan") {
            var now = userPoint(e.clientX, e.clientY);
            setView(view.x + gesture.start.x - now.x, view.y + gesture.start.y - now.y, view.w);
        } else if (gesture.type === "box") {
            var r = canvas.getBoundingClientRect();
            band.hidden = false;
            band.style.left = (Math.min(gesture.startX, e.clientX) - r.left) + "px";
            band.style.top = (Math.min(gesture.startY, e.clientY) - r.top) + "px";
            band.style.width = Math.abs(e.clientX - gesture.startX) + "px";
            band.style.height = Math.abs(e.clientY - gesture.startY) + "px";
        }
    });

    function inside(box, el, whole) {
        var r = el.getBoundingClientRect();
        if (whole) return r.left >= box.left && r.right <= box.right && r.top >= box.top && r.bottom <= box.bottom;
        var cx = (r.left + r.right) / 2, cy = (r.top + r.bottom) / 2;
        return cx >= box.left && cx <= box.right && cy >= box.top && cy <= box.bottom;
    }

    function finish(e) {
        if (!pointers[e.pointerId]) return;
        delete pointers[e.pointerId];
        var g = gesture;
        if (count() > 0 || !g) { if (count() === 0) gesture = null; return; }
        gesture = null;
        band.hidden = true;
        if (g.type === "pinch") return;
        if (g.type === "box" && g.moved) {
            if (g.print) {
                var end = userPoint(e.clientX, e.clientY);
                printMode = false;
                canvas.classList.remove("is-print-area");
                openPrint({ x: Math.min(g.start.x, end.x), y: Math.min(g.start.y, end.y), w: Math.abs(end.x - g.start.x), h: Math.abs(end.y - g.start.y) },
                    "Part of the floor");
                return;
            }
            var box = {
                left: Math.min(g.startX, e.clientX), right: Math.max(g.startX, e.clientX),
                top: Math.min(g.startY, e.clientY), bottom: Math.max(g.startY, e.clientY)
            };
            var found = all("[data-ws]", svg).filter(function (el) { return inside(box, el, false); });
            if (!declared && editable && found.length === 0)
                found = all("rect[id],circle[id],ellipse[id],path[id],polygon[id]", svg).filter(function (el) { return inside(box, el, true); });
            if (g.shift) toggle(found); else setSelection(found);
            return;
        }
        if (g.moved) return;
        var hit = resolve(g.target);
        if (hit.length === 0) { if (!g.shift) setSelection([]); }
        else if (g.shift) toggle(hit);
        else if (hit.length === 1 && hit[0].hasAttribute("data-ws")) showDesk(hit[0], editable && !coarse ? { slot: "person", chain: false } : null);
        else setSelection(hit);
    }

    canvas.addEventListener("pointerup", finish);
    canvas.addEventListener("pointercancel", function (e) { delete pointers[e.pointerId]; gesture = null; band.hidden = true; });

    canvas.addEventListener("wheel", function (e) {
        e.preventDefault();
        var p = userPoint(e.clientX, e.clientY);
        zoomAt(e.deltaY < 0 ? 1.2 : 1 / 1.2, p.x, p.y);
    }, { passive: false });

    // Keys on the map: + / - zoom, 0 fits, the arrow keys move the map.
    canvas.addEventListener("keydown", function (e) {
        var c = centre();
        var step = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] }[e.key];
        if (e.key === "+" || e.key === "=") zoomAt(1.3, c.x, c.y);
        else if (e.key === "-") zoomAt(1 / 1.3, c.x, c.y);
        else if (e.key === "0") fitView();
        else if (step) setView(view.x + step[0] * view.w * 0.12, view.y + step[1] * view.h * 0.12, view.w);
        else return;
        e.preventDefault();
    });

    // Esc undoes one thing at a time: a search list closes itself first (it handles the key; in the desk view a Replace
    // box closes next), then print-area mode, search highlights, the desk view or selection and last full screen.
    document.addEventListener("keydown", function (e) {
        if (e.key !== "Escape" || e.defaultPrevented) return;
        var t = e.target instanceof Element ? e.target : null;
        if (t && t.closest("input, textarea, select") && !t.closest("[data-workplace-find], [data-slot-find]")) return;
        if (printMode) { printMode = false; canvas.classList.remove("is-print-area"); }
        else if (svg.querySelector(".is-hit")) clearHits();
        else if (selected.length) { setSelection([]); if (t && deskView && deskView.contains(t)) canvas.focus({ preventScroll: true }); }
        else if (full) setFull(false);
        else return;
        e.preventDefault();
    });

    // Double-click a room to zoom to it.
    canvas.addEventListener("dblclick", function (e) {
        var room = e.target instanceof Element ? e.target.closest("[data-kind=room]") : null;
        if (!room || !svg.contains(room)) {
            var seat = e.target instanceof Element ? e.target.closest("[data-ws][data-room]") : null;
            if (seat) room = svg.querySelector('[data-kind=room][data-id="' + esc(seat.getAttribute("data-room")) + '"]');
        }
        if (!room) {
            // A table or another shape on top of the room: the smallest room under the pointer.
            var p = userPoint(e.clientX, e.clientY), best = null;
            all("[data-kind=room]", svg).forEach(function (r) {
                var b = userBox(svg, r);
                if (p.x >= b.x && p.x <= b.x + b.w && p.y >= b.y && p.y <= b.y + b.h && (!best || b.w * b.h < best.a)) best = { el: r, a: b.w * b.h };
            });
            room = best && best.el;
        }
        if (!room) return;
        e.preventDefault();
        zoomTo(userBox(svg, room), 1.1);
    });

    all("[data-zoom]").forEach(function (b) {
        b.addEventListener("click", function () {
            var c = centre(), what = b.getAttribute("data-zoom");
            if (what === "in") zoomAt(1.4, c.x, c.y);
            else if (what === "out") zoomAt(1 / 1.4, c.x, c.y);
            else fitView();
        });
    });

    var area = root.querySelector("[data-area]");
    if (area) area.addEventListener("click", function () {
        areaMode = !areaMode;
        area.setAttribute("aria-pressed", areaMode ? "true" : "false");
        canvas.classList.toggle("is-area", areaMode);
    });

    // Full screen inside the page (not the browser's Fullscreen API, so Esc can leave it last): the map takes the whole
    // window, the side panel comes back as an overlay on the right while something is selected.
    var fullButton = root.querySelector("[data-fullscreen]");
    function setFull(on) {
        var c = centre(), w = view.w;
        full = on;
        root.classList.toggle("is-full", on);
        document.documentElement.classList.toggle("wp-full-open", on);
        if (fullButton) {
            fullButton.setAttribute("aria-pressed", on ? "true" : "false");
            var label = fullButton.querySelector("[data-fullscreen-label]");
            if (label) label.textContent = on ? "Exit full screen" : "Full screen";
            fullButton.title = on ? "Leave full screen (Esc)" : "Full screen";
        }
        sizeCanvas();
        var a = aspect();
        setView(c.x - w / 2, c.y - w * a / 2, w);
        canvas.focus({ preventScroll: true });
    }
    if (fullButton) fullButton.addEventListener("click", function () { setFull(!full); });

    window.addEventListener("resize", function () {
        var c = centre(), w = view.w;
        sizeCanvas();
        var a = aspect();
        setView(c.x - w / 2, c.y - w * a / 2, w);
    });
    root.classList.add("is-live");
    sizeCanvas();
    updatePanel();
    fitView();
    relabel(true);
    // Fonts change the width of the labels: fit them again once the bundled fonts are in.
    if (document.fonts && document.fonts.ready) document.fonts.ready.then(function () { engine.remeasure(); relabel(true); });
    var initial = (root.getAttribute("data-focus") || "").split(",").filter(Boolean)
        .map(function (id) { return svg.getElementById(id); }).filter(Boolean);
    if (initial.length) focusElements(initial);
})();

// Floor plan print page: the label engine fits names and tags at 7 pt or more on the chosen paper (A4 or A3 landscape);
// what does not fit gets a number and a key under the map, and on A4 the page then suggests A3. The print dialog opens
// by itself when the floor page asked for it, or with the button. "Save as PDF" in that dialog makes the PDF.
(function () {
    var sheet = document.querySelector("[data-print-sheet]");
    if (!sheet) return;
    var map = sheet.querySelector("[data-print-map]");
    var svg = map && map.querySelector("svg");
    var keyBox = sheet.querySelector("[data-callout-box]");
    var hint = document.querySelector("[data-a3-hint]");
    var a3 = sheet.getAttribute("data-paper") === "a3";
    // The sheet's width on paper in CSS px (96 per inch): 277 mm on A4, 400 mm on A3 (landscape, 10 mm margins).
    var paperWidth = a3 ? 1512 : 1047;
    var engine = svg ? createLabelEngine(svg, { prefix: sheet.getAttribute("data-tag-prefix") || "" }) : null;
    function layout() {
        if (!engine) return;
        map.classList.add("show-labels");
        var callouts = -1;
        // The key takes room from the map, which changes the scale: settle in a few rounds.
        for (var round = 0; round < 4; round++) {
            var ctm = svg.getScreenCTM();
            if (!ctm || !ctm.a) return;
            var scale = sheet.getBoundingClientRect().width / paperWidth;
            var res = engine.layout(ctm.a, 7 * 96 / 72 * scale, true);
            if (keyBox) {
                keyBox.hidden = res.callouts === 0;
                renderCalloutKey(keyBox.querySelector("[data-callout-key]"), res.key);
            }
            sheet.setAttribute("data-callouts", String(res.callouts));
            if (res.callouts === callouts) break;
            callouts = res.callouts;
        }
        if (hint) hint.hidden = a3 || callouts <= 0;
    }
    layout();
    if (document.fonts && document.fonts.ready) document.fonts.ready.then(function () { if (engine) engine.remeasure(); layout(); });
    window.addEventListener("resize", layout);
    // Lay the sheet out at its size on paper while printing, so the labels are fitted for the paper, not the window.
    window.addEventListener("beforeprint", function () { document.body.classList.add("is-printing"); layout(); });
    window.addEventListener("afterprint", function () { document.body.classList.remove("is-printing"); layout(); });
    var button = document.querySelector("[data-print-now]");
    if (button) button.addEventListener("click", function () { window.print(); });
    if (sheet.getAttribute("data-autoprint") === "true") {
        var go = function () { setTimeout(function () { layout(); window.print(); }, 300); };
        if (document.fonts && document.fonts.ready) document.fonts.ready.then(go); else go();
    }
})();

// ---------------------------------------------------------------------------------------------------------------
// Theme switch in the top bar: light, dark or follow the system. theme.js applied the saved choice before the first
// paint; this keeps the buttons in step, saves new choices and follows the system setting live while on "system".
(function () {
    var group = document.querySelector("[data-theme-switch]");
    if (!group) return;
    var root = document.documentElement;
    var media = window.matchMedia ? window.matchMedia("(prefers-color-scheme: dark)") : null;
    function pref() { return root.getAttribute("data-theme-pref") || "system"; }
    function apply(p) {
        var dark = p === "dark" || (p === "system" && media && media.matches);
        root.setAttribute("data-theme", dark ? "dark" : "light");
        root.setAttribute("data-theme-pref", p);
        group.querySelectorAll("[data-theme-set]").forEach(function (b) {
            b.setAttribute("aria-pressed", b.getAttribute("data-theme-set") === p ? "true" : "false");
        });
    }
    group.hidden = false;
    apply(pref());
    group.addEventListener("click", function (e) {
        var b = e.target instanceof Element ? e.target.closest("[data-theme-set]") : null;
        if (!b) return;
        var p = b.getAttribute("data-theme-set");
        try { localStorage.setItem("ats-theme", p); } catch (err) { /* not saved, still applied */ }
        apply(p);
    });
    if (media) {
        var onChange = function () { if (pref() === "system") apply("system"); };
        if (media.addEventListener) media.addEventListener("change", onChange); else media.addListener(onChange);
    }
})();

// Collapse the sidebar to icons (wide screens). The choice is remembered; theme.js restores it before the first paint.
(function () {
    var toggle = document.querySelector("[data-rail-toggle]");
    if (!toggle) return;
    var root = document.documentElement;
    var label = toggle.querySelector(".rail-toggle-label");
    function sync() {
        var collapsed = root.getAttribute("data-rail") === "collapsed";
        toggle.setAttribute("aria-pressed", collapsed ? "true" : "false");
        toggle.title = collapsed ? "Expand menu" : "Collapse menu";
        if (label) label.textContent = collapsed ? "Expand menu" : "Collapse menu";
    }
    toggle.hidden = false;
    sync();
    toggle.addEventListener("click", function () {
        var collapsed = root.getAttribute("data-rail") !== "collapsed";
        if (collapsed) root.setAttribute("data-rail", "collapsed"); else root.removeAttribute("data-rail");
        try { localStorage.setItem("ats-rail", collapsed ? "collapsed" : ""); } catch (err) { /* not saved */ }
        sync();
    });
})();

// Ctrl+K / Cmd+K, or "/" outside a text field, focuses the search in the top bar.
(function () {
    var input = document.querySelector("[data-global-search] input[type=search]");
    if (!input) return;
    document.addEventListener("keydown", function (e) {
        var typing = e.target instanceof Element && e.target.closest("input, textarea, select, [contenteditable]");
        var combo = (e.ctrlKey || e.metaKey) && !e.altKey && (e.key === "k" || e.key === "K");
        if (combo || (e.key === "/" && !typing && !e.ctrlKey && !e.metaKey && !e.altKey)) {
            e.preventDefault();
            input.focus();
            input.select();
        }
    });
})();
