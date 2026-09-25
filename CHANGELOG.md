# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.3.5] - 2026-09-25

### Changed
- **Idle villagers wander the village.** With no work they can do, every villager now strolls around its village instead of parking at one relax spot. It still heads for the fire, a table or a seat when it actually feels cold, lonely or tired, and goes back to wandering afterwards.
- **Idle villagers walk.** Wandering and relaxing now happen at a slow walk; villagers only hurry when they're on a job.
- **Villagers sort out collisions.** When two villagers bump into each other, one steps aside and the other carries on, instead of both shoving until one gives up. A villager blocked by a player, a cart or an animal waits a moment, backs away and tries again, and gives up on a stroll that keeps running into the same thing.
- **Villagers carry what they haul.** A villager now picks a stray item up, walks it to the chest and puts it in there. Before, the item stayed on the ground and jumped into the chest from wherever it lay, even from another floor. If the trip is interrupted, the item is dropped at the villager's feet; it's never lost, even if the villager dies or the area unloads.
- **Piles are hauled in one trip.** Identical items lying together are picked up together, up to a full stack, as a single job, so a spilled pile no longer costs one round trip per item. Two villagers no longer chase the same item.
- **Villagers only flee from real danger.** A hostile has to be able to reach the villager (in view, or already inside the village) and be aware of someone. A greyling wandering outside the wall no longer sends everyone running.
- Idle villagers notice stray items straight away, even while wandering or relaxing.
- Requires BepInExPack_Valheim 5.4.2351.

### Fixed
- **Items no longer duplicate or turn into underground ghosts.** When a Farmer collected harvested crops it only removed its own copy, so the player could still pick the same items up. On a dedicated server that left server-only copies stuck underground, where villagers kept trying and failing to reach them and ignored every other stray item. Villagers now remove collected items properly for everyone.
- **Stray items are hauled even when some can't be reached.** One unreachable item used to be picked every time and block everything else; villagers now take the nearest item they can actually reach, and skip items that are still falling.
- **Villagers go back to work after fleeing.** A Farmer or crafter interrupted mid-walk used to stand idle until a 10-minute timeout; they now pick up the walk where they left off.
- **Long hauls, such as upstairs, no longer time out a few steps short.** Trip time now scales with the length of the route.
- **The Farmer no longer offers orders for things that can't be farmed.** Loot and props such as the Fuling totem, Dvergr lanterns and tankards, charred skulls, pot shards and snowballs showed up as forage orders. Only plants that grow back are offered now, and the old entries disappear on their own. Characters from before the forage unlock fix may still see forage orders for plants they have never picked; the `vv_reset forage` console command clears those.

## [0.3.4] - 2026-09-23

### Changed
- **The Carpenter works through the whole village in one round.** Instead of a separate job per damaged piece, the Carpenter takes one repair round, walks to the nearest damaged piece it can reach, fixes everything around it, and moves straight on to the next, until nothing reachable is left.
- **Lumberjacks no longer charge trees.** They fell and split once in reach, and the trunk still falls away from them. The run-up kept failing — backing off to a spot in mid-air, or running off the walkable ground — and the Lumberjack spent whole rounds retrying the same tree.

### Fixed
- **Villagers no longer freeze on a job forever.** A villager interrupted mid-task (by a monster, for example) could stay "busy" with nothing left to finish it — a Farmer stood idle for a whole day. Any job still running after 10 minutes is now dropped and the villager moves on.
- **Guards return to patrol after a fight.** When a target died or left the guard zone, the Guard was left standing where the fight ended — sometimes for as long as the server ran — until something else happened to move it.
- **Fleeing villagers go back to work.** A scare now lasts at most 30 seconds; a monster still close by simply starts a new one.
- **Villagers are not hurt by falling trees or rolling logs.** Monsters and weapons still hurt them.
- **The Carpenter no longer walks a loop between pieces it can't reach.** A piece it can't get to is skipped for 10 minutes instead of 30 seconds, and a stalled walk is noticed within seconds rather than after 20.
- **Villagers no longer wedge themselves against a door frame.** Doorway crossings now run down the middle of the door, clear of the frame posts.
- **Villagers can make raw fish from any caught fish.** The prep table's raw fish recipe takes any one fish, but villagers read it as needing one of every kind, so a chest of Pike could never be used.
- **"I'm out of…" alerts count every chest in the village.** They only looked 20m around the village centre, so a stocked order in a farther chest (102 Silver against a quota of 100) read as empty and the villager asked for ore it didn't need.

## [0.3.3] - 2026-09-22

