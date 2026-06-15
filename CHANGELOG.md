# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
