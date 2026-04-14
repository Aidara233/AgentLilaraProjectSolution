using System.Collections.Generic;
using System.Linq;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 命令注册表。管理所有已注册的框架命令。
    /// </summary>
    internal static class CommandRegistry
    {
        private static readonly Dictionary<string, ICommand> _commands;

        static CommandRegistry()
        {
            var list = new ICommand[]
            {
                new HelpCommand(),
                new StatusCommand(),
                new WhoAmICommand(),
                new ReloadCommand(),
            };
            _commands = list.ToDictionary(c => c.Name.ToLowerInvariant());
        }

        /// <summary>按名称查找命令（大小写不敏感）。</summary>
        public static ICommand? Get(string name)
            => _commands.TryGetValue(name.ToLowerInvariant(), out var cmd) ? cmd : null;

        /// <summary>所有已注册命令。</summary>
        public static IReadOnlyDictionary<string, ICommand> All => _commands;
    }
}
