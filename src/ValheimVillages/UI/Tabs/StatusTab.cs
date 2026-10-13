using System.Collections.Generic;
using System.Text;
using ValheimVillages.Attributes;
using ValheimVillages.Behaviors.Relax;
using ValheimVillages.Enums;
using ValheimVillages.Schemas;
using ValheimVillages.UI.Core;
using ValheimVillages.UI.Interaction;
using ValheimVillages.Villager.Levels;
using ValheimVillages.Villager.Records;

namespace ValheimVillages.UI.Tabs
{
    /// <summary>
    ///     Tab exposing what a villager IS rather than what it is doing: its level, how far it
    ///     could go and what is holding it back, plus its mood.
    ///
    ///     <para>The level half exists because a derived level is invisible without it — a
    ///     villager's level comes from its room and its neighbours, so unlike a station the
    ///     player cannot see the upgrade they built. Every unmet step therefore names the thing
    ///     to go and do, not the condition that failed.</para>
    /// </summary>
    [RegisterTab("status", Order = 0)]
    public class StatusTab : IVillagerTabUI
    {
        /// <summary>Colour for a drive that has reached the point of acting on it.</summary>
        private const string ElevatedColor = "#FFC24A";

        /// <summary>Colour for a step already met.</summary>
        private const string MetColor = "#8FD18F";

        /// <summary>Colour for a rung not yet reachable — the thing the player can go build.</summary>
        private const string LockedColor = "#6E7B88";

        private const int SummaryRow = 0;
        private const int QuartersRow = 1;
        private const int MoodRow = 2;

        /// <summary>Cells in a drive bar. Eight reads as a bar without being noisy at 0.</summary>
        private const int BarCells = 8;

        private VillagerLevelState m_level;
        private VillagerDef m_def;
        private DriveState m_drives;
        private bool m_hasRecord;

        public string TabName => "Status";

        public void OnSelected(VillagerBehaviorBridge villager) => Refresh(villager);

        public void OnUpdate(VillagerBehaviorBridge villager) => Refresh(villager);

        public void OnDeselected()
        {
            m_level = default;
            m_def = null;
            m_drives = null;
            m_hasRecord = false;
        }

        /// <summary>
        ///     Re-reads level and mood. Both are derived from the world (comfort pieces, the
        ///     village roster, the owner's drive mirror), so they are sampled here and reused by
        ///     <see cref="GetListItems" /> / <see cref="GetDetail" /> rather than recomputed per
        ///     call the way the row builders would otherwise do several times a frame.
        /// </summary>
        private void Refresh(VillagerBehaviorBridge villager)
        {
            if (villager == null)
            {
                OnDeselected();
                return;
            }

            m_def = Villager.Registry.VillagerRegistry.Get(villager.VillagerType);

            var record = VillagerRecordTable.FindById(villager.UniqueId);
            m_hasRecord = record != null && record.IsValid;
            m_level = m_hasRecord ? VillagerLevel.Evaluate(record) : default;
            m_quarters = m_hasRecord ? VillagerQuarters.For(record) : default;

            m_drives = VillagerDriveMirror.ForDisplay(villager.AI?.NView, villager.UniqueId);
        }

        /// <summary>
        ///     One row per thing that contributes to this villager, summary first. Splitting it
        ///     up means each row answers a single question the player can act on, instead of one
        ///     Level row carrying quarters, tiers and steps in a single wall of text.
        /// </summary>
        public List<TabListItemUI> GetListItems(VillagerBehaviorBridge villager)
        {
            return new List<TabListItemUI>
            {
                new() { TabName = LevelRowLabel() },
                new() { TabName = "Quarters: " + QuartersRowLabel() },
                new() { TabName = "Mood: " + MoodLabel() },
            };
        }

        /// <summary>The quarters row at a glance: the thing that is wrong, or the comfort score.</summary>
        private string QuartersRowLabel()
        {
            if (!m_hasRecord) return "unknown";
            if (!m_quarters.HasCot) return "no cot";
            if (!m_quarters.Sheltered) return "exposed";
            return $"comfort {m_quarters.Comfort}/{m_quarters.ComfortTarget}";
        }

        /// <summary>
        ///     Where this villager lives, measured at their cot. Held alongside
        ///     <see cref="m_level" /> rather than replacing it: the quarters ladder owns the
        ///     first two rungs (cot -> sheltered room -> furnished room) while the authored
        ///     tiers still describe what each level GIVES.
        /// </summary>
        private QuartersState m_quarters;

