using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.Registry;

namespace ValheimVillages.Villager.Records
{
    /// <summary>Dev commands to inspect the villager record table and exercise its lifecycle.</summary>
    public static class VillagerRecordCommands
    {
        /// <summary>
        ///     Tab completions for <c>vv_records</c>: the fixed status filters plus every
        ///     village key that currently has records, so the useful values are reachable
        ///     without first running the command to discover them.
        /// </summary>
        private static IEnumerable<string> RecordFilterOptions()
        {
            var options = new List<string> { "alive", "dead", "egg" };
            options.AddRange(VillagerRecordTable.EnumerateAll()
                .Select(r => r.Village)
                .Where(v => !string.IsNullOrEmpty(v))
                .Distinct());
            return options;
        }

        [DevCommand(
            "Dump villager records [alive|dead|egg|<villageKey>] [-v = full live AI/path/region detail]",
            Name = "vv_records", OptionsProvider = nameof(RecordFilterOptions))]
        public static void Dump(Terminal.ConsoleEventArgs args)
        {
            // Absorbed vv_get_villagers: same row set (the record table) and the same
            // presence resolver, so -v just adds the live-instance block under each row.
            string filter = null;
            var verbose = false;
            for (var i = 1; i < args.Length; i++)
            {
                var a = args[i];
                if (string.Equals(a, "-v", System.StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "--verbose", System.StringComparison.OrdinalIgnoreCase))
                    verbose = true;
                else if (filter == null)
                    filter = a;
            }

            List<VillagerRecord> records;
            if (string.Equals(filter, "alive", System.StringComparison.OrdinalIgnoreCase))
                records = Filter(RecordStatus.Alive);
            else if (string.Equals(filter, "dead", System.StringComparison.OrdinalIgnoreCase))
                records = Filter(RecordStatus.Dead);
            else if (string.Equals(filter, "egg", System.StringComparison.OrdinalIgnoreCase))
                records = Filter(RecordStatus.Egg);
            else if (!string.IsNullOrEmpty(filter))
                records = VillagerRecordTable.QueryByVillage(filter).ToList();
            else
                records = VillagerRecordTable.EnumerateAll().ToList();

            // Make the in-memory instance count honest before we report it in the footer.
            var pruned = VillagerAIManager.PruneTombstones();
            var navHold = VillageNavLock.IsHeld
                ? $" [nav hold {VillageNavLock.SecondsRemaining:F1}s — rebuild settle]"
                : "";

            Print($"[vv_records] {VillagerLiveness.PeerLabel()} {records.Count} record(s)" +
                  $"{(filter != null ? $" (filter: {filter})" : "")}{navHold}");
            Print("  legend: live=loaded here · away=elsewhere/unloaded · missing=NPC ZDO gone (orphan) · " +
                  "unlinked=no NPC link · ?=can't tell (client). npc= is a stored back-link, not a liveness probe.");
            foreach (var r in records)
            {
                var presence = VillagerLiveness.Resolve(r);
                var warn = presence == LivePresence.Missing ? " ⚠ORPHAN" : "";
                Print(
                    $"  {r.Status,-5} {r.Name} ({r.Type})  village={r.Village}  " +
                    $"live={VillagerLiveness.Tag(presence)}{warn}  id={r.RecordId}  npc={r.NpcZdoId} home={r.HomeAnchor}");

                // An AWAY villager has no runtime block — but its stored position is readable
                // on the host, and for a villager that has wandered off that is the ONE thing
                // you want to know. Omitting it meant the tool went quiet in exactly the case
                // it was needed for: "the Lumberjack walked off into the wild" and nothing
                // could say where to.
                if (presence == LivePresence.Away && ZNet.instance != null && ZNet.instance.IsServer())
                {
                    var zdo = ZDOMan.instance?.GetZDO(r.NpcZdoId);
                    if (zdo != null)
                    {
                        var at = zdo.GetPosition();
                        Print($"    stored pos=({at.x:F1},{at.y:F1},{at.z:F1})  " +
                              $"{Vector3.Distance(at, r.HomeAnchor):F0}m from home  " +
                              $"owner={zdo.GetOwner()}");
                    }
                }

                // Only a record with a live local instance has a runtime block to show.
                if (!verbose || presence != LivePresence.Live) continue;
                if (!VillagerAIManager.ActiveVillagers.TryGetValue(r.RecordId, out var ai) || ai == null)
                    continue;

                var sb = new StringBuilder();
                ai.AppendDebug(sb);
                Print(sb.ToString().TrimEnd());
            }

            Print($"  in-memory AI instances on this peer: {VillagerAIManager.ActiveVillagers.Count}" +
                  (pruned > 0 ? $" ({pruned} null tombstone(s) pruned)" : ""));
        }

