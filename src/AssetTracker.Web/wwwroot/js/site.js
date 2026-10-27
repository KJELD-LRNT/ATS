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

// Inputs sharing a data-sync key mirror each other (checkout: the search form's quantity and the confirmed quantity),
// so the number on screen is always the number that gets submitted.
["input", "change"].forEach(function (type) {
    document.addEventListener(type, function (e) {
        var source = e.target instanceof HTMLInputElement ? e.target : null;
        var key = source && source.getAttribute("data-sync");
        if (!key) return;
        document.querySelectorAll("input[data-sync]").forEach(function (other) {
            if (other !== source && other.getAttribute("data-sync") === key && other.value !== source.value) {
                other.value = source.value;
            }
        });
    });
});

// Segmented control (checkout: tagged asset vs pool). Shows one panel and disables the other's fields so they are
// not submitted. Without JavaScript the control stays hidden and both panels are shown.
(function () {
    var group = document.querySelector("[data-segmented]");
    if (!group) return;
    var form = group.closest("form");
    var options = Array.prototype.slice.call(group.querySelectorAll("[data-segment]"));

    function select(name, focus) {
        options.forEach(function (o) {
            var on = o.getAttribute("data-segment") === name;
            o.setAttribute("aria-checked", on ? "true" : "false");
            o.tabIndex = on ? 0 : -1;
            if (on && focus) o.focus();
        });
        form.querySelectorAll("[data-segment-panel]").forEach(function (panel) {
            var on = panel.getAttribute("data-segment-panel") === name;
            panel.hidden = !on;
            panel.querySelectorAll("input, select, button").forEach(function (c) { c.disabled = !on; });
        });
        form.querySelectorAll("[data-segment-or]").forEach(function (el) { el.hidden = true; });
    }

    options.forEach(function (o, i) {
        o.addEventListener("click", function () { select(o.getAttribute("data-segment"), false); });
        o.addEventListener("keydown", function (e) {
            if (e.key !== "ArrowLeft" && e.key !== "ArrowRight" && e.key !== "ArrowUp" && e.key !== "ArrowDown") return;
            e.preventDefault();
            var next = options[(i + (e.key === "ArrowLeft" || e.key === "ArrowUp" ? options.length - 1 : 1)) % options.length];
            select(next.getAttribute("data-segment"), true);
        });
    });

    group.hidden = false;
    var initial = options.filter(function (o) { return o.getAttribute("aria-checked") === "true"; })[0] || options[0];
    select(initial.getAttribute("data-segment"), false);
})();

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

