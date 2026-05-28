// One-shot ESC handlers for Blazor-controlled modal sheets. Each call binds an element to a .NET ref;
// pressing Escape (or focusing out of the sheet) calls OnEscape on the .NET side. Unbind removes the
// listener so circuits don't leak handlers. See AuditDetailSheet.razor.
(function () {
    var map = new WeakMap();

    window.musterDialogEscape = {
        bind: function (element, dotNetRef) {
            if (!element) return;
            var onKey = function (e) {
                if (e.key === "Escape") {
                    e.preventDefault();
                    dotNetRef.invokeMethodAsync("OnEscape");
                }
            };
            map.set(element, onKey);
            document.addEventListener("keydown", onKey);
            // Move focus into the sheet so ESC + keyboard scroll work without a click.
            try { element.focus(); } catch (_) { /* ignore */ }
        },
        unbind: function (element) {
            if (!element) return;
            var onKey = map.get(element);
            if (onKey) {
                document.removeEventListener("keydown", onKey);
                map.delete(element);
            }
        }
    };
})();
