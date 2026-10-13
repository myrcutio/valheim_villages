using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Behaviors.Work;
using ValheimVillages.Interfaces;
using ValheimVillages.Items.VirtualRecipes;
using ValheimVillages.Items.WorkOrders;
using ValheimVillages.TaskQueue.ActivityLog;
using ValheimVillages.UI.Alerts;
using ValheimVillages.UI.Core;
using ValheimVillages.UI.Interaction;
using ValheimVillages.UI.Panels;

namespace ValheimVillages.UI.Tabs
{
    /// <summary>
    ///     Tab listing tasks for the villager — currently blocked work orders that need
    ///     player attention. Each blocked entry shows the item being crafted and the
    ///     reason it can't be fulfilled.
    /// </summary>
    [RegisterTab("info", Order = 1)]
    public class InfoTab : IVillagerTabUI, IAlertFocusTab<VillagerBehaviorBridge>
    {
        private const string AttentionPrefix = "⚠ ";

        /// <summary>
        ///     Shown instead of the alert for work that is simply in progress. An alert means
        ///     "I need you"; this means "I need time". Mixing them is how a player learns to
        ///     ignore the badge.
        /// </summary>
        private const string WaitingPrefix = "⏳ ";

        /// <summary>An order running normally — present and accounted for, asking nothing.</summary>
        private const string RunningPrefix = "• ";

        /// <summary>An order stocked to its maximum. Done, not a problem.</summary>
        private const string CompletePrefix = "✓ ";

        /// <summary>Colour for the one row the villager's floating "!" is actually about.</summary>
        private const string AlertRowColor = "#FFC24A";

        private List<ActivityLogEntry> m_issues = new();
        private int m_issueCount;
        public string TabName => "Tasks";

        public void OnSelected(VillagerBehaviorBridge villager)
        {
            RefreshIssues(villager);
        }

        public void OnDeselected()
        {
            m_issues.Clear();
            m_issueCount = 0;
        }

        public void OnUpdate(VillagerBehaviorBridge villager)
        {
            RefreshIssues(villager);
        }

        #region IVillagerTab — List + Detail

        // The current-activity row is always the first list item.
        private const int CurrentTaskCount = 1;

        public List<TabListItemUI> GetListItems(VillagerBehaviorBridge villager)
        {
            var items = new List<TabListItemUI>
            {
                new() { TabName = "▶ " + CurrentActivityLabel(villager), Icon = null },
            };

            m_issueCount = m_issues.Count;

            // Every blocked order gets a "⚠" row; the one the villager is actually complaining
            // about is tinted, so it stays identifiable after the player clicks around and loses
            // the initial selection.
            var alerted = MatchingIssueIndex(FindMarker(villager));
            for (var i = 0; i < m_issues.Count; i++)
            {
                var issue = m_issues[i];
                var mark = issue.Action == SatisfiedAction ? CompletePrefix
                    : issue.Action == RunningAction || issue.Action == ShortAction ? RunningPrefix
                    : issue.WaitingOnTime ? WaitingPrefix
                    : AttentionPrefix;
                var label = mark + (issue.ItemPrefab ?? issue.TaskName ?? "(blocked)");
                items.Add(new TabListItemUI
                {
                    TabName = i == alerted ? $"<color={AlertRowColor}>{label}</color>" : label,
                    Icon = ResolveItemIcon(issue.ItemPrefab),
                });
            }

            AddAbilityItems(items, villager);
            return items;
        }

        /// <summary>
        ///     Short label for the current-activity row. Prefers the active
        ///     behavior's own status text (e.g. "Farming: Planting",
        ///     "Patrolling (8 waypoints)") and falls back to the raw behavior
        ///     state when no behavior is active or it reports nothing.
        /// </summary>
        private string CurrentActivityLabel(VillagerBehaviorBridge villager)
        {
            if (villager == null) return "Idle";

            var status = villager.AI?.ActiveBehavior?.GetStatusText();
            if (!string.IsNullOrEmpty(status)) return status;

            if (villager.CurrentState != Enums.BehaviorState.Idle)
                return villager.CurrentState.ToString();

            // "Idle" alone is the same dead end as "Missing ingredients": it says the villager
            // is doing nothing and leaves the player to work out whether that is fine. A
            // villager standing about because every order is stocked and one standing about
            // because it cannot reach its chest look identical, and only one of them wants
            // doing something about.
            // Count only rows that actually want something: the list now also carries orders
            // that are simply running, and calling those "need attention" would undo the whole
            // point of separating an alert from a state.
            // Three different situations, and only one of them wants the player to act.
            var needing = m_issues.Count(
                e => e.Action != RunningAction && e.Action != ShortAction &&
                     e.Action != SatisfiedAction);
            if (needing > 0) return $"Nothing to do — {needing} order(s) need attention";

            var outstanding = m_issues.Count(e => e.Action == ShortAction);
            if (outstanding > 0) return $"Idle — {outstanding} order(s) still to fill";

            return "Nothing to do — every order is stocked";
        }

