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

// Overview "Find, check out and return" (IT). /?handler=Find returns assets, pools and people; each result links to its
// page and offers the action that fits its state (check out an available item, return a checked-out one). Enter on an
// exact tag goes straight to the return (checked out) or the asset page, so a scanner can be used without the mouse.
(function () {
    var root = document.querySelector("[data-find-lookup]");
    if (!root) return;
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
        input: input, list: root.querySelector("[role=listbox]"), idPrefix: "find-option-", min: 2,
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
})();

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
// Workplace: find a seat, room, team, person or desktop (any floor). Picking a result highlights it when it is on the floor that is
// open, otherwise opens its floor with the seats highlighted (?focus=ws:ID | room:ID | team:ID).
(function () {
    Array.prototype.forEach.call(document.querySelectorAll("[data-workplace-find]"), function (root) {
        var input = root.querySelector("input[role=combobox]");
        var url = root.getAttribute("data-url");
        var floorUrl = root.getAttribute("data-floor-url");
        var KINDS = { room: "Room", team: "Team", person: "Person", asset: "Desktop / screen" };

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

// Workplace labels (name and desktop tag next to a seat) are drawn in "screen pixels": each label group is scaled so one
// of its units is one pixel at the current zoom, and labels are only shown once desks are far enough apart for them to
// fit (or always, on the print page). `force`: true = always, false = never, null = when they fit.
function layoutLabels(svg, host, force) {
    var ctm = svg.getScreenCTM();
    if (!ctm || !ctm.a) return;
    if (svg.wpSpacing === undefined) svg.wpSpacing = seatSpacing(svg);
    var visible = force === true || (force !== false && svg.wpSpacing * ctm.a >= 58);
    host.classList.toggle("show-labels", visible);
    if (!visible) return;
    var k = (1 / ctm.a).toFixed(5);
    Array.prototype.forEach.call(svg.querySelectorAll(".fp-label"), function (g) {
        g.setAttribute("transform", "translate(" + g.getAttribute("data-ax") + " " + g.getAttribute("data-ay") + ") scale(" + k + ")");
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

// Typical distance between neighbouring desks (median of nearest neighbours, sampled), in drawing units.
function seatSpacing(svg) {
    var pts = Array.prototype.slice.call(svg.querySelectorAll("[data-ws][data-desk]"), 0, 400).map(function (el) {
        var b = userBox(svg, el);
        return [b.x + b.w / 2, b.y + b.h / 2];
    });
    if (pts.length < 2) return 1e9;
    var near = pts.map(function (p, i) {
        var best = Infinity;
        pts.forEach(function (q, j) { if (i !== j) best = Math.min(best, Math.hypot(p[0] - q[0], p[1] - q[1])); });
        return best;
    }).sort(function (a, b) { return a - b; });
    return near[Math.floor(near.length / 2)] || 1e9;
}

// Workplace floor map: pan (drag), zoom (wheel, pinch, buttons), select seats (click, Shift-click, table or room click,
// Shift-drag or "Select area"), highlight search results, teams, free desks and seats to check, print what you see or a
// part, and (IT and facility) drag people, desktops and screens onto desks. A drop is posted with fetch and the map is
// swapped for a fresh one without losing the zoom. Without JavaScript the map is a static picture.
(function () {
    var root = document.querySelector("[data-floor-map]");
    if (!root) return;
    var canvas = root.querySelector("[data-canvas]");
    var svg = canvas && canvas.querySelector("svg");
    if (!svg) return;

    var editable = root.getAttribute("data-editable") === "true";
    var vb = svg.viewBox.baseVal;
    var home = { x: vb.x, y: vb.y, w: vb.width, h: vb.height };
    var view = { x: home.x, y: home.y, w: home.w, h: home.h };
    var SHAPES = { rect: 1, circle: 1, ellipse: 1, path: 1, polygon: 1, polyline: 1, g: 1 };
    var declared = !!svg.querySelector("[data-kind=seat]");
    var areaMode = false;
    var printMode = false;
    var namesOn = true;
    var selected = []; // elements, in the order they were selected
    var labelFrame = 0;

    function scheduleLabels() {
        if (labelFrame) return;
        labelFrame = requestAnimationFrame(function () {
            labelFrame = 0;
            layoutLabels(svg, canvas, namesOn ? null : false);
        });
    }

    function setView(x, y, w) {
        w = Math.max(home.w / 80, Math.min(home.w * 1.5, w));
        view = { x: x, y: y, w: w, h: w * home.h / home.w };
        svg.setAttribute("viewBox", [view.x, view.y, view.w, view.h].join(" "));
        scheduleLabels();
    }

    function zoomAt(factor, cx, cy) {
        var w = view.w / factor;
        var k = Math.max(home.w / 80, Math.min(home.w * 1.5, w)) / view.w;
        setView(cx - (cx - view.x) * k, cy - (cy - view.y) * k, view.w * k);
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

    function teamName(id) {
        var el = root.querySelector('[data-team-id="' + esc(id) + '"] .wp-team-name');
        return el ? el.textContent : "";
    }

    function updatePanel() {
        var s = seats(), e = shapes();
        show('[data-when="none"]', s.length === 0 && e.length === 0);
        show('[data-when="ws"]', s.length > 0);
        show('[data-when="one"]', s.length === 1);
        show('[data-when="teamed"]', s.some(function (x) { return x.hasAttribute("data-team"); }));
        show('[data-when="on"]', s.some(function (x) { return !x.classList.contains("fp-ws-off"); }));
        show('[data-when="off"]', s.some(function (x) { return x.classList.contains("fp-ws-off"); }));
        show('[data-when="el"]', e.length > 0);
        var printSel = root.querySelector('[data-print="selection"]');
        if (printSel) printSel.disabled = selected.length === 0;

        all('[data-ids="ws"]').forEach(function (f) { f.value = s.map(function (x) { return x.getAttribute("data-ws"); }).join(","); });
        all('[data-ids="el"]').forEach(function (f) { f.value = e.map(function (x) { return x.id; }).join(","); });

        var summary = root.querySelector("[data-sel-summary]");
        if (summary) summary.textContent = s.length === 1 ? "" : describe(s);
        var list = root.querySelector("[data-sel-list]");
        if (list) {
            list.textContent = "";
            if (s.length > 1) s.slice(0, 12).forEach(function (x) {
                var li = document.createElement("li");
                li.textContent = seatText(x);
                list.appendChild(li);
            });
            if (s.length > 12) { var more = document.createElement("li"); more.textContent = "… and " + (s.length - 12) + " more"; list.appendChild(more); }
        }
        var elSummary = root.querySelector("[data-el-summary]");
        if (elSummary) elSummary.textContent = e.length === 1 ? "1 shape selected." : e.length + " shapes selected.";
        syncTeamForm(s);

        if (s.length === 1) {
            var one = s[0];
            var fact = function (name, text) { var n = root.querySelector('[data-fact="' + name + '"]'); if (n) n.textContent = text || "–"; };
            fact("room", roomOf(one));
            fact("team", one.hasAttribute("data-team") ? teamName(one.getAttribute("data-team")) : "");
            loadSpot(one);
        }
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
        var teams = distinct(s.map(function (x) { return x.getAttribute("data-team"); }));
        var parts = [];
        if (free) parts.push(free + " free desk" + (free === 1 ? "" : "s"));
        if (teams.length === 1) parts.push(s.every(function (x) { return x.getAttribute("data-team") === teams[0]; }) ? "team " + teamName(teams[0]) : "partly team " + teamName(teams[0]));
        else if (teams.length > 1) parts.push(teams.length + " teams");
        return text.replace(/,$/, "") + (parts.length ? " · " + parts.join(", ") : "") + ".";
    }

    // The team box starts at the team the selected seats share; "New team…" when they have none or several.
    var teamForm = root.querySelector("[data-team-form]");
    function syncTeamNew() {
        if (!teamForm) return;
        var isNew = teamForm.querySelector("[data-team-select]").value === "new";
        teamForm.querySelector("[data-team-new]").hidden = !isNew;
        teamForm.querySelector("[data-team-new] input").required = isNew;
    }
    function syncTeamForm(s) {
        if (!teamForm || s.length === 0) return;
        var teams = distinct(s.map(function (x) { return x.getAttribute("data-team") || "-"; }));
        var select = teamForm.querySelector("[data-team-select]");
        select.value = teams.length === 1 && teams[0] !== "-" ? teams[0] : "new";
        teamForm.querySelector("[data-team-submit]").textContent = s.length === 1 ? "Set team" : "Set team for " + s.length + " seats";
        syncTeamNew();
    }
    if (teamForm) teamForm.querySelector("[data-team-select]").addEventListener("change", syncTeamNew);

    // ---- what sits at the selected seat -------------------------------------------------------------------------

    var spotHolder = root.querySelector("[data-spot-url]");
    var spotToken = 0;
    var assignForm = document.getElementById("wp-assign-form");
    var releaseForm = document.getElementById("wp-release-form");

    function node(tag, cls, text) {
        var n = document.createElement(tag);
        if (cls) n.className = cls;
        if (text !== undefined) n.textContent = text;
        return n;
    }

    function item(kind, title, meta, drag, assignmentId, workspotId) {
        var li = node("li", "wp-item");
        if (drag && editable) {
            li.setAttribute("data-drag-kind", kind);
            li.setAttribute("data-drag-id", String(drag));
            li.setAttribute("data-drag-title", title);
            li.title = "Drag onto another desk to move";
        }
        var icon = node("span", "wp-pal-icon wp-pal-icon--" + kind);
        icon.setAttribute("aria-hidden", "true");
        icon.textContent = kind === "person" ? "P" : kind === "desktop" ? "D" : "S";
        li.appendChild(icon);
        var text = node("span", "wp-item-text");
        text.appendChild(node("strong", null, title));
        if (meta) text.appendChild(node("span", "muted", meta));
        li.appendChild(text);
        if (assignmentId && releaseForm) {
            var b = node("button", "btn--sm", "Release");
            b.type = "button";
            b.setAttribute("data-release", String(assignmentId));
            b.setAttribute("aria-label", "Release " + title);
            b.addEventListener("click", function () { release(assignmentId, workspotId); });
            li.appendChild(b);
        }
        return li;
    }

    function empty(text) { return node("li", "wp-item wp-item-empty", text); }

    function renderSpot(d) {
        root.querySelector("[data-spot-status]").hidden = true;
        root.querySelector("[data-spot-body]").hidden = false;
        var kind = !d.isActive ? "Switched-off seat" : d.isDesk ? "Desk" : "Seat, not a desk: nobody is placed here";
        root.querySelector("[data-spot-kind]").textContent = kind + (d.place ? " · " + d.place : "") + ".";
        var items = root.querySelector("[data-spot-items]");
        items.textContent = "";
        if (d.person) {
            items.appendChild(item("person", d.person.name, [d.person.isActive ? null : "No longer works here", d.person.jobTitle, d.person.siteKey].filter(Boolean).join(" · "),
                d.person.personId, d.person.assignmentId, d.workspotId));
        } else if (d.isDesk) {
            items.appendChild(empty("Nobody sits here: free desk."));
        }
        if (d.desktop) items.appendChild(item("desktop", d.desktop.tag, ["Desktop", d.desktop.model].filter(Boolean).join(" · "), d.desktop.assetId, d.desktop.assignmentId, d.workspotId));
        else if (d.isDesk) items.appendChild(empty("No desktop."));
        d.screens.forEach(function (sc) { items.appendChild(item("screen", sc.tag, ["Screen", sc.model].filter(Boolean).join(" · "), sc.assetId, sc.assignmentId, d.workspotId)); });
        if (d.isDesk && d.screens.length === 0) items.appendChild(empty("No screen."));
        var flags = root.querySelector("[data-spot-flags]");
        flags.textContent = "";
        d.flags.forEach(function (f) { flags.appendChild(node("li", null, f)); });
    }

    function loadSpot(seat) {
        if (!spotHolder) return;
        var status = root.querySelector("[data-spot-status]");
        var token = ++spotToken;
        status.hidden = false;
        status.textContent = "Loading…";
        root.querySelector("[data-spot-body]").hidden = true;
        fetch(spotHolder.getAttribute("data-spot-url") + "&workspotId=" + encodeURIComponent(seat.getAttribute("data-ws")), {
            headers: { Accept: "application/json" }, credentials: "same-origin"
        }).then(function (res) {
            if (!res.ok) throw new Error("spot failed");
            return res.json();
        }).then(function (d) {
            if (token === spotToken) renderSpot(d);
        }).catch(function () {
            if (token === spotToken) status.textContent = "Could not load what is at this seat.";
        });
    }

    // ---- placing and releasing (fetch, then swap in the fresh map) ----------------------------------------------

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

    function place(seat, kind, id) {
        if (!assignForm) return;
        var ws = seat.getAttribute("data-ws");
        post(assignForm, { workspotId: ws, kind: kind, targetId: id })
            .then(function (body) { say(body.message); refresh(ws); })
            .catch(function (err) { say(err.message, true); });
    }

    function release(assignmentId, workspotId) {
        post(releaseForm, { assignmentId: assignmentId, workspotId: workspotId })
            .then(function (body) { say(body.message); refresh(String(workspotId)); })
            .catch(function (err) { say(err.message, true); });
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
            }
            var host = root.querySelector("[data-status-host]");
            if (host) host.innerHTML = parts[1];
            syncHighlightButtons();
            selected = [];
            setSelection(keep.map(function (id) { return svg.querySelector('[data-ws="' + esc(id) + '"]'); }).filter(Boolean));
            layoutLabels(svg, canvas, namesOn ? null : false);
            if (palette) palette.reload();
        }).catch(function () { window.location.reload(); });
    }

    // ---- the drag-and-drop list ---------------------------------------------------------------------------------

    var palette = null;
    var palRoot = root.querySelector("[data-palette]");
    if (palRoot) {
        var palKind = "person";
        var palInput = palRoot.querySelector("[data-palette-q]");
        var palList = palRoot.querySelector("[data-palette-list]");
        var palTimer = null, palToken = 0;
        var PAL_EMPTY = { person: "Everyone is placed.", desktop: "No desktops left to place.", screen: "No screens left to place." };

        var load = function () {
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
                    var icon = node("span", "wp-pal-icon wp-pal-icon--" + r.kind, r.kind === "person" ? "P" : r.kind === "desktop" ? "D" : "S");
                    icon.setAttribute("aria-hidden", "true");
                    b.appendChild(icon);
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
        load();
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
        if (!it || e.target.closest("[data-release]")) return;
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
        var b = boundsOf(list);
        var w = Math.max(b.w * 2.2, home.w / 8);
        var h = Math.max(b.h * 2.2, home.h / 8);
        w = Math.max(w, h * home.w / home.h);
        setView(b.x + b.w / 2 - w / 2, b.y + b.h / 2 - w * home.h / home.w / 2, w);
    }

    function focusSpec(spec) {
        var i = spec.indexOf(":");
        if (i < 0) return;
        var kind = spec.slice(0, i), value = spec.slice(i + 1), q = null;
        if (kind === "ws") q = value.split(",").map(function (v) { return '[data-ws="' + esc(v) + '"]'; }).join(",");
        else if (kind === "room") q = '[data-ws][data-room="' + esc(value) + '"]';
        else if (kind === "team") q = '[data-ws][data-team="' + esc(value) + '"]';
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
        var team = e.target.closest("[data-show-team]");
        if (team) { focusSpec("team:" + team.getAttribute("data-show-team")); return; }
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
            if (box.getAttribute("data-layer") === "teams") canvas.classList.toggle("hide-teams", !box.checked);
            else { namesOn = box.checked; layoutLabels(svg, canvas, namesOn ? null : false); }
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
                var pad = Math.max(bb.w, bb.h) * 0.25 + home.w / 100;
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
            var w = Math.max(home.w / 80, Math.min(home.w * 1.5, gesture.view.w / factor));
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
        else setSelection(hit);
    }

    canvas.addEventListener("pointerup", finish);
    canvas.addEventListener("pointercancel", function (e) { delete pointers[e.pointerId]; gesture = null; band.hidden = true; });

    canvas.addEventListener("wheel", function (e) {
        e.preventDefault();
        var p = userPoint(e.clientX, e.clientY);
        zoomAt(e.deltaY < 0 ? 1.2 : 1 / 1.2, p.x, p.y);
    }, { passive: false });

    canvas.addEventListener("keydown", function (e) {
        var c = centre();
        if (e.key === "+" || e.key === "=") zoomAt(1.3, c.x, c.y);
        else if (e.key === "-") zoomAt(1 / 1.3, c.x, c.y);
        else if (e.key === "0") setView(home.x, home.y, home.w);
        else if (e.key === "Escape") {
            setSelection([]);
            clearHits();
            printMode = false;
            canvas.classList.remove("is-print-area");
        }
    });

    all("[data-zoom]").forEach(function (b) {
        b.addEventListener("click", function () {
            var c = centre(), what = b.getAttribute("data-zoom");
            if (what === "in") zoomAt(1.4, c.x, c.y);
            else if (what === "out") zoomAt(1 / 1.4, c.x, c.y);
            else setView(home.x, home.y, home.w);
        });
    });

    var area = root.querySelector("[data-area]");
    if (area) area.addEventListener("click", function () {
        areaMode = !areaMode;
        area.setAttribute("aria-pressed", areaMode ? "true" : "false");
        canvas.classList.toggle("is-area", areaMode);
    });

    window.addEventListener("resize", scheduleLabels);
    root.classList.add("is-live");
    updatePanel();
    layoutLabels(svg, canvas, null);
    var initial = (root.getAttribute("data-focus") || "").split(",").filter(Boolean)
        .map(function (id) { return svg.getElementById(id); }).filter(Boolean);
    if (initial.length) focusElements(initial);
})();

// Floor plan print page: names and tags are always shown at a readable size; the print dialog opens by itself when the
// floor page asked for it, or with the button. "Save as PDF" in that dialog makes the PDF.
(function () {
    var sheet = document.querySelector("[data-print-sheet]");
    if (!sheet) return;
    var map = sheet.querySelector("[data-print-map]");
    var svg = map && map.querySelector("svg");
    function layout() { if (svg) layoutLabels(svg, map, true); }
    layout();
    window.addEventListener("resize", layout);
    window.addEventListener("beforeprint", layout);
    var button = document.querySelector("[data-print-now]");
    if (button) button.addEventListener("click", function () { window.print(); });
    if (sheet.getAttribute("data-autoprint") === "true") {
        var go = function () { setTimeout(function () { layout(); window.print(); }, 300); };
        if (document.fonts && document.fonts.ready) document.fonts.ready.then(go); else go();
    }
})();
