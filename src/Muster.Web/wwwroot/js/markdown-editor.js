// Discord-flavored markdown toolbar for a plain <textarea>. Each action wraps the current selection (or inserts a
// placeholder) with the matching Discord syntax, then dispatches an 'input' event so Blazor's binding picks up the
// new value. Keeps the textarea as the source of truth — no DOM takeover, so @bind keeps working.

const FORMATS = {
    bold:      { pre: "**",    suf: "**",      ph: "bold text" },
    italic:    { pre: "*",     suf: "*",       ph: "italic text" },
    underline: { pre: "__",    suf: "__",      ph: "underlined text" },
    strike:    { pre: "~~",    suf: "~~",      ph: "strikethrough" },
    spoiler:   { pre: "||",    suf: "||",      ph: "spoiler" },
    code:      { pre: "`",     suf: "`",       ph: "code" },
    codeblock: { pre: "```\n", suf: "\n```",   ph: "code" },
    link:      { pre: "[",     suf: "](https://)", ph: "text" },
    quote:     { line: "> ",   ph: "quote" },
    bullet:    { line: "- ",   ph: "list item" },
    number:    { line: "1. ",  ph: "list item" },
};

export function apply(ta, kind) {
    const f = FORMATS[kind];
    if (!ta || !f) {
        return;
    }

    const start = ta.selectionStart ?? ta.value.length;
    const end = ta.selectionEnd ?? start;
    const val = ta.value;
    const selected = val.substring(start, end);

    let insert, selStart, selEnd;
    if (f.line) {
        // Prefix each line in the selection (or a single placeholder line).
        const text = selected || f.ph;
        insert = text.split("\n").map((l) => f.line + l).join("\n");
        selStart = start;
        selEnd = start + insert.length;
    } else {
        const text = selected || f.ph;
        insert = f.pre + text + f.suf;
        selStart = start + f.pre.length;
        selEnd = selStart + text.length;
    }

    ta.value = val.substring(0, start) + insert + val.substring(end);
    ta.dispatchEvent(new Event("input", { bubbles: true }));
    ta.focus();
    ta.setSelectionRange(selStart, selEnd);
}