        public TabDetailDataUI GetDetail(
            int index, VillagerBehaviorBridge villager)
        {
            if (index < CurrentTaskCount)
                return CurrentTaskDetail(villager);

            var issueIdx = index - CurrentTaskCount;
            if (issueIdx >= 0 && issueIdx < m_issueCount)
            {
                var issue = m_issues[issueIdx];

                var pins = new List<Panels.TaskPin>();

                // Same resolver the overview uses: an order's chest first, the recorded
                // blocker position second. Reading only the recorded position — as this used
                // to — left every RUNNING order's row with no pin at all, because a synthetic
                // row has no position of its own. Two code paths answering "where is this
                // row?" differently is what put an order on the overview map and nowhere else.
                var focus = ResolveRowPosition(
                    issue, VillageContainersFor(villager));

                // Work-order blockers pin a chest; behavior blockers (e.g. patrol)
                // pin the spot that's the problem — label/colour it accordingly.
                var isStationBlocker = !string.IsNullOrEmpty(issue.StationName);
                var focusLabel = isStationBlocker ? "Order" : "Problem";

                // Same state colours as the overview, so a row's own pin does not change
                // colour just because you selected it.
                var focusColor = issue.Action == SatisfiedAction ? CompletePinColor
                    : issue.Action == RunningAction ? WorkingPinColor
                    : issue.WaitingOnTime ? WaitingPinColor
                    : isStationBlocker ? ChestPinColor
                    : ProblemPinColor;
                BuildPins(pins, focus, focusLabel, focusColor,
                    WorkOrderIcon(issue.StationName, issue.ItemPrefab, villager)
                    ?? ResolveItemIcon(issue.ItemPrefab),
                    // A station blocker IS an order, so this map's own pin opens it too.
                    isStationBlocker ? issue.ItemPrefab : null,
                    issue.StationName);
                // A cultivator order spends almost all of its life as crops in the ground,
                // so "the villager will work it when there is something to do" reads as if
                // nothing is happening. Say what is actually happening instead. The
                // discriminator is the output having a registered plant piece — the same
                // registry farming uses to plant it, so the two can't disagree.
                var reason = issue.Action == RunningAction
                    ? PlantPieceRegistry.IsFarmingOutput(issue.ItemPrefab)
                        ? "They are growing. The villager will harvest them when they are ready."
                        : "This order is running. The villager will work it when there is something to do."
                    : issue.Action == SatisfiedAction
                        ? issue.Reason ?? "This order is stocked to its maximum."
                        : issue.Reason ?? issue.Description ?? "";
                // Name the station only when it is NOT this villager's own. The panel header
                // already says which villager's station you are standing at, so a row reading
                // "Station: Farmer" on the Farmer's own panel spends a line saying nothing —
                // and these rows run long enough to push text under the map.
                //
                // A PHYSICAL station is worth naming: an order worked at a workbench or a farm
                // happens somewhere other than the villager, which the header cannot tell you.
                var ownStation = villager != null
                    ? Villager.Station.VillagerStation.GetStationName(villager.VillagerType)
                    : null;
                var showStation = !string.IsNullOrEmpty(issue.StationName) &&
                                  issue.StationName != ownStation;

                var description = showStation
                    ? $"Station: {StationDisplay.Pretty(issue.StationName)}\n{reason}"
                    : reason;

                // Lead with the villager's own words when this is the row their badge is about.
                // The floating "!" and the line over their head are the player's entry point;
                // repeating it here is what connects the thing they saw in the world to the
                // blocked entry that explains it.
                var marker = FindMarker(villager);
                if (MatchingIssueIndex(marker) == issueIdx)
                    description = Spoken(marker) + description;


                // Context the row cannot say in words: where the ingredients are, and which
                // station the order needs. Non-interactive — they explain the task, they are
                // not things to click through to.
                BuildContextPins(pins, issue, villager);

                var blockedMap = VillageMapPanel.RenderForTask(villager, pins, index);
                return new TabDetailDataUI
                {
                    Title = issue.ItemPrefab ?? issue.TaskName ?? "Blocked",
                    Icon = ResolveItemIcon(issue.ItemPrefab),
                    Description = description,
                    MapTexture = blockedMap?.Texture,
                    MapPins = blockedMap?.Pins,
                };
            }

            // Ability panels stored absolute list indices when added.
            return GetAbilityDetail(index, villager);
        }

