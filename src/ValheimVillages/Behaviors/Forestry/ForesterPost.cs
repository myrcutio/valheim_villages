using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Enums;
using ValheimVillages.Items;
using ValheimVillages.Schemas;
using ValheimVillages.TaskQueue;
using ValheimVillages.Villager.AI.Navigation;
using ValheimVillages.Villages.Entity;

namespace ValheimVillages.Behaviors.Forestry
{
    /// <summary>
    ///     The Forester's Post: a player-placed woodpile under a bannered mast that marks
    ///     where the Lumberjack's
    ///     grove goes, and — the part that actually matters — registers itself as a village
    ///     ANCHOR.
    ///
    ///     <para><b>Why a buildable piece instead of an automatic grove.</b> The villager
    ///     navmesh and region graph are built within
    ///     <see cref="Handlers.RegionPartitionHandler.RegionBuildRadius" /> of a village
    ///     anchor, and nowhere else. Measured on a dedicated server: at the palisade the
    ///     slot-31 mesh still HITs but the region graph is already unresolved; 20m further
    ///     out there is no mesh at all, and Valheim's own Humanoid mesh is absent on a
    ///     headless host, so there is nothing to fall back to. A grove far enough out for a
    ///     tree to fall safely is therefore unreachable unless something extends coverage to
    ///     it — and an anchor is precisely the thing that does.</para>
    ///
    ///     <para><b>Siting is the player's call.</b> This validates only what would stop the
    ///     post FUNCTIONING — out of anchor range, or no walkable ground to seed from. Whether
    ///     a falling trunk can reach their walls is the player's business, so there is no
    ///     clearance check here and none per-tree in the planting: the Lumberjack works the
    ///     woodlot where it was put.</para>
    /// </summary>
    public static class ForesterPost
    {
        public const string PrefabName = "vv_forester_post";

        /// <summary>Anchor name on the village ZDO. Not part of the triad — see below.</summary>
        public const string AnchorName = "forester";

        /// <summary>
        ///     Radius around the post that saplings are PLANTED within. Comfortably inside
        ///     <c>RegionPartitionHandler.RegionBuildRadius</c> (30m) so the terrain out here is
        ///     still accepted by the anchor-distance filter, and wide enough to hold a grove
        ///     with felling room.
        /// </summary>
        public const float GroveRadius = 18f;

        /// <summary>
        ///     Radius the woodlot is kept WALKABLE to, and searched for timber within.
        ///
        ///     <para>Deliberately larger than <see cref="GroveRadius" />, because felled
        ///     timber travels: the trunk is pushed away from the villager, who approaches
        ///     from the village side, so logs and the wood they drop land on the FAR side of
        ///     the tree. Measured with the two radii equal, logs and wood consistently came to
        ///     rest at 15-18m of an 18m grove — at or past the walkable edge — and the
        ///     Lumberjack could never reach his own output. Keep this inside the 30m
        ///     anchor-distance filter, or the extra ring has no navmesh either.</para>
        /// </summary>
        public const float WorkRadius = 20f;

        /// <summary>
        ///     Half-width of the walkable lane kept open between the village and a distant
        ///     post. Narrow on purpose: it is a way out to the woodlot, not an extension of
        ///     the village, and every cell freed here is a cell the boundary pass would
        ///     otherwise have sealed.
        /// </summary>
        public const float CorridorLaneRadius = 4f;

        /// <summary>
        ///     How far from the post's centre its anchor seed must sit. The woodpile is a
        ///     2.5m-wide solid lump and a seed on top of it is walled in by its own collider —
        ///     clear of it is the only place a villager could actually stand.
        /// </summary>
        public const float SeedClearance = 2.5f;

        /// <summary>The woodpile the post is built on — it reads as forestry at a glance.</summary>
        private const string BasePrefab = "wood_stack";

        /// <summary>Donor for the mast: a 2m pole, grafted twice to clear the pile.</summary>
        private const string PolePrefab = "wood_pole2";

