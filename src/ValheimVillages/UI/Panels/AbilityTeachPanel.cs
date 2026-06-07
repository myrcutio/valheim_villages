using System.Collections.Generic;
using ValheimVillages.Abilities;
using ValheimVillages.Attributes;
using ValheimVillages.Tags;
using ValheimVillages.UI.Core;
using ValheimVillages.UI.Interaction;
using ValheimVillages.Villager.Registry;

namespace ValheimVillages.UI.Panels
{
    /// <summary>
    ///     Surfaces the abilities a villager can teach the player in the Tasks (info)
    ///     tab. Driven by the "ability:&lt;id&gt;" tags in the villager's JSON definition
    ///     (e.g. the Mountaineer's "ability:mountainstride"). Each taught ability gets a
    ///     list row; its detail offers a "Learn" action when the player hasn't learned it
    ///     yet, and shows the learned state otherwise. Replaces the hardcoded Mountaineer
    ///     branch removed when the tab UI moved to the panel registry.
    /// </summary>
    [RegisterListPanel("ability", "info")]
    public class AbilityTeachPanel : IListPanelUI
    {
        public string Tag => "ability";
        public string ParentTab => "info";

        public List<TabListItemUI> GetListItems(VillagerBehaviorBridge villager)
        {
            var items = new List<TabListItemUI>();
            var player = Player.m_localPlayer;
            foreach (var ability in TaughtAbilities(villager))
            {
                var learned = player != null && ability.HasLearned(player);
                items.Add(new TabListItemUI
                {
                    TabName = learned
                        ? $"✦ {ability.DisplayName}"
                        : $"✦ Learn {ability.DisplayName}",
                });
            }

            return items;
        }

        public TabDetailDataUI GetDetail(int index, VillagerBehaviorBridge villager)
        {
            var abilities = TaughtAbilities(villager);
            if (index < 0 || index >= abilities.Count) return null;

            var ability = abilities[index];
            var player = Player.m_localPlayer;
            var learned = player != null && ability.HasLearned(player);

            if (learned)
                return new TabDetailDataUI
                {
                    Title = ability.DisplayName,
                    Description = $"{ability.Description}\n\nYou have learned this technique.",
                };

            return new TabDetailDataUI
            {
                Title = ability.DisplayName,
                Description = $"{ability.Description}\n\nThe {villager.VillagerType} can teach you this.",
                ActionText = "Learn",
                OnAction = () => ability.Learn(Player.m_localPlayer),
            };
        }

        /// <summary>
        ///     Resolve the teachable abilities for this villager from its "ability:&lt;id&gt;"
        ///     definition tags. A tag naming an ability that isn't registered (or isn't
        ///     player-teachable) is inconsistent definition state, so it throws rather than
        ///     silently dropping the row — the misconfiguration surfaces immediately.
        /// </summary>
        private static List<IPlayerAbility> TaughtAbilities(VillagerBehaviorBridge villager)
        {
            var result = new List<IPlayerAbility>();
            var def = VillagerRegistry.Get(villager?.VillagerType);
            if (def?.tags == null) return result;

            foreach (var id in TagParser.GetValues(def.tags, "ability"))
            {
                var ability = AttributeScanner.GetAbility(id);
                if (ability == null)
                    throw new System.InvalidOperationException(
                        $"Villager '{villager.VillagerType}' has tag 'ability:{id}' but no " +
                        $"ability with id '{id}' is registered.");
                if (ability is not IPlayerAbility playerAbility)
                    throw new System.InvalidOperationException(
                        $"Villager '{villager.VillagerType}' tag 'ability:{id}' resolves to " +
                        $"'{ability.GetType().Name}', which is not player-teachable (IPlayerAbility).");

                result.Add(playerAbility);
            }

            return result;
        }
    }
}
