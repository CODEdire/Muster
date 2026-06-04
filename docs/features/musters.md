# Musters

A "muster" is a button check-in: post a card, members tap **Check In**, you track and reward participation.

## Posting & check-in
- Post a muster from Discord (`/muster post`) or the web admin.
- Members check in with a **Check-In button** (buttons only — no reactions); each click is acknowledged privately.
- Optional **title**, **prompt**, **reward**, **capacity** (hard cap), and **auto-close after N hours**.
- The **creator can be auto-checked-in** on create (guild default toggle, overridable per post) — handy for a host who's attending too.
- One check-in per member; ineligible members and a full/closed/expired muster are rejected with a reason.
- Card auto-updates in place (status, count, roster) and goes terminal when closed/expired.

## Max active time
- A muster can **auto-close after a max active time** so it doesn't go stale: set per-template, per-post, or as a **guild default** (`DefaultExpiryHours`, 0 = none); a template's own expiry wins, then the guild default.
- A **standalone** muster past its window **expires and pays out**. A **linked** muster instead **soft-closes (Locked)** — it stops taking check-ins (button disabled) but isn't terminal; it's paid + closed when its session ends.

## Pay vs review
- A standalone muster's **resolve mode** decides what auto-close does: **Pay** (close + pay immediately, the default) or **Review** (soft-close to a **pending** state — no check-ins, not paid).
- In **pending review** the owner/manager curates the roster, then **Approve & pay** (close) or **Discard** (cancel, pay nothing). A manager can also **Lock for review** any open muster manually; a normal **Close & pay** still finalizes immediately.
- Resolve mode resolves **per-muster → template → guild default**.

## Channels
- Posts to the guild's configured **default channel**, an **explicit per-post channel**, or the channel the command ran in.
- Optional **allowed-channel list** — empty = any chat channel (text or voice), set = restrict; pickers, autocomplete, and posting all honor it.

## Rewards
- A muster grants **Points** (participation) and optional **Coins** (a spendable currency), paid **at close** — not on check-in, so removing someone before close needs no reversal.
- Values resolve **template → custom → guild defaults**; both 0 = check-in tracking only.
- **Minimum check-ins to reward** — an optional gate: if fewer than N members check in, *nobody* is rewarded (the card shows progress toward it). Settable as a guild default, per template, or per muster; blank = no minimum.
- The card **prompt supports Discord markdown**, authored with a themed toolbar + live preview on the templates page.

## Templates
- Named presets (e.g. "Tactical Strike Group") set Points, Coins + coin type, retention, and optional capacity/expiry — so creators pick a type instead of dialing in rewards.
- A template can also carry a **default card title + prompt**, so a template-locked creator posts in one step (no text to type); the author's own title/prompt still override.
- Picking a template overrides the guild defaults; a **Tracking Manager** may further tweak its values per-post, a **Muster Creator** gets it locked.
- No template picked → the guild's global muster defaults apply.
- Managed on a dedicated page: a searchable/sortable grid with separate add/edit pages.

## Tracking-session integration
- Link a muster to one or more **tracking sessions** to gate the session's spendable **coin** (points are never gated).
- Gate modes: **None** (mint to all), **Any** (in any linked muster), **All** (in every linked muster).
- A linked muster's own reward is paid at **session close**, only to members who checked in **and** attended.
- Sessions can **auto-create** a check-in muster on open (guild default + per-session override), with a configurable default gate mode and a choice of **where the card posts** — the default muster channel, or the **session's own channel** (used only when the allow-list permits it, else it falls back to the default).
- Ending a session auto-closes its linked musters.

## Admin (web)
- Muster **board** (`/guild/musters`): participant **card view** of active musters with check-in/out. Staff also get **Manage** + **New** buttons.
- **Manage** grid (`/guild/musters/manage`): managers see all musters + KPIs; creators see only their own. Search, status filter, sortable, paged; row actions (edit/close) gated by ownership.
- **New** + **Edit** on their own pages (markdown prompt, template/custom reward, options); edit is owner/manager-only and live-muster-only.
- Muster **detail** (tabbed: Overview / Participants / Linked sessions), role-aware: participants get the card + a toolbar check-in/out; owners/managers get the management tabs. The **participant list is a live datagrid** (search/sort/page) that updates in place when a check-in lands from Discord, web, or API (via the MusterChanged fan-out). Adding a member uses a searchable participant picker. Session linking/coin-gate controls show only for managers.
- Settings page: default channel, card retention, max active time, reward defaults, creator auto-check-in, auto-create-on-session + gate, allowed channels.

## Lifecycle & housekeeping
- Multiple musters per session (e.g. event check-in + per-round musters).
- Background sweeps auto-expire due musters and prune terminal cards after a retention window (history kept in the web).
- `/muster summary` shows any muster's roster (incl. closed) as an ephemeral reply.

## Access & audit
- **Participant** (has the participant role): sees active musters (card view), checks **in/out** from web or the Discord button, sees the general card info.
- **Muster Creator**: a participant who can also **create** (from templates, reward-locked) and fully manage the musters **they own** — edit (title/prompt/capacity/auto-close, reward stays template-locked), close, curate roster, remove the card. They can't touch others' musters.
- **Tracking Manager** (+ Officer/Admin umbrellas): full lifecycle over **all** musters, custom rewards, and **session linking + coin gate**, plus settings/templates.
- **Session linking, coin-gate, settings, and templates are Tracking-Manager-only** (they affect session economics), even for a creator's own muster.
- Enforcement is at the command handler (ownership-aware `CanManageMuster`); every action is recorded to the audit log.

### Permission matrix

| Action | Participant | Creator — own | Creator — others' | Manager / Officer |
|---|:---:|:---:|:---:|:---:|
| See active musters + card info | ✓ | ✓ | ✓ | ✓ |
| Check **in/out** (self, while Open) | ✓ | ✓ | ✓ | ✓ |
| See full roster | – | ✓ | – | ✓ |
| Create (template-locked for creators) | – | ✓ | – | ✓ (custom too) |
| Edit (title/prompt/capacity/auto-close) | – | ✓ | – | ✓ |
| Edit reward (points/coins/min) | – | – | – | ✓ |
| Close / curate roster / remove card | – | ✓ | – | ✓ |
| Link session + set coin gate | – | – | – | ✓ |
| Settings / templates | – | – | – | ✓ |

"Own" = the muster's `CreatedBy`. Officer/Admin resolve as Manager-equivalent.