        /// <summary>
        ///     Donor for the flag at the top: a crossbar and a hanging cloth.
        ///
        ///     <para><c>piece_banner05</c> is the GREEN one — the Guck banner. Its material is
        ///     named <c>Banner_Border_OrangeGrey_1_mat</c>, which is why this took a detour:
        ///     Valheim's asset names are not colour labels. Confirmed by placing one in-world
        ///     and reading the prefab back off the ZDO. Using it beats tinting a white banner
        ///     with a hand-built unlit material, which ignored lighting and glowed at night.</para>
        /// </summary>
        private const string BannerPrefab = "piece_banner05";

        /// <summary>
        ///     Where each grafted part sits above the woodpile's origin.
        ///
        ///     <para>Not invented: measured off the arrangement the player built in-world and
        ///     then read back with <c>vv_zdo_audit</c> — pile at y+0, pole origins at +0.4 and
        ///     +2.4 (a <c>wood_pole2</c> is 2m tall and centred on its origin, so the two make
        ///     one mast from -0.6 to +3.4), banner at +3.5, just above the mast's top. Copying
        ///     a look that was built by hand beats guessing offsets — the previous attempt
        ///     guessed a rotation and left a plank hanging in mid-air.</para>
        /// </summary>
        private static readonly float[] PoleHeights = { 0.4f, 2.4f };

        private const float BannerHeight = 3.5f;

        /// <summary>
        ///     How far the cloth hangs to one side of the mast.
        ///
        ///     <para>The donor's cloth is a flat plane centred on the banner's origin, and the
        ///     mast runs straight up through that origin — so grafted as-is the flag is sliced
        ///     down the middle by its own pole. The cloth's normal is X (its mesh has no
        ///     X extent at all), so a small shift along X moves the whole plane clear of a
        ///     0.4m-wide pole; the crossbar stays centred, which is what holds the two
        ///     together visually.</para>
        /// </summary>
        private const float ClothOffset = 0.3f;

        private static GameObject _prefab;

        /// <summary>
        ///     Removes the woodlot's cells from the bake's outside-cell blocker set.
        ///
        ///     <para>Registering the post as an anchor is necessary but NOT sufficient. Measured:
        ///     with the anchor in place the terrain around the post passes every RegionBuilder
        ///     filter (<c>bounds=57 dist=57 steep_ok=57 capsule_ok=57</c>) and STILL has no
        ///     navmesh, because the cell is <c>in_outsideCells=YES</c> and the bake stamps every
        ///     outside cell with a NotWalkable ModifierBox. The anchor gets the ground accepted;
        ///     this stops it being carved away again.</para>
        ///
        ///     <para>Scoped to a disc around the post, so the village hull everywhere else stays
        ///     sealed exactly as before — this deliberately claims one patch of outdoors, it does
        ///     not open the perimeter.</para>
        /// </summary>
        /// <param name="gates">
        ///     The gate pivots detected by the flood that produced <paramref name="outsideCells" />
        ///     — NOT <c>village.Graph.GetGates()</c>. The committed graph cannot answer this
        ///     during a partition: <c>SetGraph</c> clears the gate list and <c>SetGates</c>
        ///     only refills it at the very end of the run, and the list is not persisted, so
        ///     after a server restart it reads empty until a partition has completed. Reading
        ///     it here made the lane fall back to the village anchor on exactly the run that
        ///     mattered — measured after a restart: 13 gates on the committed graph, a lane
        ///     drawn from the registry anyway, and the cell just outside the nearest gate
        ///     still <c>in_outsideCells=YES</c>.
        /// </param>
        public static int UnblockWoodlotCells(
            HashSet<long> outsideCells, string villageId, HashSet<long> extramural = null,
            IReadOnlyList<Vector3> gates = null)
        {
            if (outsideCells == null || outsideCells.Count == 0) return 0;
            if (string.IsNullOrEmpty(villageId)) return 0;

            Village village = null;
            foreach (var v in VillageRegistry.EnumerateAll())
                if (v.VillageId == villageId)
                {
                    village = v;
                    break;
                }

            if (village == null || !village.TryGetAnchor(AnchorName, out var post)) return 0;

            // Route first, disc second. The route filter asks which stepping stones lie
            // OUTSIDE the village, and it answers that from outsideCells — the set FreeDisc is
            // about to remove entries from. Carving the woodlot first would make every stone
            // within WorkRadius of the post read as "inside" and silently drop it.
            var route = CorridorRoute(village, post, outsideCells, gates);

            var freed = FreeDisc(outsideCells, post, WorkRadius, extramural);

            // And the lane out to it. The grove being walkable is no use if the ground between
            // it and the village is stamped NotWalkable with everything else outside the hull:
            // measured on a live server, the bake reached a 60m grove and a villager could
            // still only walk 11.6m of the 57m (PathPartial), because the corridor itself was
            // carved away. The lane is narrow — it is a path home, not an extension of the
            // village — and it cannot open a wall: the bake still respects colliders, so this
            // only permits mesh where the ground is genuinely clear.
            for (var i = 1; i < route.Count; i++)
                freed += FreeSegment(outsideCells, route[i - 1], route[i], CorridorLaneRadius, extramural);

            if (freed > 0)
                Plugin.Log?.LogInfo(
                    $"[ForesterPost] Woodlot: freed {freed} outside cell(s) — {WorkRadius:F0}m around " +
                    $"({post.x:F0},{post.z:F0}) plus a {CorridorLaneRadius:F0}m lane over " +
                    $"{route.Count - 1} corridor leg(s) — so the grove keeps walkable navmesh");
            return freed;
        }

