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
