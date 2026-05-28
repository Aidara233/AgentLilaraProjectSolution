using AgentLilara.PluginSDK.Dice;
using AgentLilara.PluginSDK.Services;

namespace Plugin.FileTools;

internal static class FileDiceFaces
{
    private static readonly Random Rng = new();

    public static void Register(IDiceRegistry registry, string workspaceDir)
    {
        registry.Register(new DiceFace
        {
            Id = "file:random_snippet",
            Label = "随机文件片段",
            Category = "external",
            Weight = 0.5,
            Roll = ct =>
            {
                var files = Directory.GetFiles(workspaceDir, "*.*", SearchOption.AllDirectories);
                if (files.Length == 0)
                    return Task.FromResult(NoResult("workspace 中没有文件"));

                // 最多试 3 次找可读的文本文件
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    var path = files[Rng.Next(files.Length)];
                    try
                    {
                        var lines = File.ReadAllLines(path);
                        if (lines.Length == 0) continue;
                        var start = Rng.Next(lines.Length);
                        var snippet = string.Join("\n", lines.Skip(start).Take(15));
                        var relPath = Path.GetRelativePath(workspaceDir, path);
                        return Task.FromResult(new DiceResult
                        {
                            Meta = $"[file | {relPath} | {File.GetLastWriteTime(path):yyyy-MM-dd}]",
                            Content = snippet,
                            FollowUp = ""
                        });
                    }
                    catch { /* 文件不可读，重试 */ }
                }

                return Task.FromResult(NoResult("未找到可读的文本文件"));
            }
        });
    }

    private static DiceResult NoResult(string reason) => new()
    {
        Meta = "[file | - | -]",
        Content = $"({reason})",
        FollowUp = ""
    };
}
