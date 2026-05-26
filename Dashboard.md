# Dashboard — deferred / planned

A home for cross-feature dashboard and stats surfaces that are wanted but not yet scheduled. Items here are
**deferred by decision**, not gaps — pull one into a feature doc when it's picked up.

## Quest stats (from GuildQuest §6 wishlist)

Surface per-user quest activity. All the data already exists (the **ledger** + `QuestParticipant` rows) — this is a
read/projection + UI task, no new writes.

- **Per-user stats:** quests **completed** (Approved participations), **posted** (player bounties created),
  **success rate** (Approved ÷ (Approved + Rejected)), currency earned from quests.
- **History surface:** a paged list of a member's past quests (the board's `history` tab already exists per-guild;
  this is the *per-member* cut, e.g. on a profile page).
- **Leaderboard tie-in:** quest completions/points already feed the season leaderboard via the ledger; a
  quest-specific leaderboard (top completers / top posters) is a natural extension.

**Shape:** add read methods to `IQuestReadService` (or a new `IQuestStatsService`) returning DTOs; render on a
profile/dashboard page. No schema change expected — derive from existing `Quests`/`QuestParticipants`/`LedgerEntries`.

**Why deferred:** valuable but not required for the core quest loop; revisit after the current cleanup lands.

---

## Other dashboard candidates

- GuildMaster cockpit polish — counts/badges on the "Action Needed" tab (the queue itself already exists).