### Fixed
- **Villager menus work alongside ZenUI.** The Orders tab opens inside ZenUI's crafting panel and stays there, instead of appearing for a frame and going blank. Villagers had been telling every UI mod that the crafting tab was never open, so those mods cleared the pane out from under them.
- **Navigating your inventory with a controller no longer floods the log with errors**, whether or not another mod is managing the panel.
- **The Order button's controller glyph no longer reads "MISSING BUTTON DEF"** once you put the controller down.
- **The villager tab row stays visible and clickable** while another mod is drawing the crafting panel.
- **Switching villager tabs takes one click again**, not two.

## [0.3.2] - 2026-09-21

### New
- **Lumberjack.** A new villager who plants, fells and hauls timber from a woodlot, replanting from your seed stock. Work orders for every lumber type.
- **Villagers keep a feast on the table.** A Farmer lays out a fresh one from your stores when it has been eaten down to nothing.
- **The Food Preparation Table takes work orders**, the same way a cauldron does.
- **Villagers can walk through your gates.** Doorways are bridged again, so a walled village no longer traps its own villagers.
- **Villagers can reach a chest or station out of their plane** — a shelf above head height, a station on a foundation — by standing beside and below it, up to 3m, where they can see it.

### Changed
- **Guards are taught by any biome map**, not only the Mistlands and Ashlands.
- **A Forester's Post can stand 80m out**, up from 50m, laying a line of anchors home to keep the walkable ground continuous.
- **A woodlot claims less ground** — 20m around the post plus a 4m lane from the nearest gate.
- **Lumberjacks carry an armful, not a handful** — nearby drops are swept into one pile and carried a full stack at a time.
- **Lumberjacks wait for felled timber to settle**, and break up whichever log is nearest them first.
- **Villagers speak less, and only when idle** — roughly one line every two minutes, with a shared quiet period so a busy village isn't a chorus.

### Fixed
- **Villagers no longer drop Mistlands loot.** The inherited Dvergr drop table is stripped; the Lode Core still comes back on death.
- **Rooftops are no longer walkable.** A roof's collider is a plain box, so its flat top read as floor and villagers were sent to stand on it.
- **A building's upper floor is no longer pruned away.** A step was judged against a staircase's average height rather than the tread underfoot, so the stairs and everything above them were dropped.
- **Villagers no longer stand on the floor above the thing they are reaching for.**
- **The lane out to a distant woodlot leaves through a gate** instead of running at solid palisade.
- **Villagers no longer wander off.** The leash was silently disabled by a debug switch, unnavigated movement had no distance limit, and a villager already off the navmesh was driven further off it.
- **Lumberjacks connect with the tree they charge**, and give up if they overshoot it or leave the navmesh.
- **A Lumberjack no longer stands idle in a woodlot full of trees** — the round considers several trees and logs, not just the nearest of each.
- **Guards patrol the walls, not the woodlot.** Ground a village works but does not enclose now stays outside the boundary.
- **Recall works from a client**, and from the console with `vv_recall <name>`. It moves an unloaded villager too, and marks one that cannot be found as fallen so Revive can restore it.
- **A split stack of ingredients no longer stalls an order forever.**
- **Blocked orders say what is wrong and what to do about it**, including when an ingredient is in the village but out of reach.
- **A satisfied order no longer reads as blocked.**
- **Villager station recipes load again.** Every villager's recipe list was silently dropped, so their stations looked empty.
- **The Lumberjack can be unlocked** — a biome map can teach more than one villager type, and the Black Forest teaches both.
- **A Forester's Post no longer reports itself registered without saving anything.**
- **Carpenters stop "repairing" undamaged buildings** past world level 0.
- **A villager no longer freezes waiting on a station** that will never produce.
- **Villagers leave placed feasts and dishes where you put them.**
- **The Order button appears for any villager you have**, not only types unlocked from a map.
- **Farmers plant near your buildings again.**
- **Dedicated servers no longer stall on log floods** from per-frame villager state, approach and repair diagnostics.
- **A diagnostic console command no longer takes a dedicated server down.**
- **The Forester's Post no longer has a plank floating over it.**

## [0.3.1] - 2026-09-18

### Fixed
- **Villagers no longer turn back into ordinary Dvergr mages on a dedicated server.** A
  villager's identity lives in a separate record, and on a *client* that record arrives over
  the network independently of the villager itself. If the villager got there first, the game
  gave up on it permanently and left it standing in your village as a plain Dvergr — no name,
  no dialog, nothing to interact with — while the server quietly went on running the real
  villager. Reconnecting, or walking away and coming back, only re-rolled the same dice. A
  villager now waits for its record to arrive instead of giving up. Hosts and single-player
  were never affected. (Present since 0.2.6.)

## [0.3.0] - 2026-09-18

