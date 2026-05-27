// Blazor static SSR uses "enhanced navigation": internal link clicks and enhanced form posts are
// fetched and DOM-patched instead of triggering a full page load (no SignalR circuit — plain HTTP).
// The side effect is that our progressive-enhancement scripts (theme, time zone, the EasyMDE markdown
// editor, the CodeMirror JSON editor) only run their DOMContentLoaded init on the FIRST load. After an
// enhanced navigation that handler never refires, so we re-run each one on Blazor's `enhancedload` event.
// Every re-init is idempotent (guarded), so calling them again is safe.
(function () {
    function reinit() {
        [
            window.musterReinitTheme,
            window.musterReinitTz,
            window.musterReinitQuestForm,
            window.musterReinitConnectorForm
        ].forEach(function (fn) {
            if (typeof fn === "function") {
                try { fn(); } catch (e) { console.error("enhanced reinit failed", e); }
            }
        });
    }

    // blazor.web.js defines the Blazor global synchronously, but guard with a short retry in case
    // this script runs before it finishes wiring up.
    function register() {
        if (window.Blazor && typeof window.Blazor.addEventListener === "function") {
            window.Blazor.addEventListener("enhancedload", reinit);
        } else {
            setTimeout(register, 50);
        }
    }

    register();
})();