        /// <summary>
        ///     Where the lane out to the woodlot runs: from the GATE nearest the post, through
        ///     whichever stepping stones are genuinely outdoors, to the post.
        ///
        ///     <para>It used to start at the village anchor — the registry, in the middle of
        ///     the settlement — so the lane was drawn straight out through the houses and the
        ///     wall. Everything on the inside of that line is already village and needs no
        ///     claiming, and the part crossing the wall claims ground on both sides of it. The
        ///     villager leaves through a gate, so the gate is where his route outdoors actually
        ///     begins.</para>
        ///
        ///     <para>A stone INSIDE the wall is worse than useless: it drags the lane back
        ///     indoors and forces the outbound leg to cross the perimeter wherever that leg
        ///     happens to land. Measured on a live village — one stone at (-92.5,-409.3),
        ///     inside the hull, sent the lane through a stake_wall at (-98,-411) with no gate
        ///     in it, and the bake (which still respects colliders) left the grove a separate
        ///     island: <c>vv_forester_check</c> read "region graph covers it: yes" and
        ///     "villager can path to it: NO". "Outside" is asked of <c>outsideCells</c>, the
        ///     perimeter flood's own answer; the old test compared distances from the gate,
        ///     which says nothing at all about which side of the wall a point is on.</para>
        /// </summary>
        private static List<Vector3> CorridorRoute(
            Village village, Vector3 post, HashSet<long> outsideCells,
            IReadOnlyList<Vector3> gates)
        {
            var start = CorridorStart(village, post, gates);

            var stones = new List<Vector3>();
            foreach (var anchor in village.Anchors)
                if (anchor.Name != null &&
                    anchor.Name.StartsWith(ForesterPostAnchor.LinkAnchorPrefix) &&
                    IsOutsideVillage(anchor.Position, outsideCells))
                    stones.Add(anchor.Position);

            stones.Sort((a, b) =>
                (a - start).sqrMagnitude.CompareTo((b - start).sqrMagnitude));

            var route = new List<Vector3> { start };
            route.AddRange(stones);
            route.Add(post);
            return route;
        }

        /// <summary>Is this point on the far side of the village perimeter?</summary>
        private static bool IsOutsideVillage(Vector3 pos, HashSet<long> outsideCells)
        {
            if (outsideCells == null) return true;

            var cellSize = RegionGraph.LookupCellSize;
            return outsideCells.Contains(RegionGraph.PackXz(
                Mathf.FloorToInt(pos.x / cellSize), Mathf.FloorToInt(pos.z / cellSize)));
        }