### New
- **Multiple villages work.** Each village keeps its own map, work orders, villagers and
  schedule, entirely independently — building in one leaves the others alone, and villagers
  in each get on with their own work. Previously a second village would corrupt the first's
  territory, and only one of them would ever rebuild its map.
  Two caveats: this has been tested with two villages on a listen host, not on a dedicated
  server, and villages closer together than about 200m are untested.

### Fixed
- **A second village no longer corrupts the first one's map.** Each village sized its
  territory from the combined boundary of *every* village, so with two settlements each
  one claimed hundreds of metres of the other's empty ground. That blew past the size cap,
  and the cap then trimmed the box around the village's own centre — cutting part of the
  village's own buildings out of its own territory. Territory is now measured per village.
- **With two or more villages, only one of them would rebuild its map.** A queued rebuild
  for one village counted as a duplicate of another village's and was silently dropped, so
  a second village's layout could stay stale indefinitely after you built there.
- **Recalling a villager no longer flings it out of the world.** Recall moved the villager to
  the station and then moved it *again* by the same amount, landing it roughly twice as far
  out — one recall to a station left a farmer 270m away in mid-air, where his zone unloaded
  and he appeared to vanish for good. The rescue that exists to drag a stray villager home
  had the identical fault, so it could not bring him back either. (Present since 0.2.6.)
- **Recall works on a villager that isn't loaded.** It previously did nothing but say "try
  again near the village" — useless advice, since a villager worth recalling is usually one
  that has ended up far enough away to unload. It now moves the villager regardless; it is
  standing at the station when you next get there.
- **Recall always moves the villager.** It used to give up silently if it couldn't find a
  tidy spot beside the station, which is exactly what happens when a villager is stuck or
  somewhere strange — the one time you actually need it.
- **Villagers no longer stall forever on work they cannot reach.** A chest, crop or plant
  spot inside the village but off the walkable map was still offered as work; the villager
  committed to it, failed to path there, and picked the identical target again next cycle —
  so orders it *could* have worked were never reached. Work is now only offered if the
  villager can actually walk to it, and a spot that fails is skipped for the rest of that
  session instead of being re-chosen.
- Farmers no longer end a whole planting session at the first plant spot they can't stand
  at, and no longer walk in a loop between the same unreachable spot and the farm.
- Villagers no longer log a spurious "lost context" warning and drop to idle each time the
  scheduler hands them a job, moments before that job's own scan result arrives.
- **The `vv_viz tri` wireframe now shows every village at once.** Only one village's grid
  was ever drawn — whichever rebuilt its map most recently — because all villages shared a
  single cache that each rebuild overwrote. The same cache fed `vv_probe`, which would
  report the nearest walkable surface as hundreds of metres away while you stood on a
  perfectly good one. Both now report per village, and `vv_probe` names which village a
  triangle belongs to.

### Removed
- **Saves from before 0.2 are no longer supported.** The one-time work-order migration
  (`vv_migrate_workorders`) has been removed, along with the fallback that read a quota
  off the legacy in-chest order token. A work order whose village record cannot be read
  now says so instead of silently displaying a 1-10 range.
- Vestigial dev commands: the offline HNA dump/record pipeline (nothing read its output),
  the scene-snapshot harness, the room catalog, and probes for bugs that have since been
  fixed.

### Changed
- **Building no longer rebuilds the map of villages nowhere near what you changed.** Every
  piece placed or removed, and every hoe stroke, used to rebuild every loaded village's
  map — including villages on the other side of the world. Each change now records where it
  happened, and only villages whose territory it touches rebuild.
- **Rebuilding a village's map now re-examines only the ground near what you changed.**
  Working out whether each patch of ground is walkable is the single most query-heavy part
  of the rebuild, and it was redone from scratch across the whole village every time. Those
  results are now remembered per patch and only re-taken where something actually changed —
  96% fewer physics and navmesh queries for a localised edit, for an identical map.
  `vv_repartition` on its own still re-examines everything, as the check on the fast path.
- Working out where a village's outer wall line falls is likewise remembered per patch of
  ground and only re-taken near a change, on both passes that need it — 98% fewer collision
  queries and over 99% fewer terrain height samples, again for an identical result.
- **Rebuilding a village's map no longer freezes the game while it happens.** The whole
  rebuild used to run inside a single frame — around a second of dead screen, twice over
  with two villages. It now runs in the background across roughly a hundred frames, one
  village at a time, and the navmesh rebuild itself runs off the main thread. Measured on a
  two-village world: the rebuild adds about 2.5ms to an average frame, with two brief
  spikes across the whole rebuild instead of one long freeze.
- A door's width is now measured once per door type instead of once per door, and the game
  no longer searches the entire world for doors twice every time a village map is rebuilt.
- The post-bake navmesh extent check is off by default. It re-examined every village's mesh
  after each rebuild — the single largest stall left — to produce one diagnostic line.
