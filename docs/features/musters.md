# Musters

A "muster" is a button check-in: post a card, members tap **Check In**, you track and reward participation.

## Posting & check-in
- Post a muster from Discord (`/muster post`) or the web admin.
- Members check in with a **Check-In button** (buttons only — no reactions); each click is acknowledged privately.
- Optional **title**, **prompt**, **reward**, **capacity** (hard cap), and **auto-expire after N hours**.
- One check-in per member; ineligible members and a full/closed/expired muster are rejected with a reason.
- Card auto-updates in place (status, count, roster) and goes terminal when closed/expired.

## Channels
- Posts to the guild's configured **default channel**, an **explicit per-post channel**, or the channel the command ran in.
- Optional **allowed-channel list** — empty = any chat channel (text or voice), set = restrict; pickers, autocomplete, and posting all honor it.

## Rewards
- Reward (points or any guild currency) is paid **at close**, not on check-in — so removing someone before close needs no reversal.
- Reward `0` = check-in tracking only.

## Tracking-session integration
- Link a muster to one or more **tracking sessions** to gate the session's spendable **coin** (points are never gated).
- Gate modes: **None** (mint to all), **Any** (in any linked muster), **All** (in every linked muster).
- A linked muster's own reward is paid at **session close**, only to members who checked in **and** attended.
- Sessions can **auto-create** a check-in muster on open (guild default + per-session override).
- Ending a session auto-closes its linked musters.

## Admin (web)
- Musters list (active/past, status, count, linked sessions); author new musters.
- Muster detail: roster, **add/remove participants**, close, link/unlink sessions + set the coin-gate mode, remove the Discord card.
- Settings page: default channel, card retention, auto-create-on-session, allowed channels.

## Lifecycle & housekeeping
- Multiple musters per session (e.g. event check-in + per-round musters).
- Background sweeps auto-expire due musters and prune terminal cards after a retention window (history kept in the web).
- `/muster summary` shows any muster's roster (incl. closed) as an ephemeral reply.

## Access & audit
- Authoring/managing musters → **Tracking Manager** (+ admin). Check-in is open to eligible members.
- Every action is recorded to the audit log.