        private string LevelRowLabel()
        {
            if (!m_hasRecord) return "Level unknown";

            // The quarters ladder is what actually gates this villager, so it is what the row
            // reports. A villager with no habitable room is level 0 and does not work, however
            // many authored tiers their type has.
            return m_quarters.AtPotential
                ? $"Level {m_quarters.Level} (highest)"
                : $"Level {m_quarters.Level} / {m_quarters.Potential}";
        }

        public TabDetailDataUI GetDetail(int index, VillagerBehaviorBridge villager)
        {
            return index switch
            {
                QuartersRow => QuartersDetail(),
                MoodRow => MoodDetail(villager),
                _ => LevelDetail(),
            };
        }

        /// <summary>
        ///     The ladder this villager climbs, every rung shown whether or not it is reached.
        ///
        ///     <para>Locked rungs are greyed rather than hidden: an invisible rung is an upgrade
        ///     the player never learns exists, and the whole reason this tab exists is that a
        ///     villager's level comes from the room around them, so unlike a crafting station
        ///     there is nothing in the world to look at and read it off.</para>
        /// </summary>
        /// <summary>
        ///     The ladder as rows the renderer can put a badge on. Reached rungs wear the same
        ///     shield the villager wears on the map and in the panel header, so "what level am
        ///     I" has one visual language instead of a badge in one place and a green tick in
        ///     another.
        /// </summary>
        private List<LevelRung> LevelRungs()
        {
            var rungs = new List<LevelRung>();
            for (var rung = VillagerQuarters.LevelSheltered; rung <= m_quarters.Potential; rung++)
            {
                var reached = m_quarters.Level >= rung;
                var next = !reached && m_quarters.Level == rung - 1;
                rungs.Add(new LevelRung(rung, RungLabel(rung), reached, next));
            }

            return rungs;
        }

        /// <summary>What each rung of the quarters ladder asks for, in buildable terms.</summary>
        private string RungLabel(int rung)
        {
            if (rung == VillagerQuarters.LevelSheltered)
                return "a cot of their own, under a roof";
            if (rung == VillagerQuarters.LevelFurnished)
                return $"a furnished room (comfort {m_quarters.Comfort}/{m_quarters.ComfortTarget})";
            if (rung == VillagerQuarters.LevelPeered)
                return m_quarters.MissingPeers.Count > 0
                    ? "neighbours: " + string.Join(", ", m_quarters.MissingPeers)
                    : "neighbours in the village";
            return "";
        }

        /// <summary>Shelter in one line, in words the player can act on.</summary>
        private string QuartersLine()
        {
            if (!m_quarters.HasCot) return "<color=#FF8A6F>no cot</color>";

            return m_quarters.Sheltered
                ? $"<color=#6FCF6F>sheltered</color> <color=#9FB4C7>({m_quarters.Coverage:P0} covered)</color>"
                : $"<color=#FF8A6F>exposed</color> <color=#9FB4C7>({m_quarters.Coverage:P0} covered, " +
                  $"needs {VillagerQuarters.RequiredCoverage:P0})</color>";
        }

        #region Level

        private TabDetailDataUI LevelDetail()
        {
            if (!m_hasRecord)
                return new TabDetailDataUI
                {
                    Title = "Level unknown",
                    Description = "This villager has no record yet, so its level cannot be read.",
                };

            var body = new StringBuilder();

            body.Append(ScalingExplanation()).Append('\n');

            // One line per contributing area, each of which has its own row for the detail.
            body.Append("\nQuarters: ").Append(QuartersLine()).Append('\n');
            body.Append("Comfort:  ").Append(m_quarters.ComfortBar()).Append('\n');
            body.Append("Mood:     ").Append(MoodLabel()).Append('\n');

            if (!string.IsNullOrEmpty(m_quarters.Blocker))
                body.Append("\n<color=#FFE300>").Append(m_quarters.Blocker).Append("</color>\n");
            if (!m_quarters.CanWork)
                body.Append("<color=#FF8A6F>Only wanders until they have a room.</color>\n");

            // What the level is worth NOW. Null means the level is real but nothing is wired to
            // it yet, which is worth saying plainly rather than leaving a blank panel.
            //
            // The authored tier text still comes from VillagerLevel, but the LEVEL ITSELF never
            // does — the two ladders count differently (a Farmer reads 3 on quarters and 2 on
            // the authored tiers), and mixing them put one number on the row and another on the
            // panel header for the same villager.
            body.Append('\n');
            if (m_level.Benefit != null && !string.IsNullOrEmpty(m_level.Benefit.description))
                body.Append("Now: ").Append(m_level.Benefit.description).Append('\n');
            else if (!m_quarters.CanWork)
                body.Append("Now: nothing yet — this villager has nowhere to live.\n");
            else
                body.Append("Now: this role has no benefits tied to its level yet.\n");

            return new TabDetailDataUI
            {
                // Names the section rather than repeating the level: the native station
                // header already shows that number (VillagerStationLevelPatch), and two
                // readouts of one value — one of them wrong — is why that patch exists.
                // Using the row keeps the panel's layout intact; blanking it left a gap,
                // because the description is anchored below the title rather than flowing
                // after it.
                Title = "Villager Levels",
                Description = body.ToString().TrimEnd(),
                // The ladder is the summary: every rung, reached or not, so the player sees
                // both where this villager stands and what the next build would buy them.
                Rungs = LevelRungs(),
            };
        }