// People typeahead (checkout, "Who receives it?"). While typing, /Loans/Checkout?handler=People returns matching people
// with their site; picking one fills the hidden holderId so the confirm step has that person selected. Plain ARIA
// combobox: arrows move, Enter picks, Escape closes. Without JavaScript the text is searched on "Find" as before.
(function () {
    var root = document.querySelector("[data-people-lookup]");
    if (!root) return;
    var input = root.querySelector("input[role=combobox]");
    var idField = root.querySelector("[data-people-id]");
    var list = root.querySelector("[role=listbox]");
    var selected = root.querySelector("[data-people-selected]");
    var url = root.getAttribute("data-url");
    var min = Number(root.getAttribute("data-min") || 2);
    var timer = null, controller = null, people = [], active = -1;

    function make(tag, className, text) {
        var node = document.createElement(tag);
        if (className) node.className = className;
        if (text !== undefined) node.textContent = text;
        return node;
    }

    function initials(name) {
        var parts = name.trim().split(/\s+/).filter(Boolean);
        return ((parts[0] || "?")[0] + (parts.length > 1 ? parts[parts.length - 1][0] : "")).toUpperCase();
    }

    function siteBadge(p) {
        return p.siteKey ? make("span", "badge badge--info", p.siteKey) : make("span", "badge badge--muted", "No site");
    }

    function close() {
        list.hidden = true;
        input.setAttribute("aria-expanded", "false");
        input.removeAttribute("aria-activedescendant");
        active = -1;
    }

    function setActive(index) {
        var options = list.querySelectorAll("[role=option]");
        if (options.length === 0) return;
        active = (index + options.length) % options.length;
        Array.prototype.forEach.call(options, function (o, i) { o.setAttribute("aria-selected", i === active ? "true" : "false"); });
        input.setAttribute("aria-activedescendant", options[active].id);
        options[active].scrollIntoView({ block: "nearest" });
    }

    function showSelected(p) {
        selected.textContent = "";
        selected.appendChild(make("span", null, "Selected:"));
        selected.appendChild(make("strong", null, p.name));
        selected.appendChild(siteBadge(p));
        if (p.siteName) selected.appendChild(make("span", null, p.siteName));
        selected.hidden = false;
    }

    function clearSelection() {
        idField.value = "";
        selected.hidden = true;
        selected.textContent = "";
    }

    function choose(p) {
        input.value = p.name;
        idField.value = String(p.id);
        showSelected(p);
        close();
    }

    function render() {
        list.textContent = "";
        active = -1;
        if (people.length === 0) {
            var none = make("li", "combo-empty", "No matching people who can receive a loan.");
            none.setAttribute("role", "presentation");
            list.appendChild(none);
        }
        people.forEach(function (p, i) {
            var option = make("li", "combo-option");
            option.id = "who-option-" + i;
            option.setAttribute("role", "option");
            option.setAttribute("aria-selected", "false");

            var text = make("span", "picker-text");
            var name = make("span", "combo-name");
            name.appendChild(document.createTextNode(p.name + " "));
            name.appendChild(siteBadge(p));
            text.appendChild(name);
            var meta = [p.username, p.email, p.siteName, p.jobTitle].filter(Boolean).join(" · ");
            text.appendChild(make("span", "combo-meta", meta));

            option.appendChild(make("span", "avatar", initials(p.name)));
            option.appendChild(text);
            // mousedown, not click: the input would lose focus (and close the list) before a click lands.
            option.addEventListener("mousedown", function (e) { e.preventDefault(); choose(p); });
            list.appendChild(option);
        });
        list.hidden = false;
        input.setAttribute("aria-expanded", "true");
    }

    function search() {
        var q = input.value.trim();
        if (q.length < min) { close(); return; }
        if (controller) controller.abort();
        controller = typeof AbortController === "function" ? new AbortController() : null;
        fetch(url + "&q=" + encodeURIComponent(q), {
            headers: { Accept: "application/json" },
            credentials: "same-origin",
            signal: controller ? controller.signal : undefined
        }).then(function (res) {
            if (!res.ok) throw new Error("people search failed");
            return res.json();
        }).then(function (found) {
            // Ignore an answer to text that has changed meanwhile.
            if (input.value.trim() !== q) return;
            people = found;
            render();
        }).catch(function (err) {
            if (err && err.name === "AbortError") return;
            close(); // the Find button still searches the typed text
        });
    }

    input.addEventListener("input", function () {
        if (idField.value) clearSelection();
        clearTimeout(timer);
        timer = setTimeout(search, 200);
    });

    input.addEventListener("keydown", function (e) {
        if (e.key === "ArrowDown" || e.key === "ArrowUp") {
            if (list.hidden) { if (people.length) render(); else return; }
            e.preventDefault();
            setActive(active < 0 ? (e.key === "ArrowDown" ? 0 : -1) : active + (e.key === "ArrowDown" ? 1 : -1));
        } else if (e.key === "Enter" && !list.hidden && active >= 0) {
            e.preventDefault();
            choose(people[active]);
        } else if (e.key === "Escape" && !list.hidden) {
            e.preventDefault();
            close();
        }
    });

    input.addEventListener("blur", close);
    // A person chosen earlier (we came back with holderId) keeps their id until the text is edited.
})();
