using System.Threading.Tasks;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Command
{
    /// <summary>热重载配置。用法: /reload adapter [平台名]</summary>
    internal class ReloadCommand : ICommand
    {
        public string Name => "reload";
        public string Description => "热重载配置 (用法: /reload adapter [平台名])";
        public PermissionLevel RequiredPermission => PermissionLevel.Admin;

        public async Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            var parts = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
                return CommandResult.Fail("用法: /reload adapter [平台名]");

            return parts[0].ToLower() switch
            {
                "adapter" => await ReloadAdapterAsync(parts, context),
                _ => CommandResult.Fail($"未知重载目标: {parts[0]}，可选: adapter")
            };
        }

        private static async Task<CommandResult> ReloadAdapterAsync(string[] parts, CommandContext context)
        {
            var adapters = context.SystemContext.Adapters;

            if (parts.Length < 2)
            {
                var platforms = adapters.GetRegisteredPlatforms();
                return CommandResult.Fail($"请指定平台名，已注册: {string.Join(", ", platforms)}");
            }

            var platform = parts[1];
            var success = await adapters.ReloadAdapterAsync(platform);
            return success
                ? CommandResult.Ok($"适配器 [{platform}] 配置已重载。")
                : CommandResult.Fail($"未找到平台 [{platform}] 的适配器。");
        }
    }
}
