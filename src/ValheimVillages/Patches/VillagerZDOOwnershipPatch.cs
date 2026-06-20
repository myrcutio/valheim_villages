using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ValheimVillages.Items;
using ValheimVillages.Villager.Records;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Patches
{
    /// <summary>
    ///     Shared identity + authority helpers for village/villager ZDO ownership.
    ///     One predicate, one host-authority test — the redistribution steal and the
    ///     client veto must agree on what a "village entity" is, so both go through here.
    /// </summary>
    internal static class VillageOwnership
    {
        private const string RecordIdKey = "vv_record_id";

        // Carrier prefab hashes are derived from constant strings (stable across
        // sessions, processes, and hot reloads). Built lazily on first use so every
        // factory's constant is loaded, then cached. The villager NPC is a Dvergr
        // CLONE, so its prefab hash is NOT here — it is matched by identity keys below.
        private static HashSet<int> s_carrierHashes;
        private static int s_registryHash;

        private static HashSet<int> CarrierHashes
        {
            get
            {
                if (s_carrierHashes == null)
                {
                    // The registry PIECE hash is cached too, but as an EXCLUSION (see
                    // IsVillageZdo) — not a carrier we claim.
                    s_registryHash = PieceFactory.RegistryPrefabName.GetStableHashCode();
                    s_carrierHashes = new HashSet<int>
                    {
                        VillagePrefabFactory.VillagePrefabHash, // vv_village carrier (owns the HNA graph blob)
                        RecordPrefabFactory.RecordPrefabHash, // vv_villager_record carrier
                    };
                }

                return s_carrierHashes;
            }
        }

        /// <summary>
        ///     The host — the peer that must SOLELY own and simulate village entities.
        ///     On a dedicated server this is the server; on a listen-host or singleplayer
        ///     it is the local host. Inherently false on a connected client.
        ///     <para>We deliberately gate on <c>IsServer()</c> rather than trying to detect
        ///     a *dedicated* server: the redistribution steal is idempotent (no-op once the
        ///     host already owns the ZDO), so engaging on a listen-host/SP host causes no
        ///     churn while making the host correctly authoritative over any connected client.
        ///     <c>ZNet.IsDedicated()</c> is a hardcoded-false stub and cannot be used.</para>
        /// </summary>
        internal static bool IsServerAuthority()
        {
            var net = ZNet.instance;
            return net != null && net.IsServer();
        }

        /// <summary>
        ///     A village entity the host must solely own: the village carrier, the record
        ///     carrier (matched by stable prefab hash — cheap), or a villager NPC (a Dvergr
        ///     clone, matched by its durable identity keys only on a hash miss).
        ///     <para>The registry PIECE is deliberately EXCLUDED: it's a player-placed
        ///     buildable, and taking ownership during the mid-placement window yanks the
        ///     fresh piece from the placing client so it despawns on placement (the
        ///     regression the old freeze patch avoided by never SetOwner-ing it). The
        ///     registry is only a UI anchor — the authoritative village DATA lives on the
        ///     vv_village CARRIER (which IS server-owned) — so it follows vanilla piece
        ///     ownership. Excluded before the key fallback because it carries a
        ///     vv_village_id back-ref that would otherwise match.</para>
        ///     Vanilla shared entities (chests, crafting stations, item drops) carry neither
        ///     and are also OUT of scope so player interaction keeps following vanilla rules.
        /// </summary>
        internal static bool IsVillageZdo(ZDO zdo)
        {
            if (zdo == null) return false;
            var hashes = CarrierHashes; // also primes s_registryHash
            var prefab = zdo.GetPrefab();
            if (prefab == s_registryHash) return false; // registry piece = vanilla-owned
            if (hashes.Contains(prefab)) return true;
            return !string.IsNullOrEmpty(zdo.GetString(RecordIdKey))
                   || !string.IsNullOrEmpty(zdo.GetString(Village.IdKey));
        }
    }

    /// <summary>
    ///     Makes the HOST (a dedicated server, or a listen-host/singleplayer host) the
    ///     SOLE, stable owner — and therefore the sole simulator — of village entities,
    ///     and stops a connected client from ever reclaiming them.
    ///
    ///     <para>Valheim replicates ownership with no priority: a ZDO's single owner uid is
    ///     reassigned by <c>ZDOMan.ReleaseNearbyZDOS</c> based purely on active-area overlap,
    ///     and any peer can actively steal a ZDO via <c>ZNetView.ClaimOwnership</c> /
    ///     <c>ZDO.SetOwner</c>. Both funnel through <c>ZDO.SetOwner</c>, and the owner that
    ///     simulates wins because villager AI ticks are gated on <c>m_nview.IsOwner()</c>.
    ///     Village entities are created CLIENT-side (recruit UI / build placement), so they
    ///     are born client-owned and, left alone, are simulated by the client.</para>
    ///
    ///     <para>Two coordinated mechanisms enforce host authority:</para>
    ///     <list type="number">
    ///       <item><b>Server-assert</b> (this file's <see cref="ReleaseNearbyZDOSPatch" />):
    ///         the wholesale <c>ReleaseNearbyZDOS</c> replacement no longer just freezes our
    ///         ZDOs — when running as the host it STEALS any village ZDO that has drifted off
    ///         the host session back to the host (idempotent via an owner check, so no
    ///         per-pass churn), and never releases one to <c>owner=0</c>. The companion
    ///         <c>ZoneLoadRestorationPatch</c> claims carriers the instant their zone loads,
    ///         covering the case where no player is near the village (a dedicated server's
    ///         reference position is the world origin, so this redistribution pass never
    ///         reaches a distant village on its own).</item>
    ///       <item><b>Client-veto</b> (<see cref="ZdoSetOwnerClientVeto" />): on a connected
    ///         client, every attempt to set the client itself as owner of a village ZDO is
    ///         vetoed. This catches the entire active-claim family at one chokepoint
    ///         (<c>ClaimOwnership</c>, direct <c>SetOwner</c>, etc.). It is replication-SAFE:
    ///         the wire receive path applies owners via <c>SetOwnerInternal</c>, which this
    ///         prefix does not intercept — so the client still sees the host as owner.</item>
    ///     </list>
    ///
    ///     <para>Note: this governs the OWNERSHIP channel (<c>OwnerRevision</c>) only. The
    ///     authoritative graph/anchor BLOB is written via <c>ZDO.Set</c> (the
    ///     <c>DataRevision</c> channel), which <see cref="ZdoSetOwnerClientVeto" /> cannot
    ///     and must not touch; those writes are gated to the host separately (the partition
    ///     task is enqueued server-only and <c>Village</c>'s persist methods require ZDO
    ///     ownership).</para>
    /// </summary>
    public static class VillagerZDOOwnershipPatch
    {
        private static readonly List<ZDO> s_nearObjects = new();

        [HarmonyPatch(typeof(ZDOMan), "ReleaseNearbyZDOS")]
        private static class ReleaseNearbyZDOSPatch
        {
            /// <summary>
            ///     Replacement for <c>ZDOMan.ReleaseNearbyZDOS(Vector3, long)</c>: identical
            ///     to the engine method for non-village ZDOs, but for a village ZDO it never
            ///     releases or hands off ownership — and, on the host, actively steals it home.
            ///     This method is invoked server-side only (the engine gates it behind
            ///     <c>IsServer()</c> and runs it once per peer reference position), so the
            ///     steal reclaims a carrier sitting in a present player's active area instead
            ///     of letting that player take it. Kept byte-faithful to the decompiled engine
            ///     method for every other ZDO.
            /// </summary>
            [HarmonyPrefix]
            private static bool Prefix(ZDOMan __instance, Vector3 refPosition, long uid)
            {
                if (ZoneSystem.instance == null || ZNet.instance == null)
                    return false; // engine singletons not ready — do nothing this tick

                var zone = ZoneSystem.GetZone(refPosition);
                s_nearObjects.Clear();
                __instance.FindSectorObjects(
                    zone, ZoneSystem.instance.m_activeArea, 0, s_nearObjects);
                var activatedArea = ZoneSystem.instance.m_activeArea - 1;

                foreach (var zdo in s_nearObjects)
                {
                    if (zdo == null || !zdo.Persistent) continue;

                    if (VillageOwnership.IsVillageZdo(zdo))
                    {
                        // Host asserts sole ownership: drag any village ZDO that has drifted
                        // off the host session back to the host. Idempotent — the owner check
                        // makes it a no-op (no OwnerRevision bump) once the host already owns
                        // it, so there is no per-pass churn and no ping-pong. Never release to
                        // 0, never let a client steal it.
                        if (VillageOwnership.IsServerAuthority()
                            && zdo.GetOwner() != ZDOMan.GetSessionID())
                            zdo.SetOwner(ZDOMan.GetSessionID());
                        continue;
                    }

                    var sector = zdo.GetSector();
                    if (zdo.GetOwner() == uid)
                    {
                        if (!ZNetScene.InActiveArea(sector, zone, activatedArea))
                            zdo.SetOwner(0L);
                    }
                    else if ((!zdo.HasOwner() || !IsInPeerActiveArea(sector, zdo.GetOwner()))
                             && ZNetScene.InActiveArea(sector, zone, activatedArea))
                    {
                        zdo.SetOwner(uid);
                    }
                }

                return false; // fully replaced the engine method
            }

            /// <summary>Faithful copy of the private <c>ZDOMan.IsInPeerActiveArea</c>.</summary>
            private static bool IsInPeerActiveArea(Vector2i sector, long owner)
            {
                if (owner == ZDOMan.GetSessionID())
                    return ZNetScene.InActiveArea(sector, ZNet.instance.GetReferencePosition());

                var peer = ZNet.instance.GetPeer(owner);
                return peer != null && ZNetScene.InActiveArea(sector, peer.GetRefPos());
            }
        }

        /// <summary>
        ///     Client-side veto on the single active-claim funnel <c>ZDO.SetOwner</c>: a
        ///     connected client may never set ITSELF as owner of a village ZDO. Because every
        ///     active claim (<c>ZNetView.ClaimOwnership</c>, container/item-drop claims, mod
        ///     haul/destroy claims, the client's own redistribution pass) routes through
        ///     <c>SetOwner</c>, this one prefix neutralizes them all without per-type patches.
        ///
        ///     <para>Replication-safe: incoming owners from the host arrive via
        ///     <c>SetOwnerInternal</c> (in <c>ZDOMan.RPC_ZDOData</c>), which this prefix does
        ///     not intercept — so the client still receives and applies the host as owner.</para>
        /// </summary>
        [HarmonyPatch(typeof(ZDO), nameof(ZDO.SetOwner))]
        private static class ZdoSetOwnerClientVeto
        {
            [HarmonyPrefix]
            private static bool Prefix(ZDO __instance, long uid)
            {
                // Host / pre-load / no-net: pass through. Keeps SetOwner a pass-through on the
                // host (which legitimately assigns owners) and on the hot path generally.
                if (ZNet.instance == null || ZNet.instance.IsServer()) return true;
                if (ZDOMan.instance == null) return true;

                // Only veto the client claiming a village ZDO FOR ITSELF. Owner=0 releases
                // and assignments to other peers pass through untouched.
                if (uid != ZDOMan.GetSessionID()) return true;
                if (!VillageOwnership.IsVillageZdo(__instance)) return true;

                return false; // veto: the client cannot grab a village entity from the host
            }
        }
    }
}
