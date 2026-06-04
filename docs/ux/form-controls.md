# Form controls — UX standards

Shared conventions for admin/settings inputs. Copy these for new pages so dropdowns and
status feedback stay consistent.

## Channel dropdowns

Any picker over guild channels (card channel, allow-list, post target):

- **Group by type, alphabetical within group.** Render each kind under an `<optgroup label="Text">`
  / `<optgroup label="Voice">`. Text group comes first, voice second; entries inside a group sort by name.
- Source from `GuildChannelOptions` — `ChannelOption(Id, Name, Kind)` already arrives Text-then-Voice,
  alphabetical within each, so `GroupBy(c => c.Kind)` preserves the right order.
- **Prefix by kind**: text channels with `#`, voice channels with the speaker glyph `🔊 ` (native `<option>` can't
  carry a Material icon, so use the unicode glyph). `kind == "Voice" ? "🔊 " : "#"`.

```razor
@foreach (var g in _chat.GroupBy(c => c.Kind))
{
    <optgroup label="@g.Key">
        @foreach (var c in g) { <option value="@c.Id">#@c.Name</option> }
    </optgroup>
}
```

## Currency / coin dropdowns

- Show **code + full name**: `@c.Code — @c.Name` (e.g. `RSI — Republic Standard Issue`).
- Only list spendable currencies for coin-reward pickers (`.Where(c => c.IsSpendable)`).
- Native `<option>` can't render a bold symbol; if a bolded symbol is ever required, a custom
  dropdown component is needed. Code-then-name is the standard for native selects.

## "Not assigned" numeric defaults

For reward fields where `0` means "no reward", surface 0 as empty so the placeholder shows:

- Back the input with a nullable (`long?`) and `placeholder="Not assigned"`.
- On load, map a stored `0` → `null` (shows placeholder).
- On save, map `null` → `0`.

So both 0 and null present identically as "not assigned", and the field never shows a bare `0`.

## Autosave status pill

Debounced-autosave pages show a colored status pill (no Save button), not grey text:

- States: `Saving…` (`sync` icon), `Saved` (`check_circle`), `Error` (`error` + message).
- Classes `.save-pill` + `.is-saving` / `.is-saved` / `.is-error` (in `app.css`), with a `save-pop`
  entrance animation. Hidden when idle (`SaveStatus.None`).

## Scrollbars

- Custom themed scrollbars (`app.css`, `::-webkit-scrollbar` + Firefox `scrollbar-*`) are a
  **desktop concern only**. Width 14px, thumb `--text-muted` → `--accent` on hover.
- **Mobile/touch browsers ignore them.** iOS Safari and Android Chrome use native *transient overlay*
  scrollbars that fade in only while scrolling and can't be width/color styled via `::-webkit-scrollbar`.
  Don't rely on a visible scrollbar as a scroll affordance on mobile — content overflow + momentum
  scrolling is the cue there.
