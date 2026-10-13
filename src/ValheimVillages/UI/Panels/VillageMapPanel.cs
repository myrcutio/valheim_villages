using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Behaviors.Patrol;
using ValheimVillages.UI.Interaction;
using ValheimVillages.Villager.AI.Navigation;

namespace ValheimVillages.UI.Panels
{
    /// <summary>
    ///     Renders the per-task map for the Tasks tab. The map shows the village's
    ///     ACTUAL operable area — the region graph's lookup cells, the same points
    ///     <see cref="RegionGraph.PointToRegionId"/> resolves — identically for every
    ///     villager type, so the map faithfully represents where the villager can
    ///     operate (and surfaces any coverage holes / boundary-detection issues)
    ///     rather than a per-type derived shape. A patroller's route, the villager,
    ///     the anchor, gates, and task pins are drawn as overlays on top.
    /// </summary>
    public static class VillageMapPanel
    {
        // Detected gate/door markers on the village map. Cyan reads clearly
        // against the tan perimeter and the warmer task pins.
        private static readonly Color GatePinColor = new(0.25f, 0.85f, 0.95f, 1f);

        /// <summary>
        ///     Render a per-task map for a villager, plus the interactive pins laid over it.
        ///     Returns null if no useful map can be drawn.
        /// </summary>
        /// <param name="selfRow">
        ///     Row in the Tasks tab this map belongs to, so a task pin can point back at its own
        ///     entry. -1 when the map is not tied to a row.
        /// </param>
        public static MapRender RenderForTask(
            VillagerBehaviorBridge villager,
            IReadOnlyList<TaskPin> pins,
            int selfRow = -1)
        {
            if (villager == null) return null;

            var ai = villager.AI;
            if (ai == null) return null;

            var anchor = ai.HomeAnchor;
            var graph = Villages.Entity.VillageRegistry.GraphAt(anchor);
            if (graph == null) return null; // no village graph here — no map to draw

            // Source of truth for the operable area: the graph's lookup cells.
            var cells = graph.Diagnostics.GetLookupCellCenters();

            // Patrol route (if any) is drawn as an OVERLAY, not the footprint.
            var waypoints = ai.GetBehavior<PerimeterPatrolBehavior>()?.PatrolWaypoints;
            var villagerPos = ai.Position;

            var gated = WithGatePins(villager, pins, cells);

            // The renderer only needs position+colour to draw; labels and click targets are
            // this layer's business.
            // Only pins we have no icon for keep their baked circle. The rest are drawn as
            // real sprites by the overlay, and a circle underneath would show as a halo.
            var drawn = new List<(Vector3 position, Color color)>(gated.Count);
            foreach (var p in gated)
                if (PinIcon(p) == null)
                    drawn.Add((p.Position, p.Color));

            // The villager, the registry and the cot are resolved HERE, before the render,
            // rather than at Add() time below — because a pin whose icon fails to resolve must
            // still get a baked dot, and extraPins is consumed by the render. Resolving them
            // afterwards is what left them able to vanish entirely: they never passed through
            // extraPins, so a null icon drew nothing at all and left an invisible hover target.
            var record = Villager.Records.VillagerRecordTable.FindById(villager.UniqueId);
            var villagerIcon = StationIcon(villager.VillagerType);
            var registryIcon = PieceIcon(Items.PieceFactory.RegistryPrefabName);
            var cotIcon = PieceIcon(Items.VillagerBedPiece.PrefabName);
            var quarters = record != null && record.IsValid
                ? Villager.Levels.VillagerQuarters.For(record)
                : default;

            if (villagerIcon == null) drawn.Add((villagerPos, VillagerDotColor));
            if (registryIcon == null) drawn.Add((anchor, AnchorDotColor));
            if (cotIcon == null && quarters.HasCot)
                drawn.Add((quarters.CotPosition, CotDotColor));

            var frame = new MapFrame();
            var tex = PatrolMapRenderer.Render(
                waypoints,
                anchor,
                villagerPos,
                cells,
                cellSize: RegionGraph.LookupCellSize,
                extraPins: drawn,
                playerFacing: true,
                frameOut: frame,
                drawOwnDots: false,
                cellColors: VillageSurfaceMap.ColorsFor(graph, cells),
                boundary: BoundaryFor(graph));

            if (tex == null) return null;

            var render = new MapRender { Texture = tex };
            var who = record != null && record.IsValid ? record.Name : villager.VillagerType;

            // The village registry is always on the map, drawn by the renderer rather than
            // passed in as a pin, so it is described here.
            Add(render, frame, anchor, "Village registry", icon: registryIcon,
                shortLabel: "Registry");

            // Only pins that stand for a TASK route anywhere. A gate and the player's own
            // marker are landmarks — clicking them to open a work row would be a non sequitur.
            foreach (var p in gated)
            {
                var landmark = p.Landmark || p.Label == GateLabel || p.Label == PlayerLabel ||
                               p.Label == PatrolLabel;

                // A pin carrying its own row opens THAT row — which is what lets the default
                // row show every task at once and still send each icon to its own entry.
                // -1 means "the row this map belongs to".
                var row = p.Row >= 0 ? p.Row : selfRow;
                Add(render, frame, p.Position, p.Label,
                    landmark ? null : TasksTabName,
                    landmark ? -1 : row,
                    PinIcon(p),
                    p.Label == PlayerLabel ? PlayerFacingAngle() : 0f,
                    shortLabel: landmark ? p.Label : ShortTaskLabel(p.Label),
                    orderItem: p.OrderItem, orderStation: p.OrderStation);
            }

            AddCotPin(render, frame, record, quarters, cotIcon);

            SpreadCoincidentPins(render);

            // The villager goes on LAST, so their marker sits on top of everything else.
            // Pin order is child order is draw order, and being added first put the subject of
            // the whole panel underneath the gates, the registry and their own cot — any of
            // which could cover it completely on a village this dense.
            Add(render, frame, villagerPos,
                string.IsNullOrEmpty(who) ? "This villager" : who,
                StatusTabName, StatusSummaryRow,
                villagerIcon, backdrop: Core.UiSprites.Crest(),
                shortLabel: villager.VillagerType);

            return render;
        }

