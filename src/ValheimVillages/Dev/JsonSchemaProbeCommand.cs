using System.Collections.Generic;
using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Schemas;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Why villager definitions must NOT be read with Unity's JsonUtility.
    ///
    ///     <para>Measured, not assumed: <c>vv_defs</c> reports every
    ///     <c>List&lt;custom class&gt;</c> field of a definition as empty (stationRecipes,
    ///     workbenches, productions) while <c>List&lt;string&gt;</c> fields parse — so the
    ///     Lumberjack's wood recipes and the Farmer's flour recipe never register. The load
    ///     path is NOT the cause: it reproduces identically with the mod cold-loaded as a real
    ///     plugin and hot-loaded by ScriptEngine.</para>
    ///
    ///     <para>What this proved: <c>ToJson</c> of a definition built IN CODE omits those
    ///     fields entirely, and a lookalike type declared right here behaves the same — so
    ///     Unity's serializer never sees a <c>List&lt;custom class&gt;</c> field in a plugin
    ///     assembly at all, and no amount of fixing the JSON would have helped. The loader
    ///     uses Newtonsoft (which ships with the game, on the dedicated server too) instead;
    ///     this command stays as the check that says so, because the failure is SILENT —
    ///     empty lists, no exception, no warning.</para>
    /// </summary>
    public static class JsonSchemaProbeCommand
    {
        private const string Sample =
            "{\"type\":\"Probe\"," +
            "\"workStations\":[\"a\",\"b\"]," +
            "\"stationRecipes\":[{\"output\":\"Wood\",\"outputAmount\":2," +
            "\"input\":\"BeechSeeds\",\"inputAmount\":1,\"minStationLevel\":1}]," +
            "\"workbenches\":[{\"name\":\"Workbench\",\"minLevel\":1}]}";

        [DevCommand("Probe Unity JSON handling of villager-definition schemas", Name = "vv_jsonprobe")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            var sb = new StringBuilder();

            // 1. Read a hand-written document into the real schema.
            var def = JsonUtility.FromJson<VillagerDef>(Sample);
            sb.AppendLine("FromJson<VillagerDef>(inline sample):");
            sb.AppendLine($"  type='{def?.type}' workStations={def?.workStations?.Count} " +
                          $"stationRecipes={def?.stationRecipes?.Count} workbenches={def?.workbenches?.Count}");
            if (def?.stationRecipes != null)
                foreach (var r in def.stationRecipes)
                    sb.AppendLine($"    out='{r?.output}' x{r?.outputAmount} in='{r?.input}'");

            // 2. Round-trip an object built in code. If the list comes back as [] the
            //    serializer is not walking the field at all, which is a TYPE problem and no
            //    amount of fixing the JSON will help.
            var built = new VillagerDef { type = "Probe" };
            built.workStations.Add("a");
            built.stationRecipes.Add(new StationRecipe { output = "Wood", input = "BeechSeeds" });
            sb.AppendLine("ToJson(VillagerDef built in code):");
            sb.AppendLine("  " + JsonUtility.ToJson(built));

            // 3. The same shape declared HERE, outside Schemas, to separate "Unity cannot do
            //    this" from "something about those particular types".
            var local = JsonUtility.FromJson<LocalDef>(Sample);
            sb.AppendLine("FromJson<LocalDef>(same sample):");
            sb.AppendLine($"  workStations={local?.workStations?.Count} " +
                          $"stationRecipes={local?.stationRecipes?.Count}");
            var localBuilt = new LocalDef();
            localBuilt.stationRecipes.Add(new LocalRecipe { output = "Wood" });
            sb.AppendLine("  ToJson(LocalDef built in code): " + JsonUtility.ToJson(localBuilt));

            var output = "[vv_jsonprobe]\n" + sb;
            global::Console.instance?.Print(output);
            Plugin.Log?.LogInfo(output);
        }

        [System.Serializable]
        private class LocalDef
        {
            public string type = "";
            public List<string> workStations = new();
            public List<LocalRecipe> stationRecipes = new();
        }

        [System.Serializable]
        private class LocalRecipe
        {
            public string output = "";
            public int outputAmount = 1;
            public string input = "";
            public int inputAmount = 1;
            public int minStationLevel = 1;
        }
    }
}
