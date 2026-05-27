// Quest post/edit form enhancements (static SSR — full page loads, plain JS).
//  1. EasyMDE markdown editor on any [data-markdown-editor] textarea.
//  2. Show/hide guild-only vs personal-only fields based on the quest-kind select.
(function () {
    function initMarkdownEditors() {
        if (!window.EasyMDE) {
            return;
        }

        document.querySelectorAll("textarea[data-markdown-editor]").forEach(function (el) {
            if (el.dataset.mdeReady) {
                return;
            }
            el.dataset.mdeReady = "1";

            var mde = new EasyMDE({
                element: el,
                spellChecker: false,
                status: false,
                autoDownloadFontAwesome: true,
                placeholder: el.getAttribute("placeholder") || "Describe the quest… (markdown supported)",
                minHeight: "140px",
                toolbar: ["bold", "italic", "strikethrough", "heading", "|",
                          "quote", "unordered-list", "ordered-list", "|",
                          "link", "code", "|", "preview", "guide"],
            });

            // Static SSR posts the form normally — sync the editor's value into the
            // underlying textarea right before submit so model binding gets the markdown.
            var form = el.closest("form");
            if (form) {
                form.addEventListener("submit", function () { el.value = mde.value(); });
            }
        });
    }

    function initKindToggle() {
        var hidden = document.getElementById("quest-kind-value");
        if (!hidden) {
            return;
        }

        var tabs = document.querySelectorAll(".kind-tab");

        function apply(kind) {
            hidden.value = kind;
            var isGuild = kind === "guild";
            document.querySelectorAll(".guild-only").forEach(function (e) { e.classList.toggle("is-hidden", !isGuild); });
            document.querySelectorAll(".personal-only").forEach(function (e) { e.classList.toggle("is-hidden", isGuild); });
            tabs.forEach(function (t) { t.classList.toggle("active", t.dataset.kind === kind); });
        }

        tabs.forEach(function (t) {
            t.addEventListener("click", function () { apply(t.dataset.kind); });
        });

        apply(hidden.value || "personal");
    }

    function init() {
        initMarkdownEditors();
        initKindToggle();
    }

    // Re-init after Blazor enhanced navigation (idempotent — guarded by data-mdeReady).
    window.musterReinitQuestForm = init;

    if (document.readyState !== "loading") {
        init();
    } else {
        document.addEventListener("DOMContentLoaded", init);
    }
})();