        /// <summary>Detail for the "current activity" row (index 0).</summary>
        private TabDetailDataUI CurrentTaskDetail(VillagerBehaviorBridge villager)
        {
            var status = CurrentActivityLabel(villager);

            var pins = new List<Panels.TaskPin>();
            var waypoint = villager?.CurrentWaypoint;
            var target = waypoint != null ? waypoint.Position : (Vector3?)null;

            // A patroller's "target" is just the next waypoint on a route the map already draws
            // as a dashed line, so pinning it as a task was pointing at the route and calling it
            // work. It becomes a landmark on the route instead — same red, and not clickable,
            // because there is no task row behind it to open.
            var patrolling = villager != null &&
                             villager.CurrentState == Enums.BehaviorState.Patrolling;
            BuildPins(pins, target,
                patrolling ? VillageMapPanel.PatrolLabel : "Target",
                patrolling ? PatrolPinColor : TargetPinColor);

            // The default row is the overview: every order needing attention, each clickable
            // through to its own row.
            BuildAllOrderPins(pins, villager);

            // The default row is the MAP. It carries every task pin, so the panel's job here is
            // to show where things are, not to restate them as prose — one line of context,
            // then the map. The raw behaviour state is debug detail and lives on the Status tab
            // and vv_records; it was only ever here because this row had nothing else to show.
            var description = status;

            // A village-wide alert (storage almost full) belongs to no order, so no "⚠" row owns
            // it — and neither does an alert naming an order the scan hasn't logged a blocker for
            // yet. Either way the badge sent the player here, so the line has to appear somewhere
            // rather than leaving them on a row that says nothing about it.
            var marker = FindMarker(villager);
            if (marker != null && MatchingIssueIndex(marker) < 0)
                description = Spoken(marker) + description;


            // The native description layout collapses (and the map covers the
            // text) when the recipe icon is disabled, so always give it an icon:
            // the item being crafted if any, else a generic tool icon.
            var crafting = (villager?.AI?.ActiveBehavior as CraftingBehaviorAdapter)?.Crafting;
            var icon = ResolveItemIcon(crafting?.CurrentItemPrefab)
                       ?? ResolveItemIcon("Hammer");

            var activityMap = VillageMapPanel.RenderForTask(villager, pins, 0);
            return new TabDetailDataUI
            {
                Title = "Current Activity",
                Icon = icon,
                Description = description,
                MapTexture = activityMap?.Texture,
                MapPins = activityMap?.Pins,
            };
        }

        #endregion

        #region Panel-Driven Ability Data

        private static readonly List<IListPanel> s_panels = new();

        /// <summary>Register an IListPanel for this tab. Called from Plugin startup.</summary>
        public static void RegisterPanel(IListPanel panel)
        {
            if (panel.ParentTab == "info" && !s_panels.Contains(panel))
                s_panels.Add(panel);
        }

        private readonly List<(IListPanel panel, int startIdx, int count)> m_panelRanges = new();

        private void AddAbilityItems(
            List<TabListItemUI> items, VillagerBehaviorBridge villager)
        {
            m_panelRanges.Clear();
            foreach (var panel in s_panels)
                if (panel is IListPanelUI panelUI)
                {
                    var panelItems = panelUI.GetListItems(villager);
                    if (panelItems.Count > 0)
                    {
                        m_panelRanges.Add((panel, items.Count, panelItems.Count));
                        foreach (var p in panelItems) items.Add(p);
                    }
                }
        }

        private TabDetailDataUI GetAbilityDetail(
            int absoluteIndex, VillagerBehaviorBridge villager)
        {
            foreach (var (panel, startIdx, count) in m_panelRanges)
                if (absoluteIndex >= startIdx && absoluteIndex < startIdx + count)
                    return panel is IListPanelUI panelUI
                        ? panelUI.GetDetail(absoluteIndex - startIdx, villager)
                        : null;
            return null;
        }

