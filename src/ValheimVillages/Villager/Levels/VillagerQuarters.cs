using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages.Items;
using ValheimVillages.Schemas;
using ValheimVillages.Villager.Records;

namespace ValheimVillages.Villager.Levels
{
    /// <summary>
    ///     What a villager's own quarters are worth: the cot assigned to them, whether the room
    ///     around it is habitable, and how well furnished it is.
    ///
    ///     <para>This is the ladder's first two rungs. A villager with no cot has nowhere to
    ///     live and only idles; a cot in a room that meets a WORKBENCH's requirements earns the
    ///     first level; furnishing that room past bare shelter earns the second.</para>
    ///
    ///     <para>Both measurements are the engine's own, taken at the cot:
    ///     <see cref="Cover.GetCoverForPoint" /> is what <c>CraftingStation.CheckUsable</c> uses
    ///     to decide a workbench is neither roofless nor too exposed, and
    ///     <see cref="SE_Rested.CalculateComfortLevel(bool, Vector3)" /> is what gives the player
    ///     their rested buff. Borrowing them rather than inventing thresholds means a room that
    ///     "feels like" a bedroom to the player is exactly the room that counts.</para>
    ///
    ///     <para><b>Why the cot and not the registry.</b> Comfort was sampled at the villager's
    ///     home anchor once before and abandoned: the anchor is the registry, which usually
    ///     stands outdoors, so every villager read comfort 1 however good the village was. A cot
    ///     is placed deliberately, indoors, in one room — it is the one point that can answer
    ///     "where does this villager actually live".</para>
    /// </summary>
    public static class VillagerQuarters
    {
        /// <summary>
        ///     Cover fraction a room must have at the cot. Valheim's own workbench threshold —
        ///     below it a station reports <c>$msg_stationtooexposed</c>.
        /// </summary>
        public const float RequiredCoverage = 0.7f;

        /// <summary>
        ///     Comfort of a bare sheltered room, before a single furnishing.
        ///     <see cref="SE_Rested.CalculateComfortLevel(bool, Vector3)" /> starts at 1 and adds
        ///     1 simply for being in shelter, so this is what an empty roofed box scores.
        /// </summary>
        public const int ShelterBaseline = 2;

        public const int LevelNoQuarters = 0;
        public const int LevelSheltered = 1;
        public const int LevelFurnished = 2;
        public const int LevelPeered = 3;

        /// <summary>
        ///     Comfort a room must reach for the second level. Set by hand at 7, not derived.
        ///
        ///     <para>It has to sit well clear of <see cref="ShelterBaseline" />: an empty roofed
        ///     box already scores 2, so anything at or near that would hand out the furnished
        ///     level for building four walls. 7 is roughly a bed, a fire, a chair, a table and a
        ///     rug — a room somebody actually lives in.</para>
        ///
        ///     <para><c>minComfortLevel</c> on the definition still wins if a type authored a
        ///     HIGHER bar, so a type can ask for more but none can ask for less.</para>
        /// </summary>
        public const int FurnishedComfort = 7;

        public static int ComfortTarget(VillagerDef def)
        {
            return Mathf.Max(def?.minComfortLevel ?? 0, FurnishedComfort);
        }

        /// <summary>
        ///     How long a cot index is reused. Both the scheduler gate and the UI read quarters
        ///     every tick, and the honest measurement walks every Piece in the scene — doing that
        ///     per villager per frame is not affordable. A few seconds of staleness costs
        ///     nothing: a player furnishing a room sees the bar move on the next refresh, and
        ///     assigning a cot invalidates immediately.
        /// </summary>
        private const float IndexSeconds = 5f;

        /// <summary>recordId -> their cot. Rebuilt wholesale, so one scene walk serves everyone.</summary>
        private static readonly Dictionary<string, GameObject> s_cotByRecord = new();

        private static float s_indexedAt = float.NegativeInfinity;

        /// <summary>Drop the cot index. Called when an assignment changes.</summary>
        public static void Invalidate() => s_indexedAt = float.NegativeInfinity;

        private static void EnsureIndex()
        {
            if (Time.time - s_indexedAt < IndexSeconds) return;

            s_cotByRecord.Clear();
            foreach (var cot in VillagerBedPiece.PlacedCots())
            {
                var occupant = VillagerCotOccupant.GetRecordId(cot);
                if (string.IsNullOrEmpty(occupant)) continue;

                // Last one wins, and that is reported rather than hidden: two cots claiming the
                // same villager is an authoring mistake the player can fix, not something to
                // paper over by silently picking one.
                if (s_cotByRecord.ContainsKey(occupant))
                    Plugin.Log?.LogWarning(
                        $"[VillagerQuarters] more than one cot is assigned to record {occupant}");

                s_cotByRecord[occupant] = cot;
            }

            s_indexedAt = Time.time;
        }

