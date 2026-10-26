// Confirmation prompts for forms marked data-confirm="...". Kept in an external file because the
// Content-Security-Policy (script-src 'self') blocks inline event handlers such as onsubmit="...".
document.addEventListener("submit", function (e) {
    var form = e.target;
    if (form instanceof HTMLFormElement && form.dataset.confirm && !window.confirm(form.dataset.confirm)) {
        e.preventDefault();
    }
});