        /// <summary>
        ///     The gate closest to the post, out of the gates the CALLER supplies, falling back
        ///     to the village anchor when there are none — which is the right answer for an
        ///     unwalled village and the wrong-but-unavoidable one for a walled village whose
        ///     gates have not been detected yet.
        ///
        ///     <para>The gate list is a parameter rather than something read from the graph on
        ///     purpose: the two callers have genuinely different sources. The carve must use
        ///     the flood's own freshly-gathered seals, because the graph's list is empty at
        ///     that moment; <c>vv_forester_check</c> must use the committed graph, because it
        ///     is reporting on the partition that already ran.</para>
        /// </summary>
        internal static Vector3 CorridorStart(
            Village village, Vector3 post, IReadOnlyList<Vector3> gates)
        {
            if (gates == null || gates.Count == 0) return village.Anchor;

            var best = village.Anchor;
            var bestSq = float.MaxValue;
            foreach (var gate in gates)
            {
                var d = (gate - post).sqrMagnitude;
                if (d >= bestSq) continue;
                bestSq = d;
                best = gate;
            }

            return best;
        }

        private static int FreeDisc(
            HashSet<long> outsideCells, Vector3 centre, float radius, HashSet<long> extramural = null)
        {
            var cellSize = RegionGraph.LookupCellSize;
            var span = Mathf.CeilToInt(radius / cellSize);
            var cx = Mathf.FloorToInt(centre.x / cellSize);
            var cz = Mathf.FloorToInt(centre.z / cellSize);
            var r2 = radius * radius;

            var freed = 0;
            for (var dx = -span; dx <= span; dx++)
            for (var dz = -span; dz <= span; dz++)
            {
                // Disc, not the bounding square — a square would claim corners well beyond
                // the stated radius and push the carve-out toward the walls.
                var wx = (cx + dx + 0.5f) * cellSize - centre.x;
                var wz = (cz + dz + 0.5f) * cellSize - centre.z;
                if (wx * wx + wz * wz > r2) continue;

                var discKey = RegionGraph.PackXz(cx + dx, cz + dz);
                if (!outsideCells.Remove(discKey)) continue;
                extramural?.Add(discKey);
                freed++;
            }

            return freed;
        }

        /// <summary>Free every cell within <paramref name="radius" /> of the segment a→b.</summary>
        private static int FreeSegment(
            HashSet<long> outsideCells, Vector3 a, Vector3 b, float radius,
            HashSet<long> extramural = null)
        {
            var cellSize = RegionGraph.LookupCellSize;
            var minX = Mathf.FloorToInt((Mathf.Min(a.x, b.x) - radius) / cellSize);
            var maxX = Mathf.FloorToInt((Mathf.Max(a.x, b.x) + radius) / cellSize);
            var minZ = Mathf.FloorToInt((Mathf.Min(a.z, b.z) - radius) / cellSize);
            var maxZ = Mathf.FloorToInt((Mathf.Max(a.z, b.z) + radius) / cellSize);
            var r2 = radius * radius;

            var abx = b.x - a.x;
            var abz = b.z - a.z;
            var abLen2 = abx * abx + abz * abz;

            var freed = 0;
            for (var cx = minX; cx <= maxX; cx++)
            for (var cz = minZ; cz <= maxZ; cz++)
            {
                var wx = (cx + 0.5f) * cellSize;
                var wz = (cz + 0.5f) * cellSize;

                // Distance from the cell centre to the segment, clamped to its ends.
                var t = abLen2 > 0.0001f
                    ? Mathf.Clamp01(((wx - a.x) * abx + (wz - a.z) * abz) / abLen2)
                    : 0f;
                var dx = wx - (a.x + abx * t);
                var dz = wz - (a.z + abz * t);
                if (dx * dx + dz * dz > r2) continue;

                var segKey = RegionGraph.PackXz(cx, cz);
                if (!outsideCells.Remove(segKey)) continue;
                extramural?.Add(segKey);
                freed++;
            }

            return freed;
        }

