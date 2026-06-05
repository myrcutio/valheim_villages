using System.Collections;
using System.Reflection;
using System.Text;
using UnityEngine.AI;
using ValheimVillages.Attributes;
using ValheimVillages.Villager.AI.Pathfinding;

namespace ValheimVillages.Diagnostics
{
    /// <summary>
    ///     Dumps the live NavMesh villager-agent registration state (slot 31) so we
    ///     can see, across a hot reload, whether the agent slot is intact, how many
    ///     m_agentType==31 entries exist (duplicates accumulating?), and whether our
    ///     captured agentTypeID still resolves to valid Unity NavMesh settings.
    /// </summary>
    internal static class AgentDumpCommand
    {
        [DevCommand("Dump NavMesh villager agent registration state (slot 31)", Name = "vv_agent_dump")]
        public static void Run(Terminal.ConsoleEventArgs args)
        {
            var sb = new StringBuilder();
            sb.AppendLine(
                $"[vv_agent_dump] IsRegistered={VillagerAgentType.IsRegistered} " +
                $"UnityAgentTypeID={VillagerAgentType.UnityAgentTypeID}");

            // Unity-side NavMesh settings (native registration, survives script reload).
            var count = NavMesh.GetSettingsCount();
            sb.AppendLine($"  Unity NavMesh settings count={count}");
            for (var i = 0; i < count; i++)
            {
                var s = NavMesh.GetSettingsByIndex(i);
                sb.AppendLine(
                    $"    unity[{i}] agentTypeID={s.agentTypeID} radius={s.agentRadius:F2} " +
                    $"height={s.agentHeight:F2} slope={s.agentSlope:F0} climb={s.agentClimb:F2}");
            }

            // What does OUR captured id resolve to right now?
            var ours = NavMesh.GetSettingsByID(VillagerAgentType.UnityAgentTypeID);
            sb.AppendLine(
                $"  GetSettingsByID({VillagerAgentType.UnityAgentTypeID}) -> " +
                $"agentTypeID={ours.agentTypeID} radius={ours.agentRadius:F2} " +
                $"height={ours.agentHeight:F2} slope={ours.agentSlope:F0} climb={ours.agentClimb:F2}");

            // Valheim's managed agent-settings list (reset/rebuilt across reload?).
            var pf = global::Pathfinding.instance;
            if (pf == null)
            {
                sb.AppendLine("  Pathfinding.instance NULL");
                Emit(sb);
                return;
            }

            var flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var list = typeof(global::Pathfinding).GetField("m_agentSettings", flags)?.GetValue(pf) as IList;
            var asType = typeof(global::Pathfinding).GetNestedType("AgentSettings", BindingFlags.NonPublic);
            var typeF = asType?.GetField("m_agentType");
            var buildF = asType?.GetField("m_build");
            sb.AppendLine($"  Pathfinding.m_agentSettings count={list?.Count}");
            if (list != null && typeF != null && buildF != null)
                for (var i = 0; i < list.Count; i++)
                {
                    var ag = list[i];
                    if (ag == null)
                    {
                        sb.AppendLine($"    idx[{i}] null");
                        continue;
                    }

                    var atype = typeF.GetValue(ag);
                    var bs = (NavMeshBuildSettings)buildF.GetValue(ag);
                    sb.AppendLine(
                        $"    idx[{i}] m_agentType={atype} agentTypeID={bs.agentTypeID} " +
                        $"radius={bs.agentRadius:F2} climb={bs.agentClimb:F2}");
                }

            Emit(sb);
        }

        private static void Emit(StringBuilder sb)
        {
            Console.instance?.Print(sb.ToString());
            Plugin.Log?.LogWarning(sb.ToString());
        }
    }
}
