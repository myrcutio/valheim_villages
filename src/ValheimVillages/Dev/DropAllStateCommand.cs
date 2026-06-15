using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Items;
using ValheimVillages.TaskQueue;
using ValheimVillages.Villager.AI;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villager.Records;
using ValheimVillages.Villages;
using ValheimVillages.Villages.Entity;
using Object = UnityEngine.Object;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     One-pass dev reset to a clean slate. Deletes ALL village/villager persistent
    ///     state so the next test starts from zero with no object retaining an id or
    ///     reference to deleted state. This is the single explicit deletion path — it
    ///     replaces the old one-off <c>vv_records_clean</c> / <c>vv_delete_record</c>
    ///     commands, which could each leave the world in a mixed, half-reset state.
    ///
    ///     <para>Deletes (persistent ZDOs, owner claimed then DestroyZDO + live
    ///     GameObject torn down):</para>
    ///     <list type="bullet">
    ///       <item>villager record carriers (<c>vv_villager_record</c>)</item>
    ///       <item>villager NPCs (any ZDO with <c>vv_record_id</c> or legacy
    ///         <c>vv_villager_type</c> — e.g. Dverger clones)</item>
    ///       <item>registry stations (<c>vv_village_registry</c>)</item>
    ///       <item>every other <c>vv_*</c> prefab instance (furnishings, egg/pawn item
    ///         drops, work-order props) — caught by prefab-name prefix</item>
    ///     </list>
    ///     <para>Strips every <c>vv_*</c> item from the player inventory and all loaded
    ///     containers, then clears the in-memory village/nav caches.</para>
    ///
    ///     <para>Scope limit: only ZDOs currently registered in <c>ZDOMan</c> and
    ///     inventories of <b>loaded</b> containers are reachable. Run it with the test
    ///     base loaded around the player. Unloaded-sector chests are reported as
    ///     untouched, not silently skipped.</para>
    ///
    ///     <para>Usage: <c>vv_drop_all_state [--dry-run]</c> — with <c>--dry-run</c> it
    ///     prints the deletion summary and changes nothing.</para>
    /// </summary>
    public static class DropAllStateCommand
    {
        [DevCommand("Wipe ALL village/villager state to a clean slate. [--dry-run] to preview.",
            Name = "vv_drop_all_state")]
        public static void DropAll(Terminal.ConsoleEventArgs args)
        {
            var dryRun = HasFlag(args, "--dry-run");

            var zdoMan = ZDOMan.instance;
            if (zdoMan == null)
            {
                Print("[vv_drop_all_state] ZDOMan not ready — aborting");
                return;
            }

            var objectsByID = Traverse.Create(zdoMan)
                .Field<Dictionary<ZDOID, ZDO>>("m_objectsByID").Value;
            if (objectsByID == null)
            {
                Print("[vv_drop_all_state] m_objectsByID unavailable — aborting");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"[vv_drop_all_state]{(dryRun ? " DRY RUN — nothing will be deleted" : "")}");

            // --- Classify ZDOs to delete. Snapshot first: deletion mutates the table. ---
            var vvHashes = CollectVvPrefabHashes();
            var recordHash = RecordPrefabFactory.RecordPrefabHash;
            var registryHash = PieceFactory.RegistryPrefabName.GetStableHashCode();

            var toDelete = new List<ZDO>();
            var byReason = new Dictionary<string, int>();
            foreach (var zdo in objectsByID.Values)
            {
                if (zdo == null) continue;
                var reason = ClassifyForDeletion(zdo, vvHashes, recordHash, registryHash);
                if (reason == null) continue;
                toDelete.Add(zdo);
                byReason[reason] = byReason.TryGetValue(reason, out var n) ? n + 1 : 1;
            }

            sb.AppendLine($"  ZDOs to delete: {toDelete.Count}");
            foreach (var kv in byReason.OrderBy(k => k.Key))
                sb.AppendLine($"    {kv.Key}: {kv.Value}");

            // --- Inventory sweep (player + loaded containers). ---
            var itemCount = SweepInventories(remove: false, out var itemDetail);
            sb.AppendLine($"  vv_* items in inventories: {itemCount}");
            foreach (var d in itemDetail) sb.AppendLine($"    {d}");

            if (dryRun)
            {
                Print(sb.ToString());
                return;
            }

            // --- Execute deletion. ---
            var sessionId = ZDOMan.GetSessionID();
            var scene = ZNetScene.instance;
            var deleted = 0;
            foreach (var zdo in toDelete)
            {
                // DestroyZDO only acts for the ZDO's owner; claim it first.
                if (!zdo.IsOwner()) zdo.SetOwner(sessionId);

                var nview = scene != null ? scene.FindInstance(zdo) : null;
                if (nview != null && nview.gameObject != null)
                    // Removes the instance, DestroyZDO (we own it), and destroys the GameObject.
                    scene.Destroy(nview.gameObject);
                else
                    zdoMan.DestroyZDO(zdo);
                deleted++;
            }

            var itemsRemoved = SweepInventories(remove: true, out _);

            // --- Clear in-memory caches so memory matches the wiped ZDO state without a reload. ---
            VillageRegistry.ClearAll();
            VillagerAIManager.Clear();
            VillageStationRegistry.Clear();
            VillagePoiRegistry.Clear();
            VillageRoomCatalog.Clear();
            VillageAreaManager.Clear();
            BfsAdjacencyStore.Clear();
            RegionBuilder.ClearCachedState();
            GlobalTaskQueue.Clear();

            sb.AppendLine(
                $"  → deleted {deleted} ZDO(s), removed {itemsRemoved} item(s); in-memory caches cleared.");
            Print(sb.ToString());
        }

        /// <summary>
        ///     Reason label if the ZDO is village/villager state, else null. Specific
        ///     checks precede the generic vv_-prefix bucket so the summary is legible
        ///     (record/registry/anchor hashes are themselves vv_ prefabs).
        /// </summary>
        private static string ClassifyForDeletion(ZDO zdo, HashSet<int> vvHashes, int recordHash, int registryHash)
        {
            var prefab = zdo.GetPrefab();
            if (prefab == recordHash) return "record-carrier";
            if (prefab == registryHash) return "registry-station";
            if (prefab == VillagePrefabFactory.VillagePrefabHash) return "village-carrier";
            if (!string.IsNullOrEmpty(zdo.GetString("vv_record_id"))
                || !string.IsNullOrEmpty(zdo.GetString("vv_villager_type"))) return "villager-npc";
            if (vvHashes.Contains(prefab)) return "vv-prefab";
            return null;
        }

        /// <summary>Hashes of every ZNetScene-registered prefab whose name starts with "vv_".</summary>
        private static HashSet<int> CollectVvPrefabHashes()
        {
            var set = new HashSet<int>();
            var scene = ZNetScene.instance;
            if (scene?.m_prefabs == null) return set;
            foreach (var go in scene.m_prefabs)
            {
                if (go == null) continue;
                if (go.name.StartsWith("vv_", StringComparison.Ordinal))
                    set.Add(go.name.GetStableHashCode());
            }

            return set;
        }

        /// <summary>
        ///     Count (and optionally remove) every vv_* item in the player inventory and
        ///     all loaded containers. Container edits persist via Inventory.Changed →
        ///     Container.OnContainerChanged → Save.
        /// </summary>
        private static int SweepInventories(bool remove, out List<string> detail)
        {
            detail = new List<string>();
            var total = 0;

            if (Player.m_localPlayer != null)
                total += SweepInventory(Player.m_localPlayer.GetInventory(), "player", remove, detail);

            var containers = Object.FindObjectsByType<Container>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var c in containers)
            {
                if (c == null) continue;
                var inv = c.GetInventory();
                if (inv == null) continue;
                var p = c.transform.position;
                total += SweepInventory(inv, $"chest@({p.x:F0},{p.z:F0})", remove, detail);
            }

            return total;
        }

        private static int SweepInventory(Inventory inv, string label, bool remove, List<string> detail)
        {
            if (inv == null) return 0;
            var doomed = inv.GetAllItems().Where(IsVvItem).ToList();
            foreach (var item in doomed)
            {
                detail.Add($"{label}: {item.m_dropPrefab.name} x{item.m_stack}");
                if (remove) inv.RemoveItem(item);
            }

            return doomed.Count;
        }

        private static bool IsVvItem(ItemDrop.ItemData item)
        {
            return item?.m_dropPrefab != null
                   && item.m_dropPrefab.name.StartsWith("vv_", StringComparison.Ordinal);
        }

        private static bool HasFlag(Terminal.ConsoleEventArgs args, string flag)
        {
            if (args?.Args == null) return false;
            for (var i = 1; i < args.Args.Length; i++)
                if (string.Equals(args.Args[i], flag, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        private static void Print(string msg)
        {
            global::Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}