        /// <summary>
        ///     Where this villager lives. Its own row because a room is a thing the player
        ///     builds and can keep improving, so it deserves more than one line inside Level.
        /// </summary>
        private TabDetailDataUI QuartersDetail()
        {
            if (!m_hasRecord)
                return new TabDetailDataUI
                {
                    Title = "Quarters",
                    Description = "This villager has no record yet, so its quarters are unknown.",
                };

            var body = new StringBuilder();

            if (!m_quarters.HasCot)
            {
                body.Append("<color=#FF8A6F>No cot.</color> Place a Villager Cot and assign it to " +
                            "them from the cot's own menu.\n\nUntil then they only wander — a " +
                            "villager with nowhere to sleep does no work.");
                return new TabDetailDataUI { Title = "Quarters", Description = body.ToString() };
            }

            body.Append("Shelter: ").Append(QuartersLine()).Append('\n');
            body.Append("Comfort: ").Append(m_quarters.ComfortBar()).Append(
                $"  {m_quarters.Comfort}/{m_quarters.ComfortTarget}\n");

            var c = m_quarters.CotPosition;
            body.Append($"Where:   {c.x:F0}, {c.z:F0}\n");

            if (!string.IsNullOrEmpty(m_quarters.Blocker))
                body.Append("\n<color=#FFE300>").Append(m_quarters.Blocker).Append("</color>\n");

            body.Append('\n');
            if (!m_quarters.Sheltered)
                body.Append("A room counts as sheltered on the same test a workbench uses: a roof " +
                            "overhead and walls enough to keep the weather out.\n");
            else if (m_quarters.Comfort < m_quarters.ComfortTarget)
                body.Append("Comfort rises with furnishings near the bed — a fire, a table, a " +
                            "chair, a rug, a banner. The same pieces that rest YOU.\n");
            else
                body.Append("<color=" + MetColor + ">This room is as comfortable as it needs to be." +
                            "</color>\n");

            return new TabDetailDataUI { Title = "Quarters", Description = body.ToString().TrimEnd() };
        }

        /// <summary>One step, met or not, and what reaching it gives.</summary>
        private static string Step(LevelContributor c)
        {
            var grants = string.IsNullOrEmpty(c.Grants) ? "" : " — " + c.Grants;
            return c.Satisfied
                ? $"<color={MetColor}>✓ {c.Label}</color>{grants}"
                : $"· {c.Label}{grants}";
        }

        /// <summary>
        ///     Why this villager's level is what it is. Without this the steps read as arbitrary
        ///     — a Guard's list of comfort numbers and a Miner's list of colleagues come from
        ///     genuinely different rules.
        /// </summary>
        private string ScalingExplanation()
        {
            if (m_def == null) return "";

            return m_def.scalingType switch
            {
                VillagerLevel.ScalingVariety =>
                    "Levels up for each kind of villager it works alongside.",
                VillagerLevel.ScalingDependent =>
                    $"Levels up for each {m_def.dependentOnNpcType} living in the village.",
                _ => "Levels up with the comfort of its home — shelter and the furniture around it.",
            };
        }

        #endregion

        #region Mood