        #endregion

        #region Alert Focus

        /// <summary>
        ///     Which row explains this villager's floating "!". The alert names the order it is
        ///     about, so it maps onto the matching blocked entry; a village-wide alert (or one
        ///     whose order has no logged blocker) has no row of its own and falls back to the
        ///     current-activity row, whose detail carries the spoken line instead.
        /// </summary>
        public int FindAlertRow(VillagerBehaviorBridge villager)
        {
            var marker = FindMarker(villager);
            if (marker == null) return -1;

            // FindAlertRow runs before the tab is selected, so nothing has refreshed the issue
            // list for this villager yet — and the returned index must address the list the
            // upcoming GetListItems will build.
            RefreshIssues(villager);

            var issueIdx = MatchingIssueIndex(marker);
            return issueIdx >= 0 ? CurrentTaskCount + issueIdx : 0;
        }

        /// <summary>
        ///     Index into <see cref="m_issues" /> of the blocked entry this alert is about, or -1.
        ///     Matched on the item alone: the alert reads the order's own station name
        ///     ("$vv_farmer") while a blocker logs the PHYSICAL station it resolved to ("farm"),
        ///     so comparing stations would reject pairs that are in fact the same order.
        /// </summary>
        private int MatchingIssueIndex(VillagerAlertMarker marker)
        {
            if (marker == null || string.IsNullOrEmpty(marker.ItemPrefab)) return -1;

            for (var i = 0; i < m_issues.Count; i++)
                if (m_issues[i]?.ItemPrefab == marker.ItemPrefab)
                    return i;

            return -1;
        }

        /// <summary>
        ///     The bridge, the AI and the alert marker all live on the one NPC GameObject
        ///     (SpawnPatch adds them together), so the bridge's own object is the marker's — no
        ///     need to walk through <c>villagerInstance</c> and risk a destroyed component.
        /// </summary>
        private static VillagerAlertMarker FindMarker(VillagerBehaviorBridge villager)
        {
            return VillagerAlertMarker.Find(villager != null ? villager.gameObject : null);
        }

        /// <summary>The villager's own line, styled as speech, ready to prefix a description.</summary>
        private static string Spoken(VillagerAlertMarker marker)
        {
            return $"<color={AlertRowColor}><i>\u201c{marker.Message}\u201d</i></color>\n\n";
        }

        #endregion

        #region Helpers

        /// <summary>Action stamped on a synthetic row for an order that is running normally.</summary>
        private const string RunningAction = "order";

        /// <summary>
        ///     A work order that is below its minimum but has no recorded blocker — the villager
        ///     simply has not filled it yet.
        ///
        ///     <para>Distinct from <see cref="RunningAction" /> because the summary used to infer
        ///     "every order is stocked" from the ABSENCE of blocked entries, never once comparing
        ///     stock against the quota. An order sitting at 2 of 10 with nothing logged against it
        ///     was reported as fully stocked. It is not an alert either: the villager may be about
        ///     to do it, and warning about work that is merely outstanding is what the hourglass
        ///     split was meant to stop.</para>
        /// </summary>
        private const string ShortAction = "short";

        /// <summary>The scan's own word for an order that is stocked to its maximum.</summary>
        private const string SatisfiedAction = "satisfied";

