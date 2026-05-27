// Currency connector form: upgrade any [data-json-editor] textarea to a CodeMirror JSON editor
// (static SSR — full page loads, progressive enhancement). Syncs back to the underlying textarea on
// submit so model binding still gets the value. Falls back to a plain textarea if CodeMirror is absent.
(function () {
    function initJsonEditors() {
        if (!window.CodeMirror) {
            return;
        }

        document.querySelectorAll("textarea[data-json-editor]").forEach(function (el) {
            if (el.dataset.cmReady) {
                return;
            }
            el.dataset.cmReady = "1";

            var cm = window.CodeMirror.fromTextArea(el, {
                mode: { name: "javascript", json: true },
                lineNumbers: true,
                lineWrapping: true,
                tabSize: 2,
                viewportMargin: Infinity, // grow to content instead of a tiny fixed box
            });
            cm.setSize(null, 180);

            var form = el.closest("form");
            if (form) {
                form.addEventListener("submit", function () { cm.save(); });
            }
        });
    }

    // Re-init after Blazor enhanced navigation (idempotent — guarded by data-cmReady).
    window.musterReinitConnectorForm = initJsonEditors;

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", initJsonEditors);
    } else {
        initJsonEditors();
    }
})();