        private TabDetailDataUI MoodDetail(VillagerBehaviorBridge villager)
        {
            if (m_drives == null)
                return new TabDetailDataUI
                {
                    Title = "Mood",
                    Description = "Not known here yet — waiting on an update from the villager's host.",
                };

            var body = new StringBuilder();
            body.Append("What this villager wants right now.\n\n");

            foreach (Drive drive in System.Enum.GetValues(typeof(Drive)))
            {
                var value = m_drives.Get(drive);
                var elevated = value >= VillagerDrives.ElevatedThreshold;
                var row = $"{DriveName(drive),-10} {Bar(value)}";
                body.Append(elevated ? $"<color={ElevatedColor}>{row}</color>" : row).Append('\n');
            }

            body.Append('\n').Append(MoodExplanation());
            body.Append('\n').Append(VillageRecreation(villager));

            return new TabDetailDataUI { Title = "Mood — " + MoodLabel(), Description = body.ToString() };
        }

        /// <summary>
        ///     The mood in one phrase: the strongest drive once it is worth acting on, and
        ///     "content" before that. Matches what the villager will actually go and do, since
        ///     relax triggers on the same threshold.
        /// </summary>
        private string MoodLabel()
        {
            if (m_drives == null) return "unknown";

            var top = Drive.Warmth;
            var topValue = -1f;
            foreach (Drive drive in System.Enum.GetValues(typeof(Drive)))
            {
                var v = m_drives.Get(drive);
                if (v <= topValue) continue;
                topValue = v;
                top = drive;
            }

            return topValue >= VillagerDrives.ElevatedThreshold ? WantPhrase(top) : "content";
        }

        /// <summary>
        ///     What the village as a whole offers for recreation, counted by kind.
        ///
        ///     <para>A villager's mood is only half the picture: the drives say what they want,
        ///     this says whether the village can serve it. Shown on every villager's Mood panel
        ///     because these are village-wide amenities — building one hearth lifts everyone.</para>
        /// </summary>
        private static string VillageRecreation(VillagerBehaviorBridge villager)
        {
            var at = villager?.AI?.HomeAnchor;
            if (at == null) return "";

            var pois = Villages.VillagePoiRegistry.GetPois(at.Value);
            if (pois == null) return "";

            var counts = new Dictionary<LocationType, int>();
            foreach (var poi in pois)
            {
                if (!LeisureAppeal.IsLeisureType(poi.Type)) continue;
                counts.TryGetValue(poi.Type, out var n);
                counts[poi.Type] = n + 1;
            }

            var sb = new StringBuilder("\nIn the village: ");
            if (counts.Count == 0)
                return sb.Append("<color=" + LockedColor + ">nothing to do yet — a fire, a bench " +
                                 "or a table would give them somewhere to go.</color>").ToString();

            var first = true;
            foreach (var pair in counts)
            {
                if (!first) sb.Append(", ");
                sb.Append(pair.Value).Append(' ').Append(RecreationName(pair.Key, pair.Value));
                first = false;
            }

            return sb.Append('.').ToString();
        }

        private static string RecreationName(LocationType type, int count)
        {
            var one = type switch
            {
                LocationType.Fire => "fire",
                LocationType.HotTub => "hot tub",
                LocationType.Table => "table",
                LocationType.Chair => "seat",
                LocationType.Shelter => "shelter",
                LocationType.Farm => "garden",
                LocationType.Animals => "pen",
                _ => type.ToString().ToLowerInvariant(),
            };
            return count == 1 ? one : one + "s";
        }

        private string MoodExplanation()
        {
            if (m_drives == null) return "";

            foreach (Drive drive in System.Enum.GetValues(typeof(Drive)))
                if (m_drives.Get(drive) >= VillagerDrives.ElevatedThreshold)
                    return "A full bar is a want strong enough to act on — it will break off and " +
                           "head for somewhere that serves it when it has no work to do.";

            return "Nothing is pressing. It will wander while idle rather than seek anything out.";
        }

        private static string DriveName(Drive drive)
        {
            return drive switch
            {
                Drive.Warmth => "Warmth",
                Drive.Social => "Company",
                Drive.Rest => "Rest",
                Drive.Curiosity => "Novelty",
                _ => drive.ToString(),
            };
        }

        private static string WantPhrase(Drive drive)
        {
            return drive switch
            {
                Drive.Warmth => "wants warmth",
                Drive.Social => "wants company",
                Drive.Rest => "wants to rest",
                Drive.Curiosity => "restless",
                _ => "unsettled",
            };
        }

        private static string Bar(float value01)
        {
            var filled = UnityEngine.Mathf.Clamp(
                UnityEngine.Mathf.RoundToInt(UnityEngine.Mathf.Clamp01(value01) * BarCells), 0, BarCells);
            return new string('█', filled) + new string('░', BarCells - filled);
        }

        #endregion
    }
}