        /// <summary>Tab/row names the pins route to. Kept here so the strings have one home.</summary>
        private const string StatusTabName = "Status";

        private const string TasksTabName = "Tasks";
        private const string GateLabel = "Gate";

        /// <summary>The player's own pin. Named here so it can be excluded from click routing.</summary>
        private const string PlayerLabel = "You";

        /// <summary>
        ///     Label for a patroller's next waypoint. Public because <c>InfoTab</c> tags the pin
        ///     with it and this is the one place that decides what counts as a landmark —
        ///     having the two agree by sharing a constant beats agreeing by coincidence.
        /// </summary>
        public const string PatrolLabel = "Patrol route";

        /// <summary>Row indices inside StatusTab — see its own row constants.</summary>
        private const int StatusSummaryRow = 0;

        private const int StatusQuartersRow = 1;

        /// <summary>
        ///     Pin the villager's own cot. Not part of the task pins because it is not about the
        ///     current task at all — it is the thing the player most often wants to FIND, and
        ///     clicking it jumps to the quarters row that explains what the room still needs.
        /// </summary>
        private static void AddCotPin(
            MapRender render, MapFrame frame, Villager.Records.VillagerRecord record,
            Villager.Levels.QuartersState quarters, Sprite cotIcon)
        {
            if (record == null || !record.IsValid || !quarters.HasCot) return;

            Add(render, frame, quarters.CotPosition,
                $"{record.Name}'s cot — comfort {quarters.Comfort}/{quarters.ComfortTarget}",
                StatusTabName, StatusQuartersRow, cotIcon, shortLabel: "Cot");
        }

        /// <summary>
        ///     Fallback dot colours, used only when an icon fails to resolve. Every pin is then
        ///     either an authored sprite or a visible dot — never nothing, which is what an
        ///     icon-only pin degraded to before.
        /// </summary>
        private static readonly Color VillagerDotColor = new(0.3f, 0.6f, 1f, 1f);

        private static readonly Color AnchorDotColor = new(1f, 0.9f, 0.3f, 1f);
        private static readonly Color CotDotColor = new(0.85f, 0.65f, 0.95f, 1f);

