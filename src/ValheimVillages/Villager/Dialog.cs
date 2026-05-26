using System.Collections.Generic;
using UnityEngine;
using ValheimVillages.Schemas;

namespace ValheimVillages.Villager
{
    public class Dialog
    {
        /// <summary>
        ///     Configure VillagerTalk dialog lines from the villager definition.
        ///     Only replaces lists that have entries in the JSON.
        /// </summary>
        public static void ConfigureDialog(GameObject npcObject, VillagerDef villagerDef)
        {
            if (villagerDef == null) return;

            var talk = npcObject.GetComponent<VillagerTalk>();
            if (talk == null) return;

            if (villagerDef.randomTalk?.Count > 0)
                talk.randomTalk = new List<string>(villagerDef.randomTalk);

            if (villagerDef.randomGreets?.Count > 0)
                talk.randomGreets = new List<string>(villagerDef.randomGreets);

            if (villagerDef.randomGoodbye?.Count > 0)
                talk.randomGoodbye = new List<string>(villagerDef.randomGoodbye);

            Plugin.Log?.LogInfo($"Configured dialog for {villagerDef.displayName}");
        }
    }
}