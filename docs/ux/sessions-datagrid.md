# Sessions datagrid — UX notes

Status: implemented on the Sessions page (`/guilds/{id}/sessions`). Interactive (Blazor
`InteractiveServer`). This note records the layout + tab design decisions so future grids can copy them.

## Goals

- One cohesive **frame** that owns the whole working area, not a stack of separate panels.
- A **datagrid** with sortable headers, paging, filtering, and live updates.
- **Icon action buttons** colour-themed by purpose.
- The frame **fills the page** (full width, remaining viewport height); only the grid body scrolls.
- A **tab strip that hugs the top of the frame** and reads as part of it — not a detached pill row floating above.

## Frame fills the page

- The page's single bordered panel (`.panel.glass.datagrid`) is a flex column.
- On desktop, the content column (`.app-main`) that *contains* a `.datagrid` drops its `max-width`
  cap and takes a fixed `calc(100dvh - shell padding)` height, becoming a flex column itself. Scoped
  with `.app-main:has(.datagrid)` so no other page is affected.
- Inside the frame: the tab strip and (for the sessions view) the toolbar and footer are fixed-height;
  the grid body (`.dg-scroll`) is `flex: 1; min-height: 0; overflow: auto`, so only the rows scroll and
  the column headers stay put (sticky `thead`).
- Disabled on mobile (`< 861px`), where the layout is a single scrolling column with a bottom tab bar.

## Reusable component

The tab chrome is the shared `TabFrame` + `TabPane` pair (`Components/Shared/`). You declare a `TabPane`
per tab with `Id`/`Label`/optional `Icon` and its body as content; `TabFrame` builds the strip from the
registered panes and renders only the active pane's body, so inactive tabs cost nothing:

```razor
<TabFrame ActiveId="@CurrentTab" ActiveIdChanged="OnTab">
    <TabPane Id="sessions" Label="Sessions" Icon="schedule"> … </TabPane>
    <TabPane Id="leaderboard" Label="Leaderboard" Icon="leaderboard"> … </TabPane>
    @if (canSeeBackground) { <TabPane Id="background" Label="Background" Icon="sensors"> … </TabPane> }
</TabFrame>
```

The parent owns the active id (bindable via `@bind-ActiveId`; the Sessions page binds it one-way from the
URL and navigates in `ActiveIdChanged`). A null/unknown id falls back to the first pane. `Fill="true"`
(default) makes the frame claim the viewport height and scroll its body. CSS lives under "Reusable tab
frame" in `app.css` (`.tabframe*`); the datagrid content classes (`.dg-*`) stay page-specific.

## Tab design — "attached" tabs

The previous design used free-floating pill tabs (`.pill-tabs`) sitting above the panel with a gap, so
they read as a separate control. The new strip is **inside the frame, on its top edge**:

- The tab row sits flush at the top of the frame with a 1px bottom border that doubles as the seam
  between the tabs and the body — the divider line is shared, so the tabs visually belong to the frame.
- The **active** tab is lifted out of the muted state: it takes the body's surface tint and grows a 2px
  accent bar along its bottom edge that overlaps the seam. The effect is an underline/folder hybrid —
  the active tab "opens into" the panel below it.
- Inactive tabs are `--text-muted`, no background; hover brings them to `--text`.
- Each tab carries a Material Symbols icon + label (Sessions / Leaderboard / Background).

Rationale: a strip that shares the frame's border and bleeds its active state into the body removes the
"two separate things" feeling. It scales to more tabs and matches editor/devtools tab conventions users
already know.

## Icon action buttons

Buttons are colour-coded by intent using the shared `.icon-btn` system:

| Action          | Icon            | Variant            | Why                          |
|-----------------|-----------------|--------------------|------------------------------|
| View session    | `visibility`    | neutral            | non-destructive navigation   |
| End session     | `stop_circle`   | `.danger` (red)    | destructive, staff-only      |
| Start session   | `add_circle`    | `.btn` accent      | primary create action        |
| Export CSV      | `download`      | `.btn-ghost`       | secondary utility            |
| Refresh (bg)    | `refresh`       | neutral            | idempotent reload            |

Row actions are icon-only with `title`/`aria-label` for accessibility; toolbar actions keep a text label
beside the icon because they're less frequent and benefit from the affordance.

`Start session` lives in the grid toolbar next to `Export`, not in the page header — both are
grid-scoped actions, so they belong on the grid's toolbar.

## Toolbar (sessions view)

Left cluster: status segmented control (**Active** / **History** / **All**, default Active, staff-only),
name search, source filter, in-flight spinner. Right cluster: `Start session`, `Export CSV` (staff-only).

## URL state

Tab, status, search, source, sort, direction, page, and page size all live in the query string so any
view is shareable and back-button friendly. Defaults are omitted to keep links clean.