        private static void Add(
            MapRender render, MapFrame frame, Vector3 world, string label,
            string targetTab = null, int targetRow = -1,
            Sprite icon = null, float rotation = 0f, Sprite backdrop = null,
            string shortLabel = null, string orderItem = null, string orderStation = null)
        {
            if (string.IsNullOrEmpty(label)) return;

            // Matches the baked dots in PatrolMapRenderer.DrawPin: off-frame pins are hidden
            // once the view is zoomed or panned, rather than stacking against the border.
            var offFrame = !frame.Contains(world);
            if (offFrame && MapViewState.Active) return;

            render.Pins.Add(new MapPin(
                world, frame.Normalize(world), label, targetTab, targetRow,
                offFrame, icon, rotation, backdrop, shortLabel,
                orderItem, orderStation));
        }

        /// <summary>Hammer-menu icon for a build piece, by prefab name.</summary>
        /// <summary>
        ///     Chip text for a task pin. The pin's label is already the item or task name, which
        ///     is more use on a crowded map than five chips all reading "Task"; the generic word
        ///     is only a fallback for a task that could not name itself.
        /// </summary>
        private static string ShortTaskLabel(string label)
        {
            return string.IsNullOrEmpty(label) ? "Task" : label;
        }

        /// <summary>How close two pins must be, in normalised map units, to count as stacked.</summary>
        private const float PinOverlapRadius = 0.035f;

        /// <summary>How far apart to fan a stack, in normalised map units.</summary>
        private const float PinSpreadRadius = 0.045f;

        /// <summary>
        ///     Nudge pins that land on the same spot into a small ring around it.
        ///
        ///     <para>Several work orders routinely share one chest — a village filing Carrot
        ///     and CarrotSeeds into the same box produces two pins at an identical position,
        ///     and the one underneath is both invisible and unclickable. Fanning them keeps
        ///     every order reachable without moving any of them far enough to point at the
        ///     wrong chest.</para>
        ///
        ///     <para>Applied last, after every pin exists, because the stack is only knowable
        ///     once they have all been placed.</para>
        /// </summary>
        private static void SpreadCoincidentPins(MapRender render)
        {
            var pins = render.Pins;
            var handled = new bool[pins.Count];

            for (var i = 0; i < pins.Count; i++)
            {
                if (handled[i]) continue;

                var cluster = new List<int> { i };
                for (var j = i + 1; j < pins.Count; j++)
                {
                    if (handled[j]) continue;
                    if (Vector2.Distance(pins[i].Normalized, pins[j].Normalized) > PinOverlapRadius)
                        continue;

                    cluster.Add(j);
                    handled[j] = true;
                }

                handled[i] = true;
                if (cluster.Count < 2) continue;

                // Ring around the shared point, first pin included so the stack stays centred
                // on the thing it refers to rather than drifting off it.
                var centre = pins[i].Normalized;
                for (var k = 0; k < cluster.Count; k++)
                {
                    var angle = 2f * Mathf.PI * k / cluster.Count;
                    var at = centre + new Vector2(
                        Mathf.Cos(angle) * PinSpreadRadius,
                        Mathf.Sin(angle) * PinSpreadRadius);

                    var p = pins[cluster[k]];
                    pins[cluster[k]] = new MapPin(
                        p.World,
                        new Vector2(Mathf.Clamp01(at.x), Mathf.Clamp01(at.y)),
                        p.Label, p.TargetTab, p.TargetRow, p.OffFrame, p.Icon, p.Rotation,
                        p.Backdrop, p.ShortLabel, p.OrderItem, p.OrderStation);
                }
            }
        }

        /// <summary>Resolved icons, by villager type or prefab name. Hits only (see PieceIcon).</summary>
        private static readonly Dictionary<string, Sprite> s_iconCache = new();

        /// <summary>
        ///     The sprite a supplied pin draws with: its own icon when the caller had one (a
        ///     work order's item, a gate's door), else the player's arrow, else nothing — and
        ///     nothing means it keeps the dot the renderer baked.
        /// </summary>
        private static Sprite PinIcon(TaskPin pin)
        {
            if (pin.Icon != null) return pin.Icon;
            return pin.Label == PlayerLabel ? PlayerArrow() : null;
        }

