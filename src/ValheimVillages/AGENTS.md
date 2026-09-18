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

`ValheimVillages.Settings.LogSettings` exposes runtime-mutable verbosity flags. The dev console command
`vv_log <channel> [on|off]` lists and toggles them (bare `vv_log` prints the current state of every channel).
Add new flags as new high-volume channels are introduced, and register them in the `vv_log` channel table —
never leave them on by default.

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
  `BoundaryDump.OutputPath` all now use).

## Dev console commands

All mod-registered dev commands are named `vv_<area>` so the Valheim console tab-completes the full mod surface area
when the player types `vv_`. Add new commands via `[DevCommand("description", Name = "vv_<area>")]` on a `public static
void Method()` or `public static void Method(Terminal.ConsoleEventArgs)` — the
`AttributeScanner` wires it into `Terminal.ConsoleCommand` automatically and caches the (name, description) tuple for
`vv` to enumerate.

**Prefer a subcommand over a new top-level name** when a command is one of several views of the same subject. The
groups today are `vv_village <anchors|stations|pois|orders>`, `vv_graph <regions|boundary|bfs>`,
`vv_viz <path|tri|off>`, `vv_reset <all|stale|patrols|forage|markers>` and `vv_log <channel> [on|off]`. Give the group a
`OptionsProvider = nameof(SomeStaticMember)` — a static parameterless method, property or field returning
`IEnumerable<string>` — so the console tab-completes the subcommands. Without it a group *loses* discoverability
relative to flat names, which is the whole reason the flat convention existed. A group's bare form (no subcommand)
should list its subcommands rather than doing anything.

**Mark anything that destroys or fabricates state `Destructive = true`.** The scanner then refuses the invocation
unless it carries `--yes` (or `--dry-run`). Do *not* use Terminal's own `isCheat` flag for this: `IsCheatsEnabled()`
also requires `ZNet.instance.IsServer()`, so a cheat-gated command is unreachable from a client connected to a
dedicated server. For a group whose targets differ in blast radius, leave the command unmarked and call
`DevConfirm.IsConfirmed(args)` / `DevConfirm.PrintRefusal(...)` per target, as `vv_reset` does.

Type `vv` (or `vv help` / `vv --help`) in-game to print every registered command. The list is generated from the live
`AttributeScanner.GetRegisteredDevCommands()` cache, so a new command becomes self-documenting the moment it's
annotated — no manual help-text upkeep.
