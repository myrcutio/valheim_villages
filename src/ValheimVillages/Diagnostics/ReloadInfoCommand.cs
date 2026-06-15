using System;
using System.IO;
using ValheimVillages.Attributes;

namespace ValheimVillages.Diagnostics
{
    /// <summary>
    ///     Dev console command to confirm the ScriptEngine hot-reload pipeline.
    ///     Reports when the currently-loaded assembly was loaded (re-initialized
    ///     on every reload) and the mtime of the DLL it was loaded from. Run it,
    ///     rebuild, run it again: if "loaded" advances, the reload fired.
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
            try
            {
                var location = typeof(Plugin).Assembly.Location;
                if (!string.IsNullOrEmpty(location) && File.Exists(location))
                    dllMtime = File.GetLastWriteTimeUtc(location).ToString("HH:mm:ss") + "Z";
            }
            catch (Exception e)
            {
                dllMtime = $"error: {e.Message}";
            }

            var msg =
                $"[Reload] assembly loaded {loaded:HH:mm:ss}Z ({ageSeconds:F0}s ago), " +
                $"hotReload={Plugin.LastLoadWasHotReload}, dllMtime={dllMtime}";

            Console.instance?.Print(msg);
            Plugin.Log?.LogInfo(msg);
        }
    }
}