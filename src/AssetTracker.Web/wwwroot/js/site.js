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
// Workplace: find a workspot, room or team (any floor). Picking a result highlights it when it is on the floor that is
// open, otherwise opens its floor with the seats highlighted (?focus=ws:ID | room:ID | team:ID).
(function () {
    Array.prototype.forEach.call(document.querySelectorAll("[data-workplace-find]"), function (root) {
        var input = root.querySelector("input[role=combobox]");
        var url = root.getAttribute("data-url");
        var floorUrl = root.getAttribute("data-floor-url");
        var KINDS = { workspot: "Seat", room: "Room", team: "Team", person: "Person", asset: "Asset", pool: "Pool" };

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

// Workplace floor map: pan (drag), zoom (wheel, pinch, buttons), select seats (click, Shift-click, table or room click,
// Shift-drag or "Select area"), highlight search results and teams. The selection fills the hidden fields of the forms
// in the side panel; the server does everything else. Without JavaScript the map is a static picture.
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
    var selected = []; // elements, in the order they were selected

    function setView(x, y, w) {
        w = Math.max(home.w / 60, Math.min(home.w * 1.5, w));
        view = { x: x, y: y, w: w, h: w * home.h / home.w };
        svg.setAttribute("viewBox", [view.x, view.y, view.w, view.h].join(" "));
    }

    function zoomAt(factor, cx, cy) {
        var w = view.w / factor;
        var k = Math.max(home.w / 60, Math.min(home.w * 1.5, w)) / view.w;
        setView(cx - (cx - view.x) * k, cy - (cy - view.y) * k, view.w * k);
    }

    function userPoint(clientX, clientY) {
        var p = svg.createSVGPoint();
        p.x = clientX;
        p.y = clientY;
        return p.matrixTransform(svg.getScreenCTM().inverse());
    }

    function centre() { return { x: view.x + view.w / 2, y: view.y + view.h / 2 }; }

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

    function esc(s) { return (window.CSS && CSS.escape) ? CSS.escape(s) : s.replace(/"/g, '\\"'); }

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

    function show(selector, on) {
        Array.prototype.forEach.call(root.querySelectorAll(selector), function (el) { el.hidden = !on; });
    }

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

        Array.prototype.forEach.call(root.querySelectorAll('[data-ids="ws"]'), function (f) {
            f.value = s.map(function (x) { return x.getAttribute("data-ws"); }).join(",");
        });
        Array.prototype.forEach.call(root.querySelectorAll('[data-ids="el"]'), function (f) {
            f.value = e.map(function (x) { return x.id; }).join(",");
        });

        var summary = root.querySelector("[data-sel-summary]");
        if (summary) summary.textContent = s.length === 1 ? "1 workspot selected." : s.length + " workspots selected.";
        var list = root.querySelector("[data-sel-list]");
        if (list) {
            list.textContent = "";
            if (s.length > 1) s.slice(0, 12).forEach(function (x) {
                var li = document.createElement("li");
                li.textContent = x.getAttribute("data-label") || x.id;
                list.appendChild(li);
            });
            if (s.length > 12) { var more = document.createElement("li"); more.textContent = "… and " + (s.length - 12) + " more"; list.appendChild(more); }
        }
        var elSummary = root.querySelector("[data-el-summary]");
        if (elSummary) elSummary.textContent = e.length === 1 ? "1 shape selected." : e.length + " shapes selected.";

        if (s.length === 1) {
            var one = s[0];
            var fact = function (name, text) { var n = root.querySelector('[data-fact="' + name + '"]'); if (n) n.textContent = text || "–"; };
            fact("label", one.getAttribute("data-label"));
            var room = one.getAttribute("data-room");
            fact("room", one.getAttribute("data-room-name") ? one.getAttribute("data-room-name") + " (" + room + ")" : room);
            fact("table", one.getAttribute("data-table"));
            fact("team", one.hasAttribute("data-team") ? teamName(one.getAttribute("data-team")) : "");
            var idField = root.querySelector("[data-ws-id]");
            if (idField) idField.value = one.getAttribute("data-ws");
            var labelField = root.querySelector("[data-ws-label]");
            if (labelField) labelField.value = one.getAttribute("data-label") || "";
            loadSpot(one);
        }
    }

    // ---- highlight and zoom ----------------------------------------------------------------------------------

    function clearHits() {
        Array.prototype.forEach.call(svg.querySelectorAll(".is-hit"), function (el) { el.classList.remove("is-hit"); });
    }

    function focusElements(list) {
        clearHits();
        if (list.length === 0) return;
        list.forEach(function (el) { el.classList.add("is-hit"); });
        setSelection(list);
        var minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        list.forEach(function (el) {
            var b = el.getBBox();
            minX = Math.min(minX, b.x); minY = Math.min(minY, b.y);
            maxX = Math.max(maxX, b.x + b.width); maxY = Math.max(maxY, b.y + b.height);
        });
        var w = Math.max((maxX - minX) * 2.2, home.w / 8);
        var h = Math.max((maxY - minY) * 2.2, home.h / 8);
        w = Math.max(w, h * home.w / home.h);
        setView((minX + maxX) / 2 - w / 2, (minY + maxY) / 2 - w * home.h / home.w / 2, w);
    }

    function focusSpec(spec) {
        var i = spec.indexOf(":");
        if (i < 0) return;
        var kind = spec.slice(0, i), value = spec.slice(i + 1), q = null;
        if (kind === "ws") q = '[data-ws="' + esc(value) + '"]';
        else if (kind === "room") q = '[data-ws][data-room="' + esc(value) + '"]';
        else if (kind === "team") q = '[data-ws][data-team="' + esc(value) + '"]';
        if (q) focusElements(Array.prototype.slice.call(svg.querySelectorAll(q)));
    }

    root.addEventListener("workplace:focus", function (e) { focusSpec(String(e.detail)); });

    Array.prototype.forEach.call(root.querySelectorAll("[data-show-team]"), function (b) {
        b.addEventListener("click", function () { focusSpec("team:" + b.getAttribute("data-show-team")); });
    });

    // ---- pointer handling: pan, pinch, click, area -----------------------------------------------------------

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
            type: (areaMode || e.shiftKey) ? "box" : "pan", target: e.target, startX: e.clientX, startY: e.clientY,
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
            var w = Math.max(home.w / 60, Math.min(home.w * 1.5, gesture.view.w / factor));
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
            var x1 = Math.min(gesture.startX, e.clientX) - r.left + canvas.scrollLeft, y1 = Math.min(gesture.startY, e.clientY) - r.top + canvas.scrollTop;
            band.hidden = false;
            band.style.left = x1 + "px";
            band.style.top = y1 + "px";
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
            var box = {
                left: Math.min(g.startX, e.clientX), right: Math.max(g.startX, e.clientX),
                top: Math.min(g.startY, e.clientY), bottom: Math.max(g.startY, e.clientY)
            };
            var found = Array.prototype.slice.call(svg.querySelectorAll("[data-ws]")).filter(function (el) { return inside(box, el, false); });
            if (!declared && editable && found.length === 0) {
                found = Array.prototype.slice.call(svg.querySelectorAll("rect[id],circle[id],ellipse[id],path[id],polygon[id]"))
                    .filter(function (el) { return inside(box, el, true); });
            }
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
        else if (e.key === "Escape") { setSelection([]); clearHits(); }
    });

    Array.prototype.forEach.call(root.querySelectorAll("[data-zoom]"), function (b) {
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

    // ---- what sits at the selected workspot, and placing things there ----------------------------------------

    var spotHolder = root.querySelector("[data-spot-url]");
    var spotToken = 0;
    var releaseForm = document.getElementById("wp-release-form");
    var assignForm = document.getElementById("wp-assign-form");
    var place = root.querySelector("[data-place]");
    var pending = null;

    function node(tag, cls, text) {
        var n = document.createElement(tag);
        if (cls) n.className = cls;
        if (text !== undefined) n.textContent = text;
        return n;
    }

    function releaseButton(assignmentId, workspotId, label) {
        if (!releaseForm) return null;
        var b = node("button", "btn--sm", "Release");
        b.type = "button";
        b.setAttribute("aria-label", "Release " + label);
        b.addEventListener("click", function () {
            releaseForm.querySelector("[data-release=assignmentId]").value = String(assignmentId);
            releaseForm.querySelector("[data-release=workspotId]").value = String(workspotId);
            releaseForm.submit();
        });
        return b;
    }

    function renderSpot(d) {
        root.querySelector("[data-spot-status]").hidden = true;
        root.querySelector("[data-spot-body]").hidden = false;

        var person = root.querySelector("[data-spot-person]");
        person.textContent = "";
        var row = node("div", "wp-item");
        if (d.person) {
            var text = node("span", "wp-item-text");
            var name = node("strong", null, d.person.name);
            text.appendChild(name);
            if (!d.person.isActive) text.appendChild(node("span", "badge badge--muted", "Left"));
            text.appendChild(node("span", "muted", [d.person.username, d.person.jobTitle, d.person.siteKey].filter(Boolean).join(" · ")));
            row.appendChild(text);
            var rb = releaseButton(d.person.assignmentId, d.workspotId, d.person.name);
            if (rb) row.appendChild(rb);
        } else {
            row.appendChild(node("span", "muted", "Nobody sits here."));
        }
        person.appendChild(row);

        function fill(listSelector, items, describe) {
            var ul = root.querySelector(listSelector);
            ul.textContent = "";
            items.forEach(function (it) {
                var li = node("li", "wp-item");
                var t = node("span", "wp-item-text");
                var parts = describe(it);
                t.appendChild(node("strong", null, parts[0]));
                t.appendChild(node("span", "muted", parts[1]));
                li.appendChild(t);
                var b = releaseButton(it.assignmentId, d.workspotId, parts[0]);
                if (b) li.appendChild(b);
                ul.appendChild(li);
            });
        }
        fill("[data-spot-assets]", d.assets, function (a) {
            return [a.tag, [a.type, a.model, a.description, a.state === "Available" || a.state === "CheckedOut" ? "" : a.state].filter(Boolean).join(" · ")];
        });
        fill("[data-spot-pools]", d.pools, function (p) { return [p.quantity + " × " + p.sku, [p.type, p.description].filter(Boolean).join(" · ")]; });

        var hints = root.querySelector("[data-spot-hints]");
        hints.textContent = "";
        d.hints.forEach(function (h) { hints.appendChild(node("li", null, h)); });
    }

    function loadSpot(seat) {
        if (!spotHolder) return;
        var status = root.querySelector("[data-spot-status]");
        var token = ++spotToken;
        status.hidden = false;
        status.textContent = "Loading…";
        root.querySelector("[data-spot-body]").hidden = true;
        if (place) {
            place.querySelector("input[role=combobox]").value = "";
            place.querySelector("[data-place-qty]").hidden = true;
            pending = null;
        }
        fetch(spotHolder.getAttribute("data-spot-url") + "&workspotId=" + encodeURIComponent(seat.getAttribute("data-ws")), {
            headers: { Accept: "application/json" }, credentials: "same-origin"
        }).then(function (res) {
            if (!res.ok) throw new Error("spot failed");
            return res.json();
        }).then(function (d) {
            if (token === spotToken) renderSpot(d);
        }).catch(function () {
            if (token === spotToken) status.textContent = "Could not load what is at this workspot.";
        });
    }

    function submitAssign(kind, id, quantity) {
        var one = seats()[0];
        if (!assignForm || !one) return;
        assignForm.querySelector("[data-assign=workspotId]").value = one.getAttribute("data-ws");
        assignForm.querySelector("[data-assign=kind]").value = kind;
        assignForm.querySelector("[data-assign=targetId]").value = String(id);
        assignForm.querySelector("[data-assign=quantity]").value = String(quantity || 1);
        assignForm.submit();
    }

    if (place) {
        var pInput = place.querySelector("input[role=combobox]");
        var qtyBox = place.querySelector("[data-place-qty]");
        var BADGE = { person: "Person", asset: "Asset", pool: "Pool" };
        var pcombo = createCombo({
            input: pInput, list: place.querySelector("[role=listbox]"), idPrefix: "wp-place-option-", min: 2,
            emptyText: "No matching people, assets or pools.",
            url: function (q) { return place.getAttribute("data-place-url") + "&q=" + encodeURIComponent(q); },
            pickable: function (row) { return !row.reason; },
            render: function (row, option) {
                var text = pcombo.make("span", "picker-text");
                var name = pcombo.make("span", "combo-name");
                name.appendChild(document.createTextNode(row.title + " "));
                name.appendChild(pcombo.make("span", "badge badge--info", BADGE[row.kind] || row.kind));
                text.appendChild(name);
                var meta = [row.subtitle, row.place ? "now at " + row.place : "", row.reason].filter(Boolean).join(" · ");
                text.appendChild(pcombo.make("span", "combo-meta", meta));
                option.appendChild(text);
            },
            choose: function (row) {
                if (row.kind === "pool") {
                    pending = row;
                    qtyBox.hidden = false;
                    place.querySelector("[data-place-pool]").textContent = row.title;
                    pInput.value = row.title;
                    place.querySelector("#wp-qty").focus();
                    return;
                }
                if (row.place && !window.confirm(row.title + " is now at " + row.place + ". Move it here?")) return;
                submitAssign(row.kind, row.id, 1);
            }
        });
        var confirmQty = place.querySelector("[data-place-confirm]");
        if (confirmQty) confirmQty.addEventListener("click", function () {
            if (pending) submitAssign("pool", pending.id, Number(place.querySelector("#wp-qty").value) || 1);
        });
    }

    var viewSelect = root.querySelector("[data-view]");
    if (viewSelect) viewSelect.addEventListener("change", function () {
        var occupancy = viewSelect.value === "occupancy";
        canvas.classList.toggle("view-occupancy", occupancy);
        Array.prototype.forEach.call(root.querySelectorAll('[data-legend="occupancy"]'), function (l) { l.hidden = !occupancy; });
    });

    Array.prototype.forEach.call(root.querySelectorAll("[data-show-spot]"), function (b) {
        b.addEventListener("click", function () { focusSpec("ws:" + b.getAttribute("data-show-spot")); });
    });

    root.classList.add("is-live");
    updatePanel();
    var initial = (root.getAttribute("data-focus") || "").split(",").filter(Boolean)
        .map(function (id) { return svg.getElementById(id); }).filter(Boolean);
    if (initial.length) focusElements(initial);
})();