        [RequireObjectDB]
        public static void RegisterDeferred()
        {
            Register(ZNetScene.instance);
        }

        public static void Register(ZNetScene zNetScene)
        {
            if (zNetScene == null) return;

            EnsurePrefab(zNetScene);
            if (_prefab == null) return;

            if (!zNetScene.m_prefabs.Contains(_prefab))
                zNetScene.m_prefabs.Add(_prefab);

            var named = typeof(ZNetScene)
                .GetField("m_namedPrefabs", BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(zNetScene) as Dictionary<int, GameObject>;
            if (named != null) named[PrefabName.GetStableHashCode()] = _prefab;

            PieceFactory.AddToHammerTable(_prefab);
            Plugin.Log?.LogInfo($"[ForesterPost] Registered '{PrefabName}' in ZNetScene + Hammer table");
        }

        private static void EnsurePrefab(ZNetScene zNetScene)
        {
            var existing = _prefab != null ? _prefab : zNetScene.GetPrefab(PrefabName);
            if (existing != null)
            {
                // Hot reload drops mod components off the template; re-add before reuse.
                Configure(existing);
                _prefab = existing;
                return;
            }

            var basePrefab = zNetScene.GetPrefab(BasePrefab);
            if (basePrefab == null)
            {
                Plugin.Log?.LogError(
                    $"[ForesterPost] Base prefab '{BasePrefab}' not found; cannot build the post");
                return;
            }

            _prefab = PieceFactory.ClonePrefab(basePrefab, PrefabName);
            Configure(_prefab);
        }

        private static void Configure(GameObject prefab)
        {
            var piece = prefab.GetComponent<Piece>();
            if (piece != null)
            {
                piece.m_name = "Forester's Post";
                piece.m_description =
                    "Marks a woodlot for a Lumberjack. Must be within 50m of the village and " +
                    "reachable on foot. Trees fall where they are cut.";
                piece.m_category = Piece.PieceCategory.Crafting;

                var wood = ObjectDB.instance?.GetItemPrefab("Wood")?.GetComponent<ItemDrop>();
                if (wood != null)
                    piece.m_resources = new[]
                    {
                        new Piece.Requirement { m_resItem = wood, m_amount = 10, m_recover = true },
                    };
            }

            var old = prefab.GetComponent<ForesterPostAnchor>();
            if (old != null) Object.DestroyImmediate(old);
            prefab.AddComponent<ForesterPostAnchor>();

            MakeIndestructible(prefab);
            GraftMast(prefab);
        }

        /// <summary>
        ///     Raises a bannered mast out of the middle of the woodpile, so a Forester's Post
        ///     is not mistaken for one of the ordinary woodpiles it is cloned from.
        ///
        ///     <para>Every grafted part keeps the donor child's OWN local rotation and scale
        ///     and is only lifted — the donor's crossbar, for instance, is a unit cube scaled
        ///     1.41 long and yawed 90°, and re-deriving that by hand is exactly how the first
        ///     version ended up with a plank floating above the cloth.</para>
        ///
        ///     <para>Meshes only: no colliders come across, so the mast cannot carve the
        ///     woodlot's navmesh or block the Lumberjack who works around it.</para>
        /// </summary>
        private static void GraftMast(GameObject prefab)
        {
            // Hot reload re-runs Configure on the surviving template; drop the previous mast
            // first or the parts stack up one set per reload.
            var stale = new List<Transform>();
            foreach (Transform child in prefab.transform)
                if (child.name.StartsWith("vv_"))
                    stale.Add(child);
            foreach (var t in stale) Object.DestroyImmediate(t.gameObject);

            var scene = ZNetScene.instance;
            if (scene == null) return;

            var pole = scene.GetPrefab(PolePrefab);
            var banner = scene.GetPrefab(BannerPrefab);
            if (pole == null || banner == null)
            {
                Plugin.Log?.LogWarning(
                    $"[ForesterPost] Mast donors missing (pole={pole != null}, banner={banner != null}); " +
                    "post will look like a plain woodpile");
                return;
            }

            // "New" is the undamaged visual; the Worn/Broken variants are the same mesh in a
            // scuffed material and would just z-fight with it.
            for (var i = 0; i < PoleHeights.Length; i++)
                GraftChild(prefab, pole.transform.Find("New"), $"vv_mast_{i}",
                    Vector3.up * PoleHeights[i]);

            GraftChild(prefab, banner.transform.Find("woodbeam"), "vv_banner_bar",
                Vector3.up * BannerHeight);
            GraftChild(prefab, banner.transform.Find("default"), "vv_banner_cloth",
                new Vector3(ClothOffset, BannerHeight, 0f));
        }

        /// <summary>
        ///     Copy one donor child's mesh, material and local transform onto the post, lifted
        ///     by <paramref name="lift" /> metres.
        /// </summary>
        private static void GraftChild(GameObject parent, Transform src, string name, Vector3 offset)
        {
            var srcMf = src != null ? src.GetComponent<MeshFilter>() : null;
            var srcMr = src != null ? src.GetComponent<MeshRenderer>() : null;
            if (srcMf == null || srcMr == null)
            {
                Plugin.Log?.LogWarning($"[ForesterPost] Graft '{name}' skipped: donor child has no mesh");
                return;
            }

            var go = new GameObject(name);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = src.localPosition + offset;
            go.transform.localRotation = src.localRotation;
            go.transform.localScale = src.localScale;
            go.AddComponent<MeshFilter>().sharedMesh = srcMf.sharedMesh;
            go.AddComponent<MeshRenderer>().sharedMaterials = srcMr.sharedMaterials;
        }

        /// <summary>
        ///     The post ignores damage.
        ///
        ///     <para>It stands in the middle of a woodlot whose whole purpose is dropping
        ///     trees, and a falling trunk destroys what it lands on — verified, a felled beech
        ///     flattened a bush and dropped its wood. A post that its own Lumberjack knocks
        ///     down within the hour is worse than useless: losing it silently drops the
        ///     village's <c>forester</c> anchor, which un-bakes the grove and strands the
        ///     Lumberjack with no woodlot at all.</para>
        ///
        ///     <para>Immunity is on the DAMAGE path only. The Hammer's remove uses Piece
        ///     removal, not damage, so the player can still take it down deliberately.</para>
        /// </summary>
        private static void MakeIndestructible(GameObject prefab)
        {
            var wnt = prefab.GetComponent<WearNTear>();
            if (wnt == null) return;

            const HitData.DamageModifier immune = HitData.DamageModifier.Immune;
            wnt.m_damages = new HitData.DamageModifiers
            {
                m_blunt = immune,
                m_slash = immune,
                m_pierce = immune,
                m_chop = immune,
                m_pickaxe = immune,
                m_fire = immune,
                m_frost = immune,
                m_lightning = immune,
                m_poison = immune,
                m_spirit = immune,
            };

            // Structural decay too: it is deliberately out in the open with no roof and
            // nothing to support it, which is exactly what those two rules punish.
            wnt.m_noRoofWear = true;
            wnt.m_noSupportWear = true;
            wnt.m_burnable = false;
        }
    }

