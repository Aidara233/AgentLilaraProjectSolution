using System.Linq;
using System.Threading.Tasks;
using AgentCoreProcessor.Database;
using AgentCoreProcessor.Engine.Session;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 会话 Core 管理。按命令来源频道解出 Full 占用会话，查看/切换其活跃 Core。
    /// 用法: /core            → 查看当前频道占用会话的 Core
    ///       /core <name>     → 切换到指定 Core（须在会话类型白名单 AllowedCores 内）
    /// </summary>
    internal class CoreCommand : ICommand
    {
        public string Name => "core";
        public string Description => "会话核心管理 (查看/切换)";
        public PermissionLevel RequiredPermission => PermissionLevel.Admin;

        public Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            var se = context.SystemContext.GetActiveEnginesSnapshot()
                .OfType<SessionEngine>().FirstOrDefault();
            if (se == null)
                return Task.FromResult(CommandResult.Fail("SessionEngine 不可用。"));

            if (!int.TryParse(context.Message.ChannelId, out var cid) || cid <= 0)
                return Task.FromResult(CommandResult.Fail("无法从命令消息解析频道。"));

            var claimer = se.GetFullClaimer(cid);
            if (string.IsNullOrEmpty(claimer))
                return Task.FromResult(CommandResult.Fail($"频道 #{cid} 当前没有 Working 会话占用，无法操作核心。"));

            var parts = args.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                var rs = se.GetSession(claimer) as RuntimeSession;
                var current = rs?.ActiveCoreConfig ?? "?";
                var allowed = rs != null ? string.Join(", ", rs.AllowedCoreConfigs) : "?";
                return Task.FromResult(CommandResult.Ok(
                    $"会话 {claimer} 当前核心：{current}\n可用核心：{allowed}"));
            }

            var (ok, msg) = se.SwitchSessionCore(claimer, parts[0]);
            return Task.FromResult(ok ? CommandResult.Ok(msg) : CommandResult.Fail(msg));
        }
    }
}