        /// <summary>
        ///     Build the task list: every work order this villager is responsible for, plus any
        ///     blocked entry that is not one of them.
        ///
        ///     <para>ONE list drives the rows, the details AND the map pins. They used to come
        ///     from different places — rows from blocked entries, pins from the village's
        ///     orders — so an order that was running fine appeared on the map with no row to
        ///     open, and the pin's row index pointed into a list that did not contain it.</para>
        ///
        ///     <para>An order with no blocker gets a synthetic entry marked
        ///     <see cref="RunningAction" />: it is a real thing in the village with a real
        ///     state, just not a problem.</para>
        /// </summary>
        private void RefreshIssues(VillagerBehaviorBridge villager)
        {
            m_issues.Clear();
            if (villager == null || string.IsNullOrEmpty(villager.UniqueId)) return;

            var entries = VillagerActivityLog.Instance.GetEntries(villager.UniqueId).ToList();
            var blocked = entries.Where(e => e.Action == "blocked").ToList();

            // "satisfied" is the scan's word for an order that is DONE — stocked to its
            // maximum. Those belong in the list too: an order that finished should not drop
            // out of the UI as though it had never existed, and the map already pins it.
            var satisfied = entries.Where(e => e.Action == SatisfiedAction).ToList();

            var rows = new List<ActivityLogEntry>();
            var claimed = new HashSet<string>();

            var anchor = villager.AI != null ? villager.AI.HomeAnchor : Vector3.zero;
            var village = Villages.Entity.VillageRegistry.GetVillageAt(anchor);
            if (village != null)
                foreach (var match in Villager.AI.Work.ContainerScanner
                             .FindAllWorkOrders(village, villager.VillagerType))
                {
                    // A blocker outranks "done": if the scan says both, the thing the player
                    // can act on is the one worth showing.
                    var existing = blocked.FirstOrDefault(b => b.ItemPrefab == match.ItemPrefabName)
                                   ?? satisfied.FirstOrDefault(b => b.ItemPrefab == match.ItemPrefabName);
                    if (existing != null)
                    {
                        rows.Add(existing);
                        claimed.Add(match.ItemPrefabName);
                        continue;
                    }

                    // Measure before claiming anything about stock. The same count the scan
                    // uses, so the panel and the villager cannot disagree about whether an
                    // order is filled.
                    var have = Villager.AI.Work.ContainerScanner.CountAcrossContainers(
                        VillageContainersFor(villager), match.ItemPrefabName);
                    var shortfall = have < match.MinQuantity;

                    rows.Add(new ActivityLogEntry
                    {
                        VillagerId = villager.UniqueId,
                        TaskName = "work_order",
                        Action = shortfall ? ShortAction : RunningAction,
                        Reason = shortfall
                            ? $"Stocked {have} of {match.MinQuantity}. " +
                              "The villager has not filled this order yet."
                            : null,
                        ItemPrefab = match.ItemPrefabName,
                        StationName = match.StationName,
                    });
                    claimed.Add(match.ItemPrefabName);
                }

            foreach (var b in blocked)
                if (b.ItemPrefab == null || !claimed.Contains(b.ItemPrefab))
                    rows.Add(b);

            m_issues = rows;
        }

        private static readonly Color ChestPinColor = new(1f, 0.55f, 0.15f);
        private static readonly Color TargetPinColor = new(0.3f, 0.9f, 0.3f);

        /// <summary>Work-order item icons, by station. Hits only — a miss is usually a cold ObjectDB.</summary>
        private static readonly Dictionary<string, Sprite> s_workOrderIcons = new();

        /// <summary>
        ///     The icon of the work-order token a station uses, so an order pin is
        ///     unmistakably an ORDER rather than a loose item lying somewhere.
        /// </summary>
        /// <param name="stationName">
        ///     The blocked entry's station, which is <c>PhysicalStation ?? Station</c> — so it
        ///     may be a prefab name ("farm", "smelter") rather than the raw
        ///     <c>CraftingStation.m_name</c> the icon map is keyed by ("$piece_workbench",
        ///     "$vv_farmer"). Resolved via <paramref name="itemPrefab" /> when it is.
        /// </param>
        private static Sprite WorkOrderIcon(string stationName, string itemPrefab,
            VillagerBehaviorBridge villager)
        {
            var raw = RawStationFor(stationName, itemPrefab, villager);
            if (string.IsNullOrEmpty(raw)) return null;
            if (s_workOrderIcons.TryGetValue(raw, out var cached)) return cached;

            if (!Items.ItemFactory.BuildStationWorkOrderMap()
                    .TryGetValue(raw, out var workOrderName))
                return null;

            var icons = ObjectDB.instance?.GetItemPrefab(workOrderName)
                ?.GetComponent<ItemDrop>()?.m_itemData?.m_shared?.m_icons;
            var icon = icons != null && icons.Length > 0 ? icons[0] : null;

            if (icon != null) s_workOrderIcons[raw] = icon;
            return icon;
        }