    /// <summary>
    ///     Binds a placed post to the nearest village as a named anchor, then asks for a
    ///     repartition so the bake actually extends out here.
    /// </summary>
    public class ForesterPostAnchor : MonoBehaviour
    {
        /// <summary>
        ///     How far out a post may stand. Generous, because the whole point of a woodlot is
        ///     to be far enough from the walls that a falling trunk cannot reach them — 50m
        ///     turned out to be closer than players actually want to fell.
        /// </summary>
        private const float MaxDistanceFromVillage = 80f;

        /// <summary>
        ///     Maximum gap between consecutive anchors on the way out to the post.
        ///
        ///     <para>The bake covers <c>RegionBuildRadius</c> (30m) around each anchor and
        ///     nothing in between, so two anchors more than 60m apart leave a hole with no
        ///     navmesh — the grove gets baked, is not connected to the village, and the
        ///     Lumberjack is stranded at the gate. That is the real constraint the old 50m cap
        ///     was standing in for. Rather than refuse the distance, the post lays stepping
        ///     stones: extra anchors along the line home, close enough that the discs overlap
        ///     with margin.</para>
        /// </summary>
        private const float CorridorStep = 40f;

        /// <summary>Name prefix for those stepping stones; re-derived on every placement.</summary>
        public const string LinkAnchorPrefix = "forester_link";