        /// <summary>The villager's station icon, cached by type.</summary>
        private static Sprite StationIcon(string villagerType)
        {
            if (string.IsNullOrEmpty(villagerType)) return null;
            var key = "type:" + villagerType;
            if (s_iconCache.TryGetValue(key, out var cached)) return cached;

            var icon = Items.VillagerBedPrint.FindStationIcon(villagerType);

            // Hits only: a miss is usually "ObjectDB was not ready yet", and caching it would
            // leave this villager iconless for the session.
            if (icon != null) s_iconCache[key] = icon;
            return icon;
        }

        /// <summary>
        ///     The world map's own player arrow, taken off <c>Minimap.m_largeMarker</c> so the
        ///     villager map marks "you" with exactly the sprite the map screen does.
        /// </summary>
        private static Sprite PlayerArrow()
        {
            var marker = Minimap.instance != null ? Minimap.instance.m_largeMarker : null;
            var image = marker != null ? marker.GetComponentInChildren<UnityEngine.UI.Image>(true) : null;
            return image != null ? image.sprite : null;
        }

        /// <summary>
        ///     Player facing as a Z rotation, matching <c>Minimap.UpdatePlayerMarker</c>, which
        ///     sets the marker to <c>Euler(0, 0, -yaw)</c>.
        /// </summary>
        private static float PlayerFacingAngle()
        {
            var player = Player.m_localPlayer;
            return player != null ? -player.transform.rotation.eulerAngles.y : 0f;
        }

        /// <summary>How far from a gate marker to accept a door as the door that made it.</summary>
        private const float GateDoorMatch = 2.5f;

        /// <summary>
        ///     The build-menu icon of the actual door standing at a gate, so a wooden gate and
        ///     an iron one read differently. Matched by position because a gate marker is a
        ///     doorway MIDPOINT, not a prefab reference.
        /// </summary>
        private static Sprite GateIcon(Vector3 gate)
        {
            var key = $"gate:{Mathf.RoundToInt(gate.x)},{Mathf.RoundToInt(gate.z)}";
            if (s_iconCache.TryGetValue(key, out var cached)) return cached;

            Sprite best = null;
            var bestDist = GateDoorMatch * GateDoorMatch;
            foreach (var door in ValheimVillages.Patches.DoorRegistryPatch.LiveDoors())
            {
                if (door == null) continue;
                var d = (door.transform.position - gate).sqrMagnitude;
                if (d > bestDist) continue;

                var icon = door.GetComponent<Piece>()?.m_icon;
                if (icon == null) continue;
                bestDist = d;
                best = icon;
            }

            if (best != null) s_iconCache[key] = best;
            return best;
        }

        /// <summary>
        ///     The village's detected wall, as (centre, outward) pairs for the barricade. These
        ///     are the cells where the outside flood was stopped, so they trace the real
        ///     perimeter rather than a hull drawn around it.
        /// </summary>
        private static List<(Vector3 center, Vector3 outward)> BoundaryFor(RegionGraph graph)
        {
            var cells = graph?.GetBoundaryCells();
            if (cells == null || cells.Count == 0) return null;

            var list = new List<(Vector3, Vector3)>(cells.Count);
            foreach (var (_, center, outward) in cells) list.Add((center, outward));
            return list;
        }

        private static Sprite PieceIcon(string prefabName)
        {
            if (string.IsNullOrEmpty(prefabName)) return null;
            var key = "piece:" + prefabName;
            if (s_iconCache.TryGetValue(key, out var cached)) return cached;

            var piece = ZNetScene.instance?.GetPrefab(prefabName)?.GetComponent<Piece>();
            var icon = piece != null ? piece.m_icon : null;

            // Only cache a hit: a miss here usually means ZNetScene was not ready yet, and
            // caching that would leave the pin iconless for the rest of the session.
            if (icon != null) s_iconCache[key] = icon;
            return icon;
        }

