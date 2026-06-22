# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.3] 2026-06-22

### Fixed
- Recipe lists on villager and Village Registry panels flickering, and
  occasionally ignoring a click. The custom tab lists were torn down and rebuilt
  on every refresh; they now rebuild only when their contents actually change.
- A vanilla crafting station opening on the wrong tab (stuck on Upgrade) after
  you had just talked to a villager. The villager UI now restores the game's
  crafting tabs to the state they were in before it took over.
- Villagers on dedicated servers unable to reach their chests or do any work.
  The server was instantiating every village building piece twice, which
  corrupted the walkable surface around chests — a server-owned villager would
  stall at a chest and never produce anything. Pieces are no longer duplicated,
  so villagers path to their chests reliably on a dedicated server.
- Craft-capable villagers (farmers, blacksmiths, carpenters) standing idle with
  work waiting. A villager handed a job by the scheduler could fail to actually
  start it and churn between "idle" and "no work"; assigned crafting and farming
  now begin reliably.
- Villagers producing output with no materials on hand, and overshooting a work
  order's maximum. Cooked meat (and other outputs) kept appearing after the raw
  ingredients ran out, and stored counts ran well past the configured cap.
  Villagers now craft only when the ingredients are actually present and stop at
  the order's maximum — counting what is already cooking on a station, not just
  what has finished.
- Work-order quantity edits snapping back to their previous values. Changing an
  order's min/max — especially while the chest was open or villagers were using
  it — could revert as the server and your game fought over the chest. Work-order
  quotas are now stored on the village and applied through the server, so edits
  take effect immediately and persist across reloads.

### Changed
- Work-order min/max settings are now stored on the village itself rather than on
  the order item in a chest, so quota edits no longer get overwritten by the
  server and your game contending for the chest.

### Upgrade note
- Work orders created in a previous version need a one-time migration after
  updating: with developer commands enabled, run `vv_migrate_workorders` on the
  server console once the world has loaded. Until then, previously-placed work
  orders are not picked up (the new version reads quotas from the village, not the
  chest item). Re-running the command is safe — it skips orders already migrated.

## [0.1.2] - 2026-06-18

## [0.1.1] - 2026-06-14

### Fixed
- Villagers reverting to ordinary (hostile) Dvergr after a portal trip or zone
  reload. A villager you walked away from and returned to — through a portal, or
  by leaving and re-loading the area — could come back stripped of its role, AI,
  and equipment. Identity is now restored every time the world re-creates the
  NPC, not just the first time it loads in a session.
- Villages failing to come online after a save is loaded, most often on dedicated
  servers. The region graph could be built before the area finished streaming in,
  leaving villagers unable to locate their region — idle, never finishing
  "mapping" their village, never patrolling or working. The graph build now waits
  until the village's zone is loaded and its pieces are instantiated before it
  runs, and defers itself otherwise.

### Changed
- The Registry station is now the sole anchor for a village; the earlier
  bed-as-anchor system has been removed. Beds are once again ordinary player
  furniture and no longer affect villager assignment.

## [0.1.0] - 2026-06-13

### Added
- First early-access release for server testing.
- Villagers that map an enclosed village, locate stations and containers, and
  fulfill work orders from available materials (harvest/replant, refine coal,
  smelt ore, roast meat, sweep ground items to chests).
- Navigation across doors, player-made stairs, modified terrain, walls and
  ramparts, backed by an async navmesh-rebake task queue.
- Village registry station to recruit and revive villagers, view villager
  status, and identify an enclosed area as a village.
- Work-order items with a slider UI for min/max quantities and composite icons.
- Village mapping (boundary flood-fill + HNA region graph) and custom UI tabs
  with improved controller / Steam Deck support.

### Known limitations
- Melee villager combat is not implemented; guards use crossbows and
  non-combatants flee.
- Navmesh edge cases around shallow stairs, low floor gaps, and doors on terrain
  can strand villagers — prefer wide stairs/ramps and generous clearance.
- Rescue quests and map fragments are experimental and largely untested.
- Away-from-base simulation may stall until the player loads the relevant tile.

[Unreleased]: https://github.com/myrcutio/valheim_villages/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/myrcutio/valheim_villages/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/myrcutio/valheim_villages/releases/tag/v0.1.0
