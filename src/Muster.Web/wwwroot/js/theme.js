// Theme management for Muster. Static SSR (no Blazor interactivity), so this is plain JS:
// persists the choice to localStorage and reflects it via the <html data-theme> attribute.
(function () {
    var KEY = "muster-theme";

    function apply(theme) {
        var root = document.documentElement;
        if (theme === "light" || theme === "dark") {
            root.setAttribute("data-theme", theme);
        } else {
            root.removeAttribute("data-theme"); // "system"
        }
    }

    function current() {
        return localStorage.getItem(KEY) || "system";
    }

    function syncButtons() {
        var active = current();
        document.querySelectorAll("[data-theme-option]").forEach(function (btn) {
            btn.setAttribute("aria-pressed", btn.getAttribute("data-theme-option") === active ? "true" : "false");
        });
    }

    window.musterSetTheme = function (theme) {
        localStorage.setItem(KEY, theme);
        apply(theme);
        syncButtons();
    };

    function reinit() {
        apply(current());
        syncButtons();
    }

    // Re-apply after Blazor enhanced navigation (the DOMContentLoaded handler won't refire).
    window.musterReinitTheme = reinit;

    document.addEventListener("DOMContentLoaded", reinit);
})();