        /// <summary>How many times registration is retried, and how far apart.</summary>
        private const int RegisterAttempts = 6;

        private const float RegisterRetrySeconds = 10f;

        private int m_attempts;

        private void Awake()
        {
            // Retried, not one-shot. On a server BOOT the post instantiates as its zone loads,
            // which can happen before the village record is readable — and a post that fails
            // here used to stay failed until the player rebuilt it by hand, silently keeping
            // whatever stale anchor the record already held. Observed live: a seed resolved by
            // an older build sat on top of the woodpile through two restarts because Awake
            // never got another go.
            if (!TryRegister()) InvokeRepeating(nameof(RetryRegister), RegisterRetrySeconds,
                RegisterRetrySeconds);
        }

        private void RetryRegister()
        {
            // TryRegister FIRST: `++attempts >= max || TryRegister()` short-circuits on the
            // last attempt and spends it without trying.
            var done = TryRegister();
            if (done || ++m_attempts >= RegisterAttempts)
                CancelInvoke(nameof(RetryRegister));
        }

        private bool TryRegister()
        {
            var nview = GetComponent<ZNetView>();
            if (nview == null || !nview.IsValid()) return false;

            // "Not up yet" and "not my job" are NOT the same answer, and collapsing them is
            // what made the retry useless: on a cold boot the post instantiates while ZNet is
            // still starting, and reporting that as "done" meant no retry was ever scheduled,
            // nothing was logged, and the stale anchor survived every restart.
            if (ZNet.instance == null) return false;
            // Host-authoritative, like every other village ZDO writer.
            if (!ZNet.instance.IsServer()) return true;

            var pos = transform.position;
            var village = VillageRegistry.GetVillageAt(pos);
            if (village == null)
            {
                Plugin.Log?.LogWarning(
                    $"[ForesterPost] No village near ({pos.x:F1},{pos.z:F1}) — post has no effect " +
                    "until one exists. Place a Village Registry first.");
                return false;
            }

            var d = Vector3.Distance(pos, village.Anchor);
            if (d > MaxDistanceFromVillage)
            {
                Plugin.Log?.LogError(
                    $"[ForesterPost] Placed {d:F0}m from the village, beyond the " +
                    $"{MaxDistanceFromVillage:F0}m limit — even with a corridor of anchors the walk " +
                    "out would be longer than the Lumberjack's legs are worth. Move it closer.");
                return true; // a placement mistake, not a race — retrying changes nothing
            }

            // The post stands in its own colliders, so its literal position is not walkable —
            // the same trap the registry station hit. Seeding the reachability flood there
            // walls the grove off from itself. Resolve a clear standing point first.
            if (!RegistrySeedResolver.TryResolveWalkableSeed(pos, out var seed, ForesterPost.SeedClearance))
            {
                Plugin.Log?.LogError(
                    $"[ForesterPost] No walkable ground within reach of ({pos.x:F1},{pos.z:F1}) — " +
                    "the post is boxed in by terrain or pieces. The grove would have no seed to " +
                    "flood from; move it somewhere a villager could stand.");
                return true; // the ground will not change either
            }

            // Re-derived every placement: a post that moved leaves its old stepping stones
            // baking navmesh to nowhere.
            village.RemoveAnchorsWithPrefix(LinkAnchorPrefix);

            if (!LayCorridor(village, seed, out var laid))
                return true;

            if (!village.SetAnchor(ForesterPost.AnchorName, seed))
            {
                // The host has not claimed the village ZDO yet. Say so and come back: writing
                // nothing while reporting success is how a stale anchor outlived three
                // restarts.
                Plugin.Log?.LogInfo(
                    "[ForesterPost] Village record not writable yet (host has not claimed it); " +
                    "will retry the anchor shortly.");
                return false;
            }

            Plugin.Log?.LogInfo(
                $"[ForesterPost] Registered anchor '{ForesterPost.AnchorName}' for village " +
                $"{village.VillageId} at ({seed.x:F1},{seed.y:F1},{seed.z:F1}) " +
                $"(post at ({pos.x:F1},{pos.y:F1},{pos.z:F1}), seed offset {Vector3.Distance(seed, pos):F1}m), " +
                $"{d:F0}m from the village anchor via {laid} corridor anchor(s)");

            RequestRepartition(village);
            return true;
        }

