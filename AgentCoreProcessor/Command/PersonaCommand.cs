using System;
using System.IO;
using System.Threading.Tasks;
using AgentCoreProcessor.Config;
using AgentCoreProcessor.Database;

namespace AgentCoreProcessor.Command
{
    /// <summary>
    /// 查看/追加人设文件。
    /// 用法: /persona — 查看 | /persona append <文本> — 追加
    /// </summary>
    internal class PersonaCommand : ICommand
    {
        public string Name => "persona";
        public string Description => "人设管理 (查看/追加 Persona.txt)";
        public PermissionLevel RequiredPermission => PermissionLevel.Admin;

        private static string PersonaPath => Path.Combine(PathConfig.CoreConfigPath, "Persona.txt");

        public Task<CommandResult> ExecuteAsync(string args, CommandContext context)
        {
            var parts = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0 || parts[0].ToLower() == "show")
                return Task.FromResult(Show());

            if (parts[0].ToLower() == "append" && parts.Length > 1)
                return Task.FromResult(Append(parts[1]));

            return Task.FromResult(CommandResult.Fail("用法: /persona [show] | /persona append <文本>"));
        }

        private static CommandResult Show()
        {
            if (!File.Exists(PersonaPath))
                return CommandResult.Ok("Persona.txt 不存在。");
            var content = File.ReadAllText(PersonaPath);
            if (string.IsNullOrWhiteSpace(content))
                return CommandResult.Ok("Persona.txt 为空。");
            // 截断过长内容
            if (content.Length > 1500)
                content = content[..1500] + "\n... (已截断)";
            return CommandResult.Ok($"=== Persona.txt ===\n{content}");
        }

        private static CommandResult Append(string text)
        {
            File.AppendAllText(PersonaPath, "\n" + text.Trim());
            return CommandResult.Ok($"已追加到 Persona.txt。");
        }
    }
}
