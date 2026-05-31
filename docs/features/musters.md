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

## Channels
- Posts to the guild's configured **default channel**, an **explicit per-post channel**, or the channel the command ran in.
- Optional **allowed-channel list** — empty = any chat channel (text or voice), set = restrict; pickers, autocomplete, and posting all honor it.

## Rewards
- A muster grants **Points** (participation) and optional **Coins** (a spendable currency), paid **at close** — not on check-in, so removing someone before close needs no reversal.
- Values resolve **template → custom → guild defaults**; both 0 = check-in tracking only.

## Templates
- Named presets (e.g. "Tactical Strike Group") set Points, Coins + coin type, retention, optional capacity/expiry, and an emoji — so creators pick a type instead of dialing in rewards.
- Picking a template overrides the guild defaults; a **Tracking Manager** may further tweak its values per-post, a **Muster Creator** gets it locked.
- No template picked → the guild's global muster defaults apply.

## Tracking-session integration
- Link a muster to one or more **tracking sessions** to gate the session's spendable **coin** (points are never gated).
- Gate modes: **None** (mint to all), **Any** (in any linked muster), **All** (in every linked muster).
- A linked muster's own reward is paid at **session close**, only to members who checked in **and** attended.
- Sessions can **auto-create** a check-in muster on open (guild default + per-session override), with a configurable default gate mode and a choice of **where the card posts** — the default muster channel, or the **session's own channel** (used only when the allow-list permits it, else it falls back to the default).
- Ending a session auto-closes its linked musters.

## Admin (web)
- Musters list (active/past, status, count, linked sessions); author new musters.
- Muster detail: roster, **add/remove participants**, close, link/unlink sessions + set the coin-gate mode, remove the Discord card.
- Settings page: default channel, card retention, max active time, reward defaults, creator auto-check-in, auto-create-on-session + gate, allowed channels.

## Lifecycle & housekeeping
- Multiple musters per session (e.g. event check-in + per-round musters).
- Background sweeps auto-expire due musters and prune terminal cards after a retention window (history kept in the web).
- `/muster summary` shows any muster's roster (incl. closed) as an ephemeral reply.

## Access & audit
- **Tracking Manager** (+ admin): full create (custom rewards or templates), close, link, edit roster, settings.
- **Muster Creator**: post from templates only (rewards locked).
- Check-in is open to eligible members.
- Every action is recorded to the audit log.
