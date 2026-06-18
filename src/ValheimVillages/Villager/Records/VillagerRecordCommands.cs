using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.Registry;

namespace ValheimVillages.Villager.Records
{
    /// <summary>Dev commands to inspect the villager record table and exercise its lifecycle.</summary>
    public static class VillagerRecordCommands
    {
        [DevCommand("Dump villager records [alive|dead|egg|<villageKey>]", Name = "vv_records")]
        public static void Dump(Terminal.ConsoleEventArgs args)
        {
            var filter = args.Length > 1 ? args[1] : null;

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

            Print($"[vv_records] {records.Count} record(s){(filter != null ? $" (filter: {filter})" : "")}");
            foreach (var r in records)
                Print($"  {r.Status,-5} {r.Name} ({r.Type})  village={r.Village}  id={r.RecordId}  npc={r.NpcZdoId}");
        }

        [DevCommand("Kill nearest active villager (or by record id) to test the death->Dead flow",
            Name = "vv_kill_villager")]
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
            Name = "vv_set_record_status")]
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

        [DevCommand("Recruit a villager of <type> at the player position (test): vv_recruit <type>",
            Name = "vv_recruit")]
        public static void Recruit(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 2)
            {
                Print("usage: vv_recruit <type>");
                return;
            }

            var def = VillagerRegistry.Get(args[1]);
            if (def == null)
            {
                Print($"[vv_recruit] unknown type '{args[1]}'");
                return;
            }

            var pos = Player.m_localPlayer != null ? Player.m_localPlayer.transform.position : Vector3.zero;
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
            if (!Villages.Entity.VillageRegistry.TryResolveVillagerSeed(village, pos, out var spawnPos))
            {
                Print($"[vv_recruit] no reachable spawn location at {pos}; aborting.");
                return;
            }

            var prefab = !string.IsNullOrEmpty(def.preferredPrefab) ? def.preferredPrefab : "DvergerMage";
            VillagerRecord rec = null;
            var npc = VillagerSpawner.SpawnVillagerNpc(def, def.type, prefab, spawnPos, ref rec, village.VillageId);
            Print(npc != null
                ? $"[vv_recruit] recruited {def.type} at {spawnPos} into village {village.VillageId}"
                : "[vv_recruit] failed");
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
            Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
