// Applies the saved theme (light / dark / system) and the collapsed-sidebar choice before the first paint, so the page
// never flashes the wrong colours. Loaded without "defer" in <head>; an external file because the CSP blocks inline
// scripts. The switch itself lives in site.js. Without JavaScript the page simply stays in the light theme.
(function () {
    var root = document.documentElement;
    var pref = "system", rail = "";
    try {
        pref = localStorage.getItem("ats-theme") || "system";
        rail = localStorage.getItem("ats-rail") || "";
    } catch (e) { /* storage blocked: fall back to the system theme */ }
    var dark = pref === "dark" || (pref === "system" && window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches);
    root.setAttribute("data-theme", dark ? "dark" : "light");
    root.setAttribute("data-theme-pref", pref);
    if (rail === "collapsed") root.setAttribute("data-rail", "collapsed");
})();
