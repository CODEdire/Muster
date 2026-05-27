// Time zone helpers for the profile page. Static SSR (no Blazor interactivity), so this is plain JS
// loaded once and wired via inline onclick — mirrors theme.js. Fills the zone input from the
// browser's detected IANA zone and seeds a placeholder hint on load.
(function () {
    function detected() {
        try {
            return Intl.DateTimeFormat().resolvedOptions().timeZone || "";
        } catch (e) {
            return "";
        }
    }

    window.musterUseBrowserTimeZone = function () {
        var input = document.getElementById("tz-input");
        var zone = detected();
        if (input && zone) {
            input.value = zone;
        }
    };

    function seedHint() {
        var input = document.getElementById("tz-input");
        var zone = detected();
        if (input && zone && !input.value) {
            input.setAttribute("placeholder", zone + " (your browser)");
        }
    }

    // Re-seed after Blazor enhanced navigation (the DOMContentLoaded handler won't refire).
    window.musterReinitTz = seedHint;

    document.addEventListener("DOMContentLoaded", seedHint);
})();
