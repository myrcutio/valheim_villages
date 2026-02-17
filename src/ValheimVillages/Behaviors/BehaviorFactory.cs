using System;
using System.Collections.Generic;
using ValheimVillages.Behaviors.Patrol;
using ValheimVillages.Behaviors.Alarm;
using ValheimVillages.Behaviors.Work;
using ValheimVillages.Behaviors.Explore;
using ValheimVillages.NPCs.AI;

namespace ValheimVillages.Behaviors
{
    /// <summary>
    /// Maps behavior tags from NPC JSON definitions to concrete IBehavior types.
    /// Phase 4 adds [RegisterBehavior] attribute scanning; in this phase, the map is manual.
    /// </summary>
    public static class BehaviorFactory
    {
        private static readonly Dictionary<string, Func<VillagerAI, IBehavior>> s_creators = new()
        {
            { "patrol", ai => new PerimeterPatrolBehavior(ai) },
            { "alarm", ai => new BreachAlarmBehavior(ai) },
            { "craft", ai => new CraftingBehaviorAdapter(ai) },
            { "farm", ai => new FarmBehaviorAdapter(ai) },
            { "explore", ai => new ExploreBehaviorAdapter(ai) },
        };

        /// <summary>
        /// Create all behaviors for an NPC based on its definition's behavior tags.
        /// Returns a list sorted by descending priority.
        /// </summary>
        public static List<IBehavior> CreateBehaviors(VillagerAI ai, IReadOnlyList<string> behaviorTags)
        {
            var behaviors = new List<IBehavior>();
            if (behaviorTags == null) return behaviors;

            foreach (var tag in behaviorTags)
            {
                if (s_creators.TryGetValue(tag, out var creator))
                {
                    behaviors.Add(creator(ai));
                }
                else
                {
                    Plugin.Log?.LogWarning($"[BehaviorFactory] Unknown behavior tag: '{tag}'");
                }
            }

            behaviors.Sort((a, b) => b.Priority.CompareTo(a.Priority));
            return behaviors;
        }
    }
}