        /// <summary>
        ///     The villager's standing: their room, then their neighbours.
        ///
        ///     <para>Cumulative, like the authored tiers. A villager stops at the first unmet
        ///     rung even if a later one happens to be satisfied, so a village cannot skip the
        ///     roof by recruiting a Carpenter.</para>
        /// </summary>
        public static QuartersState For(VillagerRecord record)
        {
            if (record == null || !record.IsValid) return QuartersState.None("no villager record");

            var def = Registry.VillagerRegistry.Get(record.Type);
            var cot = FindCot(record.RecordId);
            if (cot == null)
                return QuartersState.WithoutCot(Potential(def));

            var room = Measure(cot.transform.position, def, Potential(def));
            if (room.Level < LevelFurnished) return room; // an earlier rung blocks this one

            return room.WithPeers(MissingPeers(def, record.Village));
        }

        /// <summary>
        ///     The highest rung this type can reach. A type that depends on nobody tops out at
        ///     FURNISHED — reporting "2 / 3" forever for a villager with nothing left to earn
        ///     would read as a permanent deficiency.
        /// </summary>
        public static int Potential(VillagerDef def)
        {
            return def?.receivesLevelFrom != null && def.receivesLevelFrom.Count > 0
                ? LevelPeered
                : LevelFurnished;
        }

        /// <summary>
        ///     Types this villager draws a level from that the village has nobody alive for.
        ///     The third rung is a VILLAGE property, not a room one — a Farmer gains it the
        ///     moment any Carpenter is enrolled, wherever that Carpenter sleeps.
        /// </summary>
        public static List<string> MissingPeers(VillagerDef def, string villageId)
        {
            var missing = new List<string>();
            if (def?.receivesLevelFrom == null || def.receivesLevelFrom.Count == 0) return missing;

            var living = VillagerLevel.LivingTypesInVillage(villageId);
            foreach (var type in def.receivesLevelFrom)
                if (!living.Contains(type))
                    missing.Add(type);

            return missing;
        }

        /// <summary>
        ///     Measure a cot's ROOM directly, for the cot's own menu. Reports the first two
        ///     rungs only — the peer rung belongs to a villager, and an unassigned cot has none.
        /// </summary>
        public static QuartersState ForCot(GameObject cot, VillagerDef def)
        {
            if (cot == null) return QuartersState.None("no cot");
            return Measure(cot.transform.position, def, Potential(def));
        }

        private static QuartersState Measure(Vector3 position, VillagerDef def, int potential)
        {
            Cover.GetCoverForPoint(position, out var coverage, out var underRoof);
            var sheltered = underRoof && coverage >= RequiredCoverage;

            // inShelter is passed through rather than hardcoded: an unsheltered cot must not be
            // credited with the +1 that being indoors grants, or a cot in a field would read as
            // furnished the moment a bench was dropped beside it.
            var comfort = SE_Rested.CalculateComfortLevel(sheltered, position);
            var target = ComfortTarget(def);

            if (!underRoof)
                return new QuartersState(true, position, false, coverage, comfort, target,
                    LevelNoQuarters, potential, "their cot has no roof over it — build one");

            if (!sheltered)
                return new QuartersState(true, position, false, coverage, comfort, target,
                    LevelNoQuarters, potential,
                    $"their room is too exposed ({coverage:P0} covered, needs " +
                    $"{RequiredCoverage:P0}) — close the walls in");

            if (comfort < target)
                return new QuartersState(true, position, true, coverage, comfort, target,
                    LevelSheltered, potential,
                    $"comfort {comfort} of {target} — add furnishings to their room");

            return new QuartersState(true, position, true, coverage, comfort, target,
                LevelFurnished, potential, "");
        }

        /// <summary>
        ///     The placed cot assigned to this record, or null.
        ///
        ///     <para>Only LOADED cots can be found, and that is correct rather than a limitation:
        ///     both measurements raycast and scan live pieces, so an unloaded room cannot be
        ///     assessed at all. A villager in an unloaded zone is not running either, so nothing
        ///     reads this while it cannot be answered.</para>
        /// </summary>
        public static GameObject FindCot(string recordId)
        {
            if (string.IsNullOrEmpty(recordId)) return null;

            EnsureIndex();
            if (!s_cotByRecord.TryGetValue(recordId, out var cot)) return null;

            // A cot destroyed since the index was built leaves a dead Unity reference behind,
            // which is not null by ==. Treat it as gone rather than measuring a corpse.
            return cot != null ? cot : null;
        }
    }