        [DevCommand("Kill nearest active villager (or by record id) to test the death->Dead flow",
            Name = "vv_kill_villager", Destructive = true)]
        public static void KillVillager(Terminal.ConsoleEventArgs args)
        {
            var idArg = args.Length > 1 ? args[1] : null;

            VillagerAI target = null;
            if (!string.IsNullOrEmpty(idArg))
            {
                VillagerAIManager.ActiveVillagers.TryGetValue(idArg, out target);
            }
            else
            {
                var ppos = Player.m_localPlayer != null
                    ? Player.m_localPlayer.transform.position
                    : Vector3.zero;
                var best = float.MaxValue;
                foreach (var ai in VillagerAIManager.ActiveVillagers.Values)
                {
                    if (ai == null) continue;
                    var d = (ai.transform.position - ppos).sqrMagnitude;
                    if (d < best)
                    {
                        best = d;
                        target = ai;
                    }
                }
            }

            if (target == null)
            {
                Print("[vv_kill_villager] no matching active villager found");
                return;
            }

            var character = target.GetComponent<Character>();
            if (character == null)
            {
                Print("[vv_kill_villager] villager has no Character component");
                return;
            }

            // Apply lethal damage directly. A player's weapon swing skips same-faction
            // (Players) characters during hit detection, so this is the only way to
            // exercise the death path by hand; in real gameplay a monster does it.
            try
            {
                var hit = new HitData
                {
                    m_point = character.transform.position,
                    m_dir = Vector3.up,
                    m_hitType = HitData.HitType.Undefined,
                };
                hit.m_damage.m_blunt = 100000f;
                character.Damage(hit);
                Print($"[vv_kill_villager] applied lethal damage to '{character.m_name}' at {character.transform.position}");
            }
            catch (System.Exception ex)
            {
                // Dev diagnostic: surface the real exception (the console otherwise only
                // shows the reflection wrapper "target of invocation").
                Print($"[vv_kill_villager] Damage threw: {ex.GetType().Name}: {ex.Message}");
                Plugin.Log?.LogError($"[vv_kill_villager] Damage threw:\n{ex}");
            }
        }

        [DevCommand("Set a record's status: vv_set_record_status <id> <alive|dead|egg>",
            Name = "vv_set_record_status", Destructive = true)]
        public static void SetRecordStatus(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 3)
            {
                Print("usage: vv_set_record_status <id> <alive|dead|egg>");
                return;
            }

            if (!System.Enum.TryParse<RecordStatus>(args[2], true, out var status))
            {
                Print($"[vv_set_record_status] unknown status '{args[2]}' (alive|dead|egg)");
                return;
            }

            VillagerRecordTable.SetStatus(args[1], status);
            Print($"[vv_set_record_status] {args[1]} -> {status}");
        }

        [DevCommand("Recruit a villager of <type>: vv_recruit <type> [x] [z] [y]  " +
                    "(X,Z,[Y] order; defaults to the player position)",
            Name = "vv_recruit")]
        public static void Recruit(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 2)
            {
                Print("usage: vv_recruit <type> [x] [z] [y]   (X,Z,[Y] order)");
                return;
            }

            var def = VillagerRegistry.Get(args[1]);
            if (def == null)
            {
                Print($"[vv_recruit] unknown type '{args[1]}'");
                return;
            }

            // Explicit coords, else the player. A dedicated server has no local player, so
            // without the coords form this command resolved (0,0,0) and always reported
            // "no village here" — unusable on the very host the villagers actually run on.
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            Vector3 pos;
            if (args.Length >= 4
                && float.TryParse(args[2], System.Globalization.NumberStyles.Float, inv, out var rx)
                && float.TryParse(args[3], System.Globalization.NumberStyles.Float, inv, out var rz))
            {
                // global:: — inside this namespace the bare name `Villager` binds to the
                // TYPE, not the namespace, so the relative path does not resolve.
                pos = new Vector3(
                    rx,
                    global::ValheimVillages.Villager.AI.Navigation.MeshProbe.ResolveY(rx, rz, args, 4, inv),
                    rz);
            }
            else if (Player.m_localPlayer != null)
            {
                pos = Player.m_localPlayer.transform.position;
            }
            else
            {
                Print("[vv_recruit] no local player (headless?) — pass coords: vv_recruit <type> <x> <z> [y]");
                return;
            }

            // Resolve (never mint) an existing village at the player. Villages are created
            // only by placing a registry station, so dev-recruit requires standing in one.
            var village = Villages.Entity.VillageRegistry.GetVillageCovering(pos)
                          ?? Villages.Entity.VillageRegistry.FindNearAnchor(pos);
            if (village == null)
            {
                Print("[vv_recruit] no village here — place a registry station first (villages are minted only there)");
                return;
            }

            if (village.IsInvalid)
            {
                Print($"[vv_recruit] village {village.VillageId} is invalid (no connected anchor triad); aborting.");
                return;
            }

            // Spawn ON the village (slot-31) graph: resolve an HNA-valid, approachable
            // cell at the player, seeded against the village's founder-connected anchor
            // triad (not the player's island). No fallback by design — fail loudly if the
            // player isn't on a settled village graph rather than spawning off-mesh.
            if (!Villages.Entity.VillageRegistry.TryResolveVillagerSeed(village, pos, out _))
            {
                Print($"[vv_recruit] no reachable spawn location at {pos}; aborting.");
                return;
            }

            // Spawn on the HOST (server-owned from birth); the host re-resolves the seed near
            // the player position against its own navmesh. recordId empty = fresh recruit.
            VillagerRecruitRpc.RequestSpawn(def.type, village.VillageId, pos, "", paid: false);
            Print($"[vv_recruit] requested {def.type} into village {village.VillageId} (host-authoritative spawn)");
        }

        [DevCommand("Revive a fallen villager by record id: vv_revive <id>", Name = "vv_revive")]
        public static void Revive(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 2)
            {
                Print("usage: vv_revive <id>");
                return;
            }

            var rec = VillagerRecordTable.FindById(args[1]);
            Print(VillagerReviveService.Revive(rec, out var err)
                ? $"[vv_revive] revived {args[1]}"
                : $"[vv_revive] failed: {err}");
        }

        private static List<VillagerRecord> Filter(RecordStatus status)
        {
            return VillagerRecordTable.EnumerateAll().Where(r => r.Status == status).ToList();
        }

        private static void Print(string msg)
        {
            // Capped + chunked: a single oversized write to a headless server's
            // stdout pipe blocks the main thread. See ConsoleReport.
            ValheimVillages.Dev.ConsoleReport.Emit(msg);
        }
    }
}