        /// <summary>
        ///     Lay stepping-stone anchors between the village and the post so the baked
        ///     navmesh is continuous the whole way out. Returns false — having laid nothing —
        ///     when any stone has no walkable ground under it, because a corridor with a hole
        ///     in it strands the Lumberjack exactly as surely as no corridor at all.
        /// </summary>
        private static bool LayCorridor(Village village, Vector3 seed, out int laid)
        {
            laid = 0;

            // Along the same line the lane is carved on — from the gate, not the registry.
            // Stones laid from the middle of the settlement land inside the wall, where
            // CorridorRoute now (correctly) discards them, so they would be anchors spent on
            // nothing while the lane still had to cross the perimeter on its own.
            var home = ForesterPost.CorridorStart(village, seed, village.Graph?.GetGates());
            var distance = Vector3.Distance(home, seed);
            if (distance <= CorridorStep) return true;

            var gaps = Mathf.CeilToInt(distance / CorridorStep);

            for (var i = 1; i < gaps; i++)
            {
                var point = Vector3.Lerp(home, seed, (float)i / gaps);
                if (!RegistrySeedResolver.TryResolveWalkableSeed(point, out var linkSeed))
                {
                    Plugin.Log?.LogError(
                        $"[ForesterPost] No walkable ground at ({point.x:F1},{point.z:F1}) on the way " +
                        "out to the post — the navmesh corridor would have a hole in it and the " +
                        "Lumberjack could not walk to the grove. Move the post, or bridge the gap.");
                    village.RemoveAnchorsWithPrefix(LinkAnchorPrefix);
                    return false;
                }

                village.SetAnchor($"{LinkAnchorPrefix}{i}", linkSeed);
                laid++;
            }

            return true;
        }

        /// <summary>
        ///     The anchor only changes anything once a partition re-reads it, so ask for one
        ///     immediately rather than leaving the post inert until something else triggers a
        ///     rebuild.
        /// </summary>
        private static void RequestRepartition(Village village)
        {
            var id = village.VillageId;
            if (string.IsNullOrEmpty(id)) return;

            var anchor = village.Anchor;
            GlobalTaskQueue.Enqueue(new VillagerTask
            {
                Name = "hna_partition",
                SourceId = "forester_post",
                Priority = TaskPriority.High,
                // Without this the field defaults to 0 and the task is dropped on its first
                // pass through the queue — observed verbatim: "Task 'hna_partition' for
                // forester_post timed out after 0.1s (limit 0s)". The post then wrote its
                // anchor and nothing ever rebuilt on it, so a woodlot whose carve had gone
                // wrong stayed wrong until something else happened to trigger a partition.
                // Longer than the 60s a hand-typed vv_repartition gets, because this one
                // usually fires while zones are still streaming in and the partition's
                // readiness precondition defers (PartitionReady: "piece not instantiated in
                // zone ... prefab=Rock_4") against this same deadline.
                TimeoutSeconds = 120f,
                Attributes = new Dictionary<string, string>
                {
                    { "village_id", id },
                    { "anchor_x", anchor.x.ToString("F2", CultureInfo.InvariantCulture) },
                    { "anchor_z", anchor.z.ToString("F2", CultureInfo.InvariantCulture) },
                },
            });
        }
    }
}