    /// <summary>A villager's quarters, measured. <see cref="Level" /> is the first two rungs.</summary>
    public readonly struct QuartersState
    {
        public QuartersState(bool hasCot, Vector3 cotPosition, bool sheltered, float coverage,
            int comfort, int comfortTarget, int level, int potential, string blocker,
            IReadOnlyList<string> missingPeers = null)
        {
            HasCot = hasCot;
            CotPosition = cotPosition;
            Sheltered = sheltered;
            Coverage = coverage;
            Comfort = comfort;
            ComfortTarget = comfortTarget;
            Level = level;
            Potential = potential;
            Blocker = blocker;
            MissingPeers = missingPeers ?? System.Array.Empty<string>();
        }

        /// <summary>The highest rung this villager's type can reach.</summary>
        public int Potential { get; }

        /// <summary>
        ///     Types this villager draws a level from that nobody in the village is. Empty once
        ///     the peer rung is earned, or when the type depends on nobody.
        /// </summary>
        public IReadOnlyList<string> MissingPeers { get; }

        public bool AtPotential => Level >= Potential;

        /// <summary>
        ///     Resolve the peer rung on top of a furnished room. Separate from the room
        ///     measurement because it is a VILLAGE fact, not a geometric one.
        /// </summary>
        public QuartersState WithPeers(List<string> missing)
        {
            if (Potential < VillagerQuarters.LevelPeered) return this; // nothing to earn

            if (missing == null || missing.Count == 0)
                return new QuartersState(HasCot, CotPosition, Sheltered, Coverage, Comfort,
                    ComfortTarget, VillagerQuarters.LevelPeered, Potential, "");

            var names = string.Join(", ", missing.ToArray());
            return new QuartersState(HasCot, CotPosition, Sheltered, Coverage, Comfort,
                ComfortTarget, VillagerQuarters.LevelFurnished, Potential,
                $"needs a {names} living in the village", missing);
        }

        public bool HasCot { get; }
        public Vector3 CotPosition { get; }

        /// <summary>Roofed AND covered enough — the same bar a workbench has to clear.</summary>
        public bool Sheltered { get; }

        public float Coverage { get; }
        public int Comfort { get; }

        /// <summary>Comfort needed for <see cref="VillagerQuarters.LevelFurnished" />.</summary>
        public int ComfortTarget { get; }

        public int Level { get; }

        /// <summary>
        ///     What to go and do, empty when nothing is wanted. Names the problem AND the fix, so
        ///     a player reading it in the UI never has to guess what "level 1" is waiting on.
        /// </summary>
        public string Blocker { get; }

        /// <summary>A villager with no quarters only idles — they have nowhere to live yet.</summary>
        public bool CanWork => Level >= VillagerQuarters.LevelSheltered;

        /// <summary>
        ///     Progress toward the furnished level, 0..1, for the comfort bar. Measured from the
        ///     bare-shelter baseline rather than from zero: the stretch the player can actually
        ///     influence by furnishing is baseline..target, and starting the bar at zero would
        ///     show it already most of the way full the moment a roof went up.
        /// </summary>
        public float ComfortProgress
        {
            get
            {
                // No cot means no progress, not complete progress. The unmeasured state carries
                // a zero target, and dividing by that span read as a FULL bar — a villager with
                // nowhere to live showed the same bar as one in a finished bedroom.
                if (!HasCot || ComfortTarget <= 0) return 0f;

                var span = ComfortTarget - VillagerQuarters.ShelterBaseline;
                if (span <= 0) return 1f;
                return Mathf.Clamp01((Comfort - VillagerQuarters.ShelterBaseline) / (float)span);
            }
        }

        /// <summary>
        ///     The comfort bar, as rich text. Shared so the cot's menu and the villager's own
        ///     panel cannot disagree about how full it is.
        ///
        ///     <para>Shows the RAW comfort either side of the slash, not the baseline-relative
        ///     numbers the bar is filled from — the player sees "4 / 7" and can compare it with
        ///     the comfort their own character reports, while the fill still only measures the
        ///     stretch they can change.</para>
        /// </summary>
        public string ComfortBar(int width = 10)
        {
            if (!HasCot) return "<color=#9FB4C7>no quarters</color>";

            var filled = Mathf.RoundToInt(ComfortProgress * width);
            var done = Comfort >= ComfortTarget;
            var colour = done ? "#6FCF6F" : "#FFE300";

            return $"<color={colour}>{new string('\u25A0', filled)}</color>" +
                   $"<color=#3A3A3A>{new string('\u25A1', width - filled)}</color> " +
                   $"<color={colour}>{Comfort}</color><color=#9FB4C7> / {ComfortTarget}</color>";
        }

        public static QuartersState None(string blocker)
        {
            return new QuartersState(false, Vector3.zero, false, 0f, 0, 0,
                VillagerQuarters.LevelNoQuarters, VillagerQuarters.LevelFurnished, blocker);
        }

        public static QuartersState WithoutCot(int potential)
        {
            return new QuartersState(false, Vector3.zero, false, 0f, 0, 0,
                VillagerQuarters.LevelNoQuarters, potential,
                "no cot — place a Villager Cot and assign it to them");
        }
    }
}
