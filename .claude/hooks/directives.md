# Valheim Villages — architecture & code-style directives

These are the rules the Stop-hook review enforces against uncommitted changes.
Report a violation ONLY when the diff clearly breaks one of these. Do not
speculate about code that is not part of the diff.

## 1. No fallback code; never silently mask game-state anomalies
- No default/placeholder values substituted when expected state is missing
  (e.g. `?? new Foo()`, `value == null ? defaultValue : value` to paper over a
  state that *should* exist, `GetComponent<X>() ?? gameObject.AddComponent<X>()`
  as a "just in case").
- No catch-and-continue: `catch { }`, `catch { return; }`, `catch { return null/false/default; }`,
  or logging-then-swallowing that lets execution proceed past a real anomaly.
- When expected village/villager state is absent or inconsistent, the code must
  **fail loudly** (throw / halt) — not invent a substitute and carry on.
- Record loads are read-only: never auto-create, auto-delete, or mutate records
  to "repair" a missing/invalid one. Only explicit player actions change records.

## 2. GameObjects never hold persistent data
- The persistent source of truth is the village / villager record (ZDO-backed).
- MonoBehaviours / GameObjects must **derive** their settings from the village or
  villager reference at use-time; they must not be the authoritative store.
- Violations: persisting gameplay state in serialized MonoBehaviour fields,
  reading identity/home/config off the fresh NPC GameObject instead of its record,
  caching record-owned data on the GameObject as the source of truth, or writing
  state back onto the GameObject rather than the record.
- Transient view/render state on a GameObject is fine; *persistent* data is not.

## 3. No dangling functions or unreachable code paths
- No methods/properties added (or left) with zero call sites.
- No unreachable branches: code after `return`/`throw`, conditions that can never
  be true, `if (false)`, dead `else` arms.
- No commented-out blocks left behind in place of deletion.
- If a refactor removes the last caller of something, the something goes too.
