using System.Linq;
using System.Text;
using ValheimVillages.Attributes;

namespace ValheimVillages.Settings
{
    /// <summary>
    ///     In-game help command. Type <c>vv</c>, <c>vv help</c>, or <c>vv --help</c>
    ///     in the Valheim developer console to print every dev command this mod
    ///     has registered (name + description), sorted alphabetically.
    /// </summary>
    internal static class HelpCommand
    {
        [DevCommand("List all Valheim Villages dev commands (try also: vv help, vv --help)", Name = "vv")]
        public static void PrintHelp(Terminal.ConsoleEventArgs args)
        {
            var commands = AttributeScanner.GetRegisteredDevCommands()
                .OrderBy(c => c.Name)
                .ToList();

            if (commands.Count == 0)
            {
                Console.instance?.Print("[vv] No dev commands registered.");
                return;
            }

            var width = commands.Max(c => c.Name.Length);
            var sb = new StringBuilder();
            sb.Append("[vv] ").Append(commands.Count).Append(" dev command(s):");
            Console.instance?.Print(sb.ToString());

            foreach (var (name, description) in commands)
            {
                sb.Clear();
                sb.Append("  ").Append(name.PadRight(width)).Append("  ").Append(description ?? "");
                Console.instance?.Print(sb.ToString());
            }
        }
    }
}