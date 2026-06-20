# Valheim Villages

Add villagers to Valheim.  They map out an enclosed village, locate stations and containers within the walls, and attempt to fulfill work orders using available materials.

> **Early access.** This is a first draft published for server testing. Expect rough edges — see Known Issues.

### Install

Requires [BepInExPack Valheim](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/). Install via a mod manager (r2modman / Thunderstore Mod Manager) by searching for **ValheimVillages**, or manually drop `ValheimVillages.dll` into `BepInEx/plugins/ValheimVillages/`. For dedicated servers, install the same plugin on the server.

### Current features

  - villagers
    - when idle they scan for new work to do by looking for work orders in chests or items lying on the ground
      - ground items are swept to chests
        - TODO: items are swept to whatever chest is closest, there's no rhyme or reason to it
      - plants are harvested and replanted if necessary
      - refine coal in a kiln
      - smelt ore in a smelter
      - roast meat on a cooking station
      - TODO: replace used-up feasts with fresh ones
    - can navigate through doors, up player-made stairs, around modified terrain, and across walls and ramparts
    - work-in-progress: they can sometimes fight enemies, poorly
      - guards will try to shoot crossbows at enemies
      - farmers, blacksmiths, etc will attempt to run away and cower
  - async queue for mod-related tasks
    - rebaking navmesh happens async after piece edits in a village
    - work scanning
    - deduplication and rate-limiting to prevent lag (hopefully)
  - village registry station
    - can recruit new villagers
    - can revive dead villagers
    - shows all current villager status
    - identifies an enclosed area as a village
  - Work order items
    - all recipes have an option to create an "order"
    - right clicking provides a UI with sliders to set minimum and maximum desired quantities
    - work order icon is a composite image
      - recipe item icon is overlaid
      - green check if order minimum is satisfied
      - red X if order is unsatisfied and requirements are missing
  - village mapping
    - significant navmesh hacks are involved to determine the boundaries of the village
      - two flood/fill operations to determine walls
        - outside-in from 30m, represents navmesh cells that are unreachable for villagers
        - inside-out from the village registry station, must be connected by a direct path back to the station
        - used to derive the village polygon for drawing the map in Tasks menus
    - HNA graph region mapping
      - identifies navmesh areas on similar elevations
      - links navmesh areas across stairs and doors that typically are not navigable by the default valheim Humanoid or MonsterAi agent
      - serves as a rudimentary "room" designation
        - TODO: rooms will eventually convey comfort bonuses to the villagers, but those bonuses are not yet implemented
  - custom UI tabs
    - Overrides some native valheim tab management to allow for more than just craft/upgrade
    - added better controller support for navigating tabs, particularly on steam deck

### Known Issues
If you are looking here you probably 
  - the villagers use dverger prefabs for animations, and sometimes a race condition on load fails to clean up the old prefab while still creating a new one, leading to very confused dwarves wandering your village
  - navmeshes are really hard
    - there are many, many edge cases that the villagers may not be able to properly navigate.  in particular this is a problem with shallow stairs and low gaps between floors.  to best avoid these, use wide stairs and ramps, and lots of clearance between floors and terrain.  if it's not handicap accessible, it will probably prove a problem for a villager.
    - gaps in village walls may break navmesh baking, particularly around doors on top of terrain (doors on pieces and floors work well)
  - villager rescue quests and map fragments may appear in the world, but they are very experimental and untested
  - villagers sometimes get distracted and let the meat burn on the cooking station.  mea culpa.
  - melee combat is absolutely not a thing and even a lowly greyling can end up wiping out a village if there isn't a capable guard or player to protect them
  - The village _should_ keep humming along while the player is away, but things may stall if the player loads in far away and hasn't loaded in that tile yet.  YMMV.