        /// <summary>
        ///     How close two gate markers must be to be treated as one gateway. A double gate's
        ///     two halves sit about 2m apart; separate gateways in a wall are much further.
        /// </summary>
        private const float GatePairDistance = 3f;

        /// <summary>
        ///     Collapse gates that form one gateway into a single marker at their centre.
        ///
        ///     <para>Gates are routinely built in pairs as double doors, and the partition
        ///     seals each leaf separately — so one gateway produced two markers a metre or two
        ///     apart, drawn almost on top of each other. One gateway should read as one thing.</para>
        ///
        ///     <para>Done BEFORE pins are built, so the later coincident-pin fanning does not
        ///     helpfully push the two halves back apart again.</para>
        /// </summary>
        private static List<Vector3> MergeGatePairs(List<Vector3> gates)
        {
            var result = new List<Vector3>();
            var taken = new bool[gates.Count];

            for (var i = 0; i < gates.Count; i++)
            {
                if (taken[i]) continue;
                taken[i] = true;

                var sum = gates[i];
                var count = 1;

                for (var j = i + 1; j < gates.Count; j++)
                {
                    if (taken[j]) continue;
                    if (Vector3.Distance(gates[i], gates[j]) > GatePairDistance) continue;

                    sum += gates[j];
                    count++;
                    taken[j] = true;
                }

                result.Add(sum / count);
            }

            return result;
        }

        /// <summary>How close a sealed gate must be to walkable village floor to be ITS gate.</summary>
        private const float GateNearCellXZ = 4f;

        /// <summary>Vertical tolerance for the same test — a gate is on the floor it opens onto.</summary>
        private const float GateNearCellY = 3f;

        /// <summary>
        ///     Append a pin for every gate the partition sealed into this villager's village
        ///     boundary — but only gates that actually touch the village.
        ///
        ///     <para><c>RubberBandPrune.GatherGateSeals</c> selects doors with a pure XZ
        ///     rectangle test against the bake bounds and no elevation test at all. Those bounds
        ///     are the village FOOTPRINT, which runs far wider than the village itself (here
        ///     198x160m around a village whose boundary is 37x38m), so every door standing
        ///     anywhere in that rectangle is sealed as a gate — including world-generated ruins.
        ///     One such door, 109m north-east and 24m BELOW the village floor, was being pinned
        ///     on the map as if it were a village gate.</para>
        ///
        ///     <para>Harmless to the flood it was gathered for — sealing a doorway nobody floods
        ///     through changes nothing — but wrong on a map, so the map asks the stricter
        ///     question: is this gate within reach of floor this village can actually walk?
        ///     Filtered here rather than in the prune because the seal set feeds the Pass 1
        ///     flood that stops an open gate collapsing the whole village, and that is not
        ///     something to retune for a cosmetic fix.</para>
        /// </summary>
        private static List<TaskPin> WithGatePins(
            VillagerBehaviorBridge villager,
            IReadOnlyList<TaskPin> pins,
            List<Vector3> cells)
        {
            var anchor = villager.AI?.HomeAnchor ?? Vector3.zero;
            var graph = Villages.Entity.VillageRegistry.GraphAt(anchor);
            var merged = new List<TaskPin>();
            if (pins != null) merged.AddRange(pins);

            var gates = graph?.GetGates();
            if (gates == null || gates.Count == 0) return merged;

            var kept = new List<Vector3>();
            foreach (var g in gates)
                if (TouchesVillageFloor(g, cells))
                    kept.Add(g);

            foreach (var g in MergeGatePairs(kept))
                merged.Add(new TaskPin(g, GatePinColor, GateLabel, GateIcon(g)));

            return merged;
        }

        /// <summary>True when any village lookup cell sits within a gate's reach, in 3D.</summary>
        private static bool TouchesVillageFloor(Vector3 gate, List<Vector3> cells)
        {
            if (cells == null) return false;

            foreach (var cell in cells)
            {
                if (Mathf.Abs(cell.y - gate.y) > GateNearCellY) continue;
                var dx = cell.x - gate.x;
                var dz = cell.z - gate.z;
                if (dx * dx + dz * dz <= GateNearCellXZ * GateNearCellXZ) return true;
            }

            return false;
        }
    }
}