        /// <summary>
        ///     The raw <c>CraftingStation.m_name</c> for an order, which is what the work-order
        ///     map is keyed by.
        ///
        ///     <para>The blocked entry records <c>PhysicalStation ?? Station</c>, so its station
        ///     is often a prefab name that no lookup keyed on "$piece_*"/"$vv_*" will ever
        ///     match — the same display-vs-raw mismatch that broke opening an order from its
        ///     pin. The village record is the authority: its WorkOrderEntry.Station IS the raw
        ///     name, so the order is found by ITEM and its station read off the record.</para>
        /// </summary>
        private static string RawStationFor(
            string stationName, string itemPrefab, VillagerBehaviorBridge villager)
        {
            // Already raw (the entry had no physical station) — nothing to resolve.
            if (!string.IsNullOrEmpty(stationName) && stationName.StartsWith("$"))
                return stationName;

            if (string.IsNullOrEmpty(itemPrefab)) return stationName;

            var anchor = villager?.AI != null ? villager.AI.HomeAnchor : Vector3.zero;
            var village = Villages.Entity.VillageRegistry.GetVillageAt(anchor);
            var orders = village?.WorkOrders;
            if (orders == null) return stationName;

            foreach (var entry in orders)
                if (entry.Item == itemPrefab)
                    return entry.Station;

            return stationName;
        }

        /// <summary>Matches the dashed route the waypoint belongs to, not the task-pin green.</summary>
        private static readonly Color PatrolPinColor = new(0.70f, 0.16f, 0.13f);
        private static readonly Color PlayerPinColor = new(0.35f, 0.8f, 1f);
        private static readonly Color ProblemPinColor = new(0.95f, 0.25f, 0.2f);

        /// <summary>
        ///     Add the focus pin (chest/target) and the player's pin.
        ///
        ///     <para>This used to also return a colour-coded legend line ("● Chest  ● You") for
        ///     the description. The pins name themselves on hover now, so the legend was saying
        ///     the same thing twice while taking room from the map it was explaining.</para>
        /// </summary>
        /// <param name="orderItem">
        ///     The work order this focus pin stands for, so clicking it opens that order's
        ///     editor. Without it the pin falls back to row navigation — and on a task's OWN
        ///     map that means re-selecting the row already open, i.e. the click appears dead.
        ///     The overview row sets this when it builds its own pins; this is the same identity for
        ///     the single-task map, so both behave alike.
        /// </param>
        private static void BuildPins(
            List<Panels.TaskPin> pins,
            Vector3? focus, string focusLabel, Color focusColor, Sprite focusIcon = null,
            string orderItem = null, string orderStation = null)
        {
            // The focus pin borrows the work order's own icon where there is one, so the thing
            // on the map is the thing in the row.
            if (focus.HasValue)
                pins.Add(new Panels.TaskPin(focus.Value, focusColor, focusLabel, focusIcon,
                    orderItem: orderItem, orderStation: orderStation));

            var player = Player.m_localPlayer;
            if (player != null)
                pins.Add(new Panels.TaskPin(player.transform.position, PlayerPinColor, "You"));
        }

        /// <summary>
        ///     Pin every blocked order the villager has, each pointing at its OWN row.
        ///
        ///     <para>Only the default row calls this. A specific task's row pins just that task,
        ///     so opening one filters the map down to it — which is the point: the overview
        ///     shows everything needing attention, and picking one narrows to it.</para>
        /// </summary>
        /// <summary>How long a village's container list is reused before rescanning.</summary>
        private const float ContainerCacheSeconds = 5f;

        private static string s_cachedContainerVillage;
        private static float s_cachedContainerAt;
        private static List<Container> s_cachedContainers;

        /// <summary>
        ///     The village's containers, cached briefly.
        ///
        ///     <para>Pinning orders needs to know which chest holds each one, and the map is
        ///     rebuilt on the description refresh timer — scanning the village per refresh is
        ///     exactly the cost the surface-map work was moved out of the UI to avoid.</para>
        /// </summary>
        private static List<Container> VillageContainers(string villageId, Vector3 anchor)
        {
            if (s_cachedContainers != null && s_cachedContainerVillage == villageId &&
                Time.time - s_cachedContainerAt < ContainerCacheSeconds)
                return s_cachedContainers;

            s_cachedContainers = Villager.AI.Work.ContainerScanner
                .FindVillageContainers(anchor, Settings.WorkSettings.ChestScanRadius);
            s_cachedContainerVillage = villageId;
            s_cachedContainerAt = Time.time;
            return s_cachedContainers;
        }

