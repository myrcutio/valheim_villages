using System;
using System.Reflection;
using System.Text;
using UnityEngine;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.Registry;

namespace ValheimVillages.Dev
{
    /// <summary>
    ///     Diagnostic: what each villager definition ACTUALLY contains at runtime, next to what
    ///     its embedded JSON says on disk.
    ///
    ///     <para>Exists because "my work order isn't in the list" has two very different causes
    ///     that look identical from the game: the JSON never made it into the loaded assembly
    ///     (stale build / wrong deploy path), or it did and
    ///     <see cref="JsonUtility" /> dropped part of it on the floor. Printing the raw resource
    ///     text alongside the deserialized object separates those in one call — and unlike a
    ///     load-time log line it survives the log buffer rotating, which on a busy session
    ///     evicts the whole startup sequence within minutes.</para>
    /// </summary>
    public static class VillagerDefDumpCommand
    {
        [DevCommand("Dump villager definitions: deserialized vs raw embedded JSON. " +
                    "Usage: vv_defs [type]", Name = "vv_defs")]
        public static void Dump(Terminal.ConsoleEventArgs args)
        {
            var filter = args.Length >= 2 ? args[1] : null;
            var sb = new StringBuilder();

            foreach (var kv in VillagerRegistry.Definitions)
            {
                var def = kv.Value;
                if (def == null) continue;
                if (filter != null && !string.Equals(def.type, filter, StringComparison.OrdinalIgnoreCase))
                    continue;

                sb.AppendLine($"- {def.type} station='{def.stationName}'");

                // Deserialized view.
                var sr = def.stationRecipes;
                sb.Append("    stationRecipes(parsed) = ");
                if (sr == null)
                {
                    sb.AppendLine("null");
                }
                else
                {
                    sb.AppendLine(sr.Count.ToString());
                    foreach (var r in sr)
                        sb.AppendLine(
                            $"      out='{r?.output}' x{r?.outputAmount} in='{r?.input}' x{r?.inputAmount} " +
                            $"lvl={r?.minStationLevel} physical='{r?.physicalStation}'");
                }

                sb.AppendLine($"    workStations(parsed)   = {def.workStations?.Count.ToString() ?? "null"}");
                sb.AppendLine($"    workbenches(parsed)    = {def.workbenches?.Count.ToString() ?? "null"}");
                sb.AppendLine($"    productions(parsed)    = {def.productions?.Count.ToString() ?? "null"}");

                // Raw embedded view — is the JSON inside THIS assembly even current?
                sb.AppendLine($"    raw JSON: {DescribeRaw(def.type)}");
            }

            if (sb.Length == 0)
                sb.AppendLine(filter != null
                    ? $"no villager definition named '{filter}'"
                    : "no villager definitions loaded");

            Print("[vv_defs]\n" + sb);
        }

        /// <summary>
        ///     Crude but decisive read of the embedded JSON for a type: does the resource in the
        ///     LOADED assembly contain a stationRecipes block, and what outputs does it name?
        ///     Deliberately text-level — the whole point is to see the bytes that shipped,
        ///     without going back through the deserializer being investigated.
        /// </summary>
        private static string DescribeRaw(string type)
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                foreach (var name in assembly.GetManifestResourceNames())
                {
                    if (!name.Contains(".Villager.Registry.Definitions.")) continue;
                    if (!name.EndsWith("." + type.ToLowerInvariant() + ".json",
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    using var stream = assembly.GetManifestResourceStream(name);
                    if (stream == null) return "resource stream null";
                    using var reader = new System.IO.StreamReader(stream);
                    var text = reader.ReadToEnd();

                    var idx = text.IndexOf("\"stationRecipes\"", StringComparison.Ordinal);
                    if (idx < 0) return $"{text.Length}b, NO stationRecipes key";

                    // Report the outputs named inside the block so a stale resource is obvious.
                    var end = text.IndexOf(']', idx);
                    var block = end > idx ? text.Substring(idx, end - idx) : text.Substring(idx);
                    var outputs = new StringBuilder();
                    var at = 0;
                    while (true)
                    {
                        at = block.IndexOf("\"output\"", at, StringComparison.Ordinal);
                        if (at < 0) break;
                        var q1 = block.IndexOf('"', block.IndexOf(':', at) + 1);
                        var q2 = q1 >= 0 ? block.IndexOf('"', q1 + 1) : -1;
                        if (q1 < 0 || q2 < 0) break;
                        if (outputs.Length > 0) outputs.Append(", ");
                        outputs.Append(block.Substring(q1 + 1, q2 - q1 - 1));
                        at = q2;
                    }

                    return $"{text.Length}b, stationRecipes outputs = [{outputs}]";
                }

                return "no embedded resource for this type";
            }
            catch (Exception ex)
            {
                return $"read failed: {ex.Message}";
            }
        }

        private static void Print(string s)
        {
            global::Console.instance?.Print(s);
            Plugin.Log?.LogInfo(s);
        }
    }
}