- **Diagnostics no longer fill your disk.** Three separate channels wrote to disk as you
  played, none of them capped or trimmed: a legacy line-per-event file that had reached 4GB,
  a per-rebuild JSON file that had left 3,451 files behind, and a per-rebuild telemetry line.
  Each opened and closed a file on the main thread. The first is deleted outright; the other
  two are off unless you turn them on, and what they recorded is in the normal log anyway.
  (Existing files already on disk are left alone — delete `BepInEx/config/vv_dumps` if you
  want the space back.)
- **A work order now belongs to the village its token is standing in.** Move the token to a
  chest in another village and the order moves with it, keeping the amounts you set. Take
  the token out and the order goes; put it back and it returns. Two villages can each run
  the same order, because each has its own token. An order whose village no longer exists
  isn't lost either — drop its token in any village's chest and that village takes it on.
  A token in your pocket is in transit, not gone, so nothing is removed while you carry it.
- Village territory no longer grows to swallow a neighbouring village's buildings when two
  settlements are close together; each building is attributed to whichever village it is
  nearest.
- `vv_probe`'s wall-flood section reports the village you are standing in rather than
  whichever village rebuilt most recently, and `vv_village anchors` includes the registry.
- `vv_path` works away from the first village: it sampled from a fixed height that only
  suited one village's altitude, and silently reported failure anywhere else.
- `vv_chestpolicy` scopes chests by village territory, matching what villagers actually see,
  instead of a radius that disagreed with them.
- Diagnostics that read the navmesh now warn when a rebuild is in progress, instead of
  reporting a half-rewritten one as fact.
- Calls into the game's internals that fail now say so in the log instead of silently
  reporting "no fire", "no fuel" or "nothing queued" and letting villagers act on it.
- A village is only ever removed once nothing is left of it — no villagers and no registry.
  It could previously be removed while villagers still lived there, orphaning their records.
- A new work order now defaults to **one full stack** of the item it produces, refilling at
  **half a stack**, instead of a flat 1-10. The old default ignored what was being made: it
  ordered a fifth of a stack of something that stacks to 50, and ten separate copies of
  something that does not stack at all.
- The `vv_` dev console surface is consolidated from 63 commands to 36. Dumps that describe
  one subject are now subcommands — `vv_village <list|at|anchors|stations|pois|orders>`,
  `vv_graph <regions|boundary|bfs>`, `vv_viz <path|tri|off>`,
  `vv_reset <all|stale|patrols|forage|markers>` and `vv_log <channel> [on|off]` — and the
  console tab-completes each group's subcommands.
- `vv_get_villagers` is now `vv_records -v`; `vv_beeprobe`/`vv_forageprobe` are
  `vv_harvestprobe`; `vv_bake_audit` is `vv_probe bake`; `vv_drawpath`/`vv_pathcompare` are
  `vv_path`; `vv_capture_at` is `vv_capture <x> <z>`.
- Commands that destroy or fabricate state (`vv_reset all`, `vv_damage_structures`,
  `vv_kill_villager`, `vv_set_record_status`) now require an explicit `--yes`.

## [0.2.8] - 2026-09-18

### New
- Farmers forage ripe berries, mushrooms and thistle in the village, on a work order
- Plants become orderable once you've picked one yourself

### Fixed
- Villagers no longer idle forever after being revived or after a world reload
- Carpenters no longer retry structures they can't reach; those wait until the village changes
- Craft progress bar no longer covers the Order button
- Ripe-crop scan covers the whole village, not a 20m circle around the anchor
- Harvest orders with nothing ready no longer log as unimplemented

### Changed
- Far less log spam; per-chest and item-spawn detail moved behind `vv_log_ingredients` and `vv_log_itemspawns`

## [0.2.7] - 2026-09-15

### Fixed
- Work orders for recipes that craft in batches (arrows and the like) now deliver the whole batch.  the villager was paying the full ingredient cost for the stack and then handing back a single item.
- Work order quantities are now counted from what is actually sitting in the village's chests rather than from what one villager remembers crafting, so an order stops at the right amount and picks itself back up when the stock gets used.

## [0.2.6] - 2026-09-13

### New
- Farmers can now harvest honey
- villagers will pause when their menu is opened
- crafted items will prefer being stored adjacent to their work orders.  other items will avoid being stored in chests with work orders.

### Fixed
- Scheduling villagers no longer get stuck on lower priority, unfulfillable tasks


## [0.2.4] - 2026-09-13

### Fixed
- Village navmesh graph no longer gets confused sometimes on dedicated servers
- Work orders in chests should be owned by dedicated server, not by individual players
- Villagers prioritize tasks more intelligently

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
  **(Removed in 0.3.0 — this migration is no longer available. A world last played
  before 0.2 must be upgraded on 0.2.8 or earlier first.)**

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
