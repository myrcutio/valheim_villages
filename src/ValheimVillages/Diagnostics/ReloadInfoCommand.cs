using System;
using System.IO;
using ValheimVillages.Attributes;

namespace ValheimVillages.Diagnostics
{
    /// <summary>
    ///     Dev console command to confirm the ScriptEngine hot-reload pipeline.
    ///     Reports when the currently-loaded assembly was loaded (re-initialized
    ///     on every reload), the mtime and size of the DLL it was loaded from, and
    ///     WHERE that file is. Run it, rebuild, run it again: if "loaded" advances,
    ///     the reload fired.
    ///
    ///     <para>The path and size are there because "I deployed it and it still isn't
    ///     running" is the most expensive question this command gets asked, and a timestamp
    ///     alone cannot answer it. A server that loads a stale DLL and one that loads a
    ///     shadowing copy from a second plugin directory look identical without the path;
    ///     with it, comparing against the build on disk takes one glance. Measured: a
    ///     dedicated server restarted twice on a DLL the operator believed they had
    ///     replaced.</para>
    /// </summary>
    internal static class ReloadInfoCommand
    {
        [DevCommand("Report when this assembly was last (re)loaded by ScriptEngine", Name = "vv_reloadinfo")]
        public static void Report(Terminal.ConsoleEventArgs args)
        {
            // All timestamps are UTC so readings from different processes/hosts (e.g. a
            // dedicated server and a client on different local zones) are directly comparable.
            var loaded = Plugin.AssemblyLoadedAt;
            var ageSeconds = (DateTime.UtcNow - loaded).TotalSeconds;

            var dllMtime = "n/a";
            var dllSize = "n/a";
            var dllPath = "n/a";
            try
            {
                var location = typeof(Plugin).Assembly.Location;
                dllPath = string.IsNullOrEmpty(location) ? "(in-memory)" : location;
                if (!string.IsNullOrEmpty(location) && File.Exists(location))
                {
                    dllMtime = File.GetLastWriteTimeUtc(location).ToString("HH:mm:ss") + "Z";
                    dllSize = new FileInfo(location).Length.ToString();
                }
            }
            catch (Exception e)
            {
                dllMtime = $"error: {e.Message}";
            }

            var msg =
                $"[Reload] {Plugin.PluginName} v{Plugin.PluginVersion} — assembly loaded " +
                $"{loaded:HH:mm:ss}Z ({ageSeconds:F0}s ago), " +
                $"hotReload={Plugin.LastLoadWasHotReload}, dllMtime={dllMtime}, " +
                $"dllSize={dllSize}\n  loaded from: {dllPath}";

            Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}