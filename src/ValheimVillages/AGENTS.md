# Valheim Villages — Mod Source Conventions

Project-wide notes for code under `src/ValheimVillages/`. Subdirectories have their own `AGENTS.md` with area-specific
guidance — read those too when touching `Abilities/`, `Behaviors/`, `Items/`, `Patches/`,
`TaskQueue/`, `UI/`, `Villager/`, or `Villages/`.

## Logging

Use the structured `DebugLog` helpers in preference to raw
`Plugin.Log.LogInfo(...)` calls. They produce single-line, greppable, k=v formatted events that downstream tooling (and
LLM-assisted log review) can parse without per-message regex.

The helpers live in `partial class DebugLog` split across:
`DebugLog.cs` (original JSON sidecar writer, kept for compatibility),
`DebugLog.Time.cs`, `DebugLog.Events.cs`, `DebugLog.Throttle.cs`,
`DebugLog.Sidecar.cs`, `DebugLog.Cycle.cs`, `DebugLog.Correlation.cs`.

### When to reach for what

| Situation                                          | Use                                                                  | Output shape                                                                         |
|----------------------------------------------------|----------------------------------------------------------------------|--------------------------------------------------------------------------------------|
| Normal one-off state change worth logging          | `DebugLog.Event(component, event_name, kv...)`                       | `[Component] event_name t=+12.34s k1=v1 k2=v2`                                       |
| High-volume event (per-tick, per-probe, per-frame) | `DebugLog.Throttled(key, component, event_name, kv...)`              | Same as Event, with `suppressed=N window_s=10` on rollups                            |
| Dumping a list with > ~5 items                     | `DebugLog.List(component, name, items)`                              | Summary line + sidecar JSON file under `<BepInEx>/config/vv_dumps/<name>_<sha>.json` |
| Marking the start of a (re)load cycle              | `DebugLog.BeginCycle(isHotReload)` (called from `Plugin.Awake` only) | `===== VV CYCLE n=N hot=true t=<UTC ISO> =====`                                      |
| Tagging a villager-related line                    | Include `("vid", DebugLog.Vid(npcId))` in the kv                     | `vid=1b11c0b8`                                                                       |
| Tagging a task-related line                        | Include `("task", DebugLog.Tid(name, id))` in the kv                 | `task=hna_partition#42`                                                              |
| Genuine warning/error                              | `Plugin.Log.LogWarning/LogError` — not the structured helpers        | BepInEx default                                                                      |

### Conventions

- **`component`** is PascalCase (`NavMeshLink`, `Region`, `Patrol`, `Cultivator`, `ModTest`).
- **`event_name`** is snake_case (`probe_area`, `triangulation`, `state_change`, `skip`).
- **kv keys** are snake_case (`needs_link`, `wall_blocked`, `rej_bounds`).
- **Values** that contain spaces, `=`, or `"` are auto-quoted by `Event()`. Don't pre-quote.
- **Severity** discipline: structured Info for normal events; `LogDebug` for noisy diagnostics gated behind
  `LogSettings.*Verbose*` flags; `LogWarning` only when something is wrong (a "test was skipped because of a normal
  precondition" is **not** a warning — it's an Info with `reason=…`).

### Toggling verbose channels at runtime

`ValheimVillages.Settings.LogSettings` exposes runtime-mutable verbosity flags. The dev console command `vv_log_navmesh`
toggles `VerboseNavMesh` (NavMesh probe-area firehose). Add new flags + commands as new high-volume channels are
introduced — never leave them on by default.

### Don't

- Don't introduce another `class DebugLog` in a sub-namespace — it will shadow the global one and break call sites in
  subtle ways. (Q1 2026: removed a Cursor-era duplicate from
  `TaskQueue/Handlers/POIDiscoveryHandler.cs` that did exactly this.)
- Don't write multi-line debug dumps to the BepInEx log. Use `DebugLog.List` so the summary stays on one line and the
  bulk lands in a sidecar.
- Don't hand-roll `t=+Ns` timestamps. Use `DebugLog.T()` or just let `Event()` add it.
- Don't write to `/home/benny/Projects/valheim_villages/.cursor/`. All mod-produced sidecar files belong under
  `<BepInEx>/config/vv_dumps/` (the path
  `DebugLog.List` and the redirected `DebugLog.Append` / `PathTelemetry` /
  `BoundaryDump.OutputPath` / `SpatialDump.SavePath` all now use).

## Dev console commands

All mod-registered dev commands follow the convention `vv_<area>_<verb>` so the Valheim console tab-completes the full
mod surface area when the player types `vv_`. Add new commands via
`[DevCommand("description", Name = "vv_<area>_<verb>")]` on a `public static
void Method()` or `public static void Method(Terminal.ConsoleEventArgs)` — the
`AttributeScanner` wires it into `Terminal.ConsoleCommand` automatically and caches the (name, description) tuple for
`vv` to enumerate.

Type `vv` (or `vv help` / `vv --help`) in-game to print every registered command. The list is generated from the live
`AttributeScanner.GetRegisteredDevCommands()` cache, so a new command becomes self-documenting the moment it's
annotated — no manual help-text upkeep.