        /// <summary>
        ///     Pin every work order this villager is responsible for — not only the blocked
        ///     ones.
        ///
        ///     <para>Blocked-only was the original behaviour and it reads as a bug: an order
        ///     being worked normally simply vanished from the map, so the only orders you could
        ///     see were the ones going wrong. An order is a standing thing in the village; its
        ///     state is a property of it, not a reason to hide it.</para>
        ///
        ///     <para>Pinned at the chest holding the order's token — both where the work is
        ///     filed and what the editor binds to, so the same pin can open it.</para>
        /// </summary>
        private void BuildAllOrderPins(List<Panels.TaskPin> pins, VillagerBehaviorBridge villager)
        {
            var anchor = villager?.AI != null ? villager.AI.HomeAnchor : Vector3.zero;
            var village = Villages.Entity.VillageRegistry.GetVillageAt(anchor);
            if (village == null) return;

            var containers = VillageContainers(village.VillageId, anchor);
            if (containers == null) return;

            // Walk the SAME list the rows come from, so a pin's target row always exists and
            // always refers to the entry the pin is about. Building pins from a separate query
            // is what let the map show an order the list had no row for.
            for (var i = 0; i < m_issues.Count; i++)
            {
                var issue = m_issues[i];
                var row = i + CurrentTaskCount;

                var at = ResolveRowPosition(issue, containers);
                if (!at.HasValue) continue;

                var running = issue.Action == RunningAction;
                var isOrder = !string.IsNullOrEmpty(issue.ItemPrefab) &&
                              !string.IsNullOrEmpty(issue.StationName);

                pins.Add(new Panels.TaskPin(
                    at.Value,
                    issue.Action == SatisfiedAction ? CompletePinColor
                    : running ? WorkingPinColor
                    : issue.WaitingOnTime ? WaitingPinColor
                    : ProblemPinColor,
                    issue.ItemPrefab ?? issue.TaskName ?? "Blocked",
                    WorkOrderIcon(issue.StationName, issue.ItemPrefab, villager)
                    ?? ResolveItemIcon(issue.ItemPrefab),
                    row,
                    isOrder ? issue.ItemPrefab : null,
                    issue.StationName));
            }
        }

        /// <summary>Ingredient sources — where the stuff for this order is sitting.</summary>
        private static readonly Color IngredientPinColor = new(0.72f, 0.82f, 0.60f);

        /// <summary>The station the order is made at.</summary>
        private static readonly Color StationPinColor = new(0.78f, 0.70f, 0.88f);

        /// <summary>
        ///     Add the supporting cast for ONE order: every chest holding one of its
        ///     ingredients, and the station it is worked at.
        ///
        ///     <para>All marked as landmarks. They answer "why is this order stuck / what feeds
        ///     it", which is a question about the task, not a thing to open — and making an
        ///     ingredient chest clickable would promise a row that does not exist.</para>
        /// </summary>
        private void BuildContextPins(
            List<Panels.TaskPin> pins, ActivityLogEntry issue, VillagerBehaviorBridge villager)
        {
            if (villager == null || string.IsNullOrEmpty(issue.ItemPrefab)) return;

            // The station is known from the ORDER, not from the recipe, so pin it first and
            // unconditionally. Gating it behind a recipe lookup meant an order whose recipe
            // could not be resolved lost its station pin too — the one piece of context most
            // worth having when something is wrong.
            AddStationPin(pins, issue, villager);

            var recipe = Villager.AI.Work.StationMatcher.FindRecipeForNpc(
                issue.ItemPrefab, villager.VillagerType);
            if (recipe == null) return;

            var containers = VillageContainersFor(villager);

            var sources = Villager.AI.Work.ContainerScanner.FindIngredients(containers, recipe);
            if (sources != null)
                foreach (var src in sources)
                {
                    if (src?.Container == null) continue;
                    pins.Add(new Panels.TaskPin(
                        src.Container.transform.position,
                        IngredientPinColor,
                        $"{ItemDisplay.Name(src.PrefabName)} x{src.Amount}",
                        ResolveItemIcon(src.PrefabName),
                        landmark: true));
                }
        }

