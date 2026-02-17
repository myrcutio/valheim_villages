using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ValheimVillages.Patches
{
    /// <summary>
    /// Registers a Hugin tutorial for work orders and triggers it when the player
    /// first obtains a work order. Uses Player.m_shownTutorials for "show once" tracking.
    /// </summary>
    public static class WorkOrderTutorial
    {
        public const string TutorialName = "vv_workorder";
        private const string Topic = "Work orders";
        private const string Text =
            "Place work orders in a chest. Villagers who use that station will find them and craft the requested items.";

        private static bool s_registered;

        [HarmonyPatch(typeof(Tutorial), "Awake")]
        [HarmonyPostfix]
        public static void Tutorial_Awake_Postfix(Tutorial __instance)
        {
            if (__instance == null || s_registered) return;
            RegisterTutorial(__instance);
            s_registered = true;
        }

        private static void RegisterTutorial(Tutorial tutorial)
        {
            if (tutorial.m_texts == null) return;
            var list = tutorial.m_texts;
            foreach (var t in list)
            {
                var nameField = t.GetType().GetField("m_name", BindingFlags.Public | BindingFlags.Instance);
                if (nameField != null && nameField.GetValue(t) as string == TutorialName)
                    return;
            }

            var ttType = typeof(Tutorial).Assembly.GetType("Tutorial+TutorialText");
            if (ttType == null) return;
            var entry = System.Activator.CreateInstance(ttType);
            if (entry == null) return;

            SetField(entry, ttType, "m_name", TutorialName);
            SetField(entry, ttType, "m_topic", Topic);
            SetField(entry, ttType, "m_text", Text);
            SetField(entry, ttType, "m_label", "");
            SetField(entry, ttType, "m_isMunin", false);
            SetField(entry, ttType, "m_globalKeyTrigger", "");
            SetField(entry, ttType, "m_tutorialTrigger", "");

            list.Add((Tutorial.TutorialText)entry);
            Plugin.Log?.LogInfo($"[Valheim Villages] Registered work order tutorial: {TutorialName}");
        }

        private static void SetField(object obj, System.Type type, string name, object value)
        {
            var f = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (f != null)
                f.SetValue(obj, value);
        }

        /// <summary>
        /// Call when the player has just received a work order. Shows the tutorial
        /// only if they have not seen it before (Player.m_shownTutorials).
        /// </summary>
        public static void MaybeShowWorkOrderTutorial(Player player)
        {
            if (player == null) return;
            player.ShowTutorial(TutorialName, false);
        }
    }
}
