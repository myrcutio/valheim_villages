# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.2.3] - 2026-09-09

### Fixed
- **Compatibility with Valheim's 2026-09-09 update.** The update changed several
  engine interfaces the mod builds on, and the mod stopped working in visible ways:
  villagers could no longer be interacted with at all, and any villager that tried to
  put food in a cooking station or ore in a smelter threw and abandoned the job. Both
  are repaired — hovering a villager now grants the same reach as any other creature,
  and the station calls match the game's new signatures.
- **Villagers cooking on a cold fire.** A villager would walk to a cooking station,
  put meat on it and stand there while nothing cooked, because the mod asked the
  station whether it was lit rather than asking the fire. It now reads the fireplace's
  own state, so an unlit station is recognised as unusable — and when a villager has
  cooking to do and finds the fire out, they fetch fuel and relight it instead of
  giving up.
- **Villagers stalling beside a station or chest.** An approach point could land a few
  centimetres from the edge of the walkable area, which left the villager unable to
  finish the last step and stuck until something else interrupted them.
- **Villagers taking each other's jobs.** Two villagers could claim the same crafting
  task; the loser failed its assignment and went idle. Tasks are now owned by the
  villager that claimed them.
- **Workbench crafting produced items without consuming materials.** The workbench
  path deposited its output but never removed the ingredients it used, so orders could
  overshoot their quota and materials were never spent.
- **Villager records reported as orphaned.** Records whose NPC had been re-created
  (after a world reload) were listed as orphans instead of being re-linked to the
  villager they belong to.

### Added
- **Idle villagers with their own needs.** Instead of picking a leisure spot at
  random, villagers now build up warmth, company, rest and curiosity over time and
  choose what to do accordingly — gathering near each other, visiting the animals and
  the farm, heading for shelter when cold or exposed, and generally preferring the
  more comfortable parts of the village.
- **Villagers tell you when they are stuck.** A villager with a problem only you can
  fix now shows a floating "!" and says what is wrong when you come near: storage
  nearly full, a missing ingredient named from the recipe they actually know, or an
  output chest with no room left.
- **Chests holding a work order are reserved for it.** Villagers no longer fill a
  work-order chest with unrelated salvage — only that order's output, the recipe's
  ingredients, the fuel its station burns, and work orders themselves. Every slot
  taken by something else was a slot the order's output could not land in, which
  stalled the order outright.
- **The Village Registry has its own build-menu icon**, rendered from the piece
  itself, instead of showing the plain table it is built from.

## [0.2.2] - 2026-06-24

### Fixed
- **Rescue quest rewards at surface locations.** A rescue quest could send you to
  an above-ground site — a farm, camp, tower, or ruin — but the Lode Core reward
  only ever appeared if you stepped into a dungeon *interior*. Surface sites have
  no interior to enter, so those quests (most Meadows, Plains, and Ashlands
  rescues) led you to an empty location with nothing to collect. Rewards now
  recognize whether a quest points at an interior dungeon or a surface site:
  interior quests still place the core inside when you enter, and surface quests
  place it when you arrive at the marked location. Mixed-biome pools (Swamp,
  Mountain, Mistlands) are decided per chosen location.

## [0.2.1] - 2026-06-23

### Fixed
- **Map fragment rescue quests on dedicated servers.** Combining biome fragments
  did nothing when playing on a dedicated server: the search for a quest dungeon
  ran on your game client, which doesn't hold the world's location data (only the
  server does), so no location was ever found and the fragments were left unspent.
  The lookup now runs on the server and reports the chosen location back, so
  combining fragments reliably reveals a quest marker in every setup — dedicated
  server, player-hosted, and singleplayer. Fragments are still consumed only once a
  valid location is confirmed, so a failed search leaves them in your pack.

## [0.2.0] - 2026-06-23

### Added
- **Lode Core recruitment economy.** Recruiting and reviving villagers is now
  earned through exploration instead of being free:
  - Loot **biome map fragments** out in the world, then combine three fragments
    from the same biome to **unlock that biome's villager type** for recruitment
    and reveal a quest marker pointing to a dungeon.
  - Recover a **Lode Core** from the marked dungeon — the resource that recruiting
    or reviving a villager spends. A villager that dies drops its Lode Core, so it
    is recoverable rather than lost.
  - Fragments decide *which* villager types you can recruit (knowledge); Lode
    Cores decide *how many* villagers you can field (currency). Unlocks are
    tracked per player.
  - Lode Cores cannot be carried through portals.
  - Recruiting and reviving are atomic — a Lode Core is spent only if a villager
    actually spawns, and is refunded to you otherwise.
- The Village Registry's recruit and revive panels now show the Lode Core cost
  and which villager types you have unlocked.
- The work-order **Order** button on a crafting station now appears only when you
  have unlocked a villager type that can work that station, and its tooltip names
  the type.
- **Idle villagers relax.** A villager with no work seeks out a cozy spot — such
  as a hot tub — to relax instead of standing around.

### Changed
- **Smoother village (re)partition.** Rebuilding a village's navigation graph —
  which happens on structure changes and on load — caused noticeable hitches.
  The rebuild now does far less redundant work: per-cell ground heights are
  cached, and the wall-check flood skips the open ground away from any structure,
  cutting up to ~45% of the physics and ground-height queries per rebuild with no
  change to the result. Larger villages benefit the most.

### Developer
- New `[Region] PartitionProfile` log line reports per-stage timings and
  native-query counts for each region partition, for diagnosing rebuild cost.
- `vv_recruit` and `vv_revive` console commands remain free (no Lode Core cost)
  for testing.

### Upgrade note
- Recruiting and reviving now require Lode Cores, and recruiting a villager type
  requires first unlocking it by combining biome fragments. Villagers already
  living in your world are unaffected — only new recruits and revives draw on the
  new economy.

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

[Unreleased]: https://github.com/myrcutio/valheim_villages/compare/v0.2.0...HEAD
[0.2.0]: https://github.com/myrcutio/valheim_villages/compare/v0.1.3...v0.2.0
[0.1.3]: https://github.com/myrcutio/valheim_villages/compare/v0.1.1...v0.1.3
[0.1.1]: https://github.com/myrcutio/valheim_villages/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/myrcutio/valheim_villages/releases/tag/v0.1.0