        /// <summary>
        ///     Pin the station this order is worked at.
        ///
        ///     <para>Chosen by the station's recorded name, because the thing to look for
        ///     differs by kind: a hive and a kiln are not CraftingStations, so a single lookup
        ///     cannot find them all. Unknown names fall through to a CraftingStation by name,
        ///     which is what a villager's virtual station is.</para>
        /// </summary>
        private void AddStationPin(
            List<Panels.TaskPin> pins, ActivityLogEntry issue, VillagerBehaviorBridge villager)
        {
            var station = issue.StationName;
            if (string.IsNullOrEmpty(station)) return;

            var at = villager.AI != null ? villager.AI.HomeAnchor : Vector3.zero;
            var lower = station.ToLowerInvariant();
            var pos = Vector3.zero;
            var found = false;

            // A farm is cultivated GROUND, not a component — no station lookup can find it,
            // which is why a farm order had no station pin at all. It is a village PoI.
            if (lower.Contains("farm"))
            {
                var best = float.MaxValue;
                var foundFarm = false;
                foreach (var poi in Villages.VillagePoiRegistry.GetPois(at, Enums.LocationType.Farm))
                {
                    var d = (poi.Position - at).sqrMagnitude;
                    if (d >= best) continue;
                    best = d;
                    pos = poi.Position;
                    foundFarm = true;
                }

                if (!foundFarm) return;

                pins.Add(new Panels.TaskPin(
                    pos, StationPinColor, "Farm", null, landmark: true));
                return;
            }

            if (lower.Contains("beehive"))
                found = Villages.VillageStationRegistry.TryFindStation<Beehive>(
                    at, _ => true, out pos, out _);
            else if (lower.Contains("smelter") || lower.Contains("kiln") ||
                     lower.Contains("blastfurnace") || lower.Contains("windmill"))
                found = Villages.VillageStationRegistry.TryFindStation<Smelter>(
                    at, _ => true, out pos, out _);
            else if (lower.Contains("cooking") || lower.Contains("oven"))
                found = Villages.VillageStationRegistry.TryFindStation<CookingStation>(
                    at, _ => true, out pos, out _);
            else
                found = Villages.VillageStationRegistry.TryFindStation<CraftingStation>(
                    at, cs => cs.m_name == station, out pos, out _);

            if (!found) return;

            pins.Add(new Panels.TaskPin(
                pos, StationPinColor, StationDisplay.Pretty(station), null, landmark: true));
        }

        /// <summary>The cached container list for this villager's village, or an empty list.</summary>
        private static List<Container> VillageContainersFor(VillagerBehaviorBridge villager)
        {
            var anchor = villager?.AI != null ? villager.AI.HomeAnchor : Vector3.zero;
            var village = Villages.Entity.VillageRegistry.GetVillageAt(anchor);
            if (village == null) return new List<Container>();

            return VillageContainers(village.VillageId, anchor) ?? new List<Container>();
        }

        /// <summary>
        ///     Where a task row sits in the world: the chest holding its order token, or the
        ///     position a blocker recorded. Null when neither is known, which is the only
        ///     honest reason to leave a row off the map.
        /// </summary>
        private static Vector3? ResolveRowPosition(ActivityLogEntry issue, List<Container> containers)
        {
            if (!string.IsNullOrEmpty(issue.ItemPrefab) && !string.IsNullOrEmpty(issue.StationName))
                foreach (var c in containers)
                    if (Villager.AI.Work.WorkOrderChestPolicy.HoldsOrder(
                            c, issue.ItemPrefab, issue.StationName))
                        return c.transform.position;

            if (issue.WorkOrderPosX.HasValue && issue.WorkOrderPosY.HasValue &&
                issue.WorkOrderPosZ.HasValue)
                return new Vector3(issue.WorkOrderPosX.Value, issue.WorkOrderPosY.Value,
                    issue.WorkOrderPosZ.Value);

            return null;
        }
        /// <summary>An order running normally — on the map, but not calling for help.</summary>
        private static readonly Color WorkingPinColor = new(0.55f, 0.78f, 0.95f);

        /// <summary>An order waiting on time rather than on the player.</summary>
        private static readonly Color WaitingPinColor = new(0.85f, 0.78f, 0.45f);

        /// <summary>An order stocked to its maximum — finished.</summary>
        private static readonly Color CompletePinColor = new(0.50f, 0.80f, 0.52f);
        private static Sprite ResolveItemIcon(string itemPrefab)
        {
            if (string.IsNullOrEmpty(itemPrefab) || ObjectDB.instance == null) return null;
            var prefab = ObjectDB.instance.GetItemPrefab(itemPrefab);
            if (prefab == null) return null;
            var drop = prefab.GetComponent<ItemDrop>();
            var icons = drop?.m_itemData?.m_shared?.m_icons;
            return icons != null && icons.Length > 0 ? icons[0] : null;
        }

        #endregion
    }
}
